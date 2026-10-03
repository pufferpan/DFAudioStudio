using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using DFAudioStudio.Core.Data;
using DFAudioStudio.Core.Models;
using DFAudioStudio.Core.Services;
// 只用来「清空某一条的旧识别文本」：Core 没有对应公开方法，这里复用 Core 已经引用的
// Microsoft.Data.Sqlite（传递依赖，不新增包、不改 csproj），对同一个库开一个短连接做最小 UPDATE。
using Microsoft.Data.Sqlite;

namespace DFAudioStudio.App.Services;

/// <summary>
/// 全局单例服务容器：一份配置（AppSettings）、一个索引库（IndexDb）、一个扫描器（Scanner）、一个识别队列（TranscriptionQueue）。
/// 注意：<see cref="Log"/> 可能在后台线程触发，订阅方必须自行切回 UI 线程（界面里统一用 UiDispatch）。
/// </summary>
public sealed class AppServices
{
    private static readonly Lazy<AppServices> LazyInstance = new(() => new AppServices());

    public static AppServices Current => LazyInstance.Value;

    /// <summary>应用配置。整个进程里始终是同一个实例，设置页只改属性再 Save()。</summary>
    public AppSettings Settings { get; }

    /// <summary>SQLite 索引库（内部连接池 + WAL，可被扫描线程 / 识别线程 / UI 同时使用）。</summary>
    public IndexDb Db { get; }

    /// <summary>全量 / 增量扫描器。</summary>
    public Scanner Scanner { get; }

    /// <summary>
    /// 识别队列：**默认不在启动时启动**，只在用户点「开始识别（手动）」或设置里显式勾选「启动时自动开始识别」时才启动。
    /// </summary>
    public TranscriptionQueue Queue { get; }

    /// <summary>Whisper 模型存放目录：exe\models。</summary>
    public string ModelsDir { get; }

    /// <summary>全局日志（界面「概览」页订阅显示）。回调可能在任意线程。</summary>
    public event Action<string>? Log;

    private AppServices()
    {
        Settings = AppSettings.Load();
        // 模型目录跟设置走（默认 <数据根>\models；本机已改到 E 盘，避免 C 盘空间不足）
        ModelsDir = Settings.ModelsDir;
        Db = new IndexDb(Settings.DbPath);
        Scanner = new Scanner(Db);

        // 这里传的是「工厂方法」，构造队列本身不会加载模型；
        // 只有队列的后台工作线程在 Start() 之后才会调用 CreateTranscriber()。
        Queue = new TranscriptionQueue(Db, Settings, CreateTranscriber);
        Queue.ProgressChanged += OnQueueProgress;
    }

    // ── 日志 ────────────────────────────────────────────────────────────────

    public void LogInfo(string message) => WriteLog(message);

    public void LogWarn(string message) => WriteLog("[警告] " + message);

    private static readonly object LogFileGate = new();

    /// <summary>日志文件路径：&lt;数据根&gt;\logs\app-yyyyMMdd.log（方便排查看不到界面的问题）。</summary>
    public string LogFilePath
    {
        get
        {
            try
            {
                string dir = Path.Combine(Settings.DataDir, "logs");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, $"app-{DateTime.Now:yyyyMMdd}.log");
            }
            catch { return Path.Combine(Path.GetTempPath(), "DFAudioStudio-app.log"); }
        }
    }

    private void WriteLog(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        try { Log?.Invoke(line); } catch { /* 日志订阅方异常不能影响主流程 */ }
        System.Diagnostics.Debug.WriteLine(line);
        try
        {
            lock (LogFileGate)
                File.AppendAllText(LogFilePath, line + Environment.NewLine, System.Text.Encoding.UTF8);
        }
        catch { /* 写日志失败也不能影响主流程 */ }
    }

    private void OnQueueProgress(QueueProgress p)
    {
        // 只记录有意义的状态变更；「识别中: xxx」这种逐条消息不写日志面板，避免日志被刷爆
        if (string.IsNullOrWhiteSpace(p.Message)) return;
        if (p.Message.StartsWith("识别中", StringComparison.Ordinal)) return;
        LogInfo($"队列[{p.State}]：{p.Message}（完成 {p.Done} / 失败 {p.Failed} / 剩余 {p.Remaining}）");
    }

    // ── 识别器工厂 ──────────────────────────────────────────────────────────

    /// <summary>
    /// **只在识别队列的后台线程里被调用**（用户点「开始识别」之后），启动路径绝不会走到这里：
    /// Whisper 模型有几百 MB，加载一次要几秒，不能拖慢软件打开。模型路径为空（或文件不存在）时返回 null，
    /// 队列会给出「未配置模型」的提示。
    /// </summary>
    public ITranscriber? CreateTranscriber()
    {
        try
        {
            if (!IsModelReady) return null;
            LogInfo($"正在加载识别模型 {Path.GetFileName(Settings.ModelPath)}（首次加载会慢一点）…");
            return new WhisperTranscriber(Settings.ModelPath, Math.Clamp(Settings.Parallelism, 1, 8));
        }
        catch (Exception ex)
        {
            LogWarn("Whisper 模型加载失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>是否已经选好可用的 ggml 模型。</summary>
    public bool IsModelReady => !string.IsNullOrWhiteSpace(Settings.ModelPath) && File.Exists(Settings.ModelPath);

    // ── 常用动作 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 把一条音频重新丢回识别队列：**状态重置为「未处理」+ 清空旧的识别文本**，然后（队列没在跑时）启动队列。
    ///
    /// 关键点：任何时候都能调用 —— 不要求队列正在 Running。之前只做 Db.UpdateStatus + Queue.Start()，
    /// 于是「停止之后 / 队列刚跑完那一瞬」点重新识别会静默没反应（见 <see cref="EnsureQueueRunning"/> 的说明）；
    /// 而且旧文本一直留着，界面看上去像「没生效」。
    /// </summary>
    public void RequestReTranscribe(AudioItem item, bool autostart = true)
    {
        if (item is null) return;

        ResetItemForReTranscribe(item);

        if (!autostart) return;
        EnsureQueueRunning("重新识别");
    }

    /// <summary>把一条音频重置为「未处理」并清空旧的识别文本（库里的这一条 + 传进来的内存对象）。</summary>
    private void ResetItemForReTranscribe(AudioItem item)
    {
        // ① Status → 0（未处理），顺带清掉上次的失败原因：这条会再次被 NextPending 取走。
        Db.UpdateStatus(item.Id, TranscriptStatus.None, "");

        // ② 清空旧的识别文本。
        //    Core 里没有「只清一条文本」的公开方法：ResetStatuses 是整库操作，
        //    SaveTranscript 会把状态写成 Done、并且把结果同步给同名同大小的副本（反而会让这条被去重规则排除）。
        //    所以这里用一条最小的 UPDATE 只改这一条的两列文本，不改任何既有 API 的行为。
        int cleared = ClearTranscriptText(item.Id);

        // ③ 内存里的这一份同步清掉：界面立刻不该再显示旧文本（列表下次查询也会从库里拿到空文本）。
        item.Status = TranscriptStatus.None;
        item.Transcript = "";
        item.SegmentsJson = "";
        item.LastError = "";

        LogInfo(cleared > 0
            ? $"已把「{item.FileName}」重新加入识别队列（状态＝未处理，旧的识别文本已清空）"
            : $"已把「{item.FileName}」重新加入识别队列（状态＝未处理）");
    }

    /// <summary>只清空某一条的识别文本（不碰状态、不碰同名副本），返回影响行数；失败只记日志，不影响重新入队。</summary>
    private int ClearTranscriptText(long id)
    {
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Db.DbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                Pooling = true
            }.ToString());
            conn.Open();

            // 与识别线程并发时要等锁（Core 的每个连接也设了同样的超时）
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout=8000;";
                pragma.ExecuteNonQuery();
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE items SET Transcript='', SegmentsJson='', LastError='', UpdatedAt=$u WHERE Id=$id;";
            cmd.Parameters.AddWithValue("$u", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            LogWarn("清空旧识别文本失败（状态已经重置为未处理，重跑结果会覆盖旧文本）：" + ex.Message);
            return 0;
        }
    }

    /// <summary>
    /// 确保识别队列在跑（任何一个手动入口都走这里，避免「点了没反应」）。
    ///
    /// 为什么要补偿重试：Core 的 <c>TranscriptionQueue.Start()</c> 里有这么一段
    /// <c>if (_worker is { IsCompleted: false }) { Resume(); return; }</c>，
    /// 而 <c>Resume()</c> 只在 Paused 时才生效 —— 也就是说，当**上一个 worker 任务还没结束、但状态不是 Paused** 时
    /// （典型：刚点过「停止」，worker 还在收尾；或者队列刚跑完、worker 还没被标记完成），
    /// Start() 会退化成空操作。用户看到的就是「重新识别点了没反应」。
    /// Core 的既有 API 不能改，只能用短延时补偿重试兜住这个窗口。
    /// </summary>
    public void EnsureQueueRunning(string reason)
    {
        try
        {
            if (Queue.IsRunning || Queue.IsPaused)
            {
                Queue.Start();   // 运行中：新入队的条目会在下一批被取走；暂停中：Start → Resume 继续跑
                return;
            }

            if (!IsModelReady)
            {
                // 不静默失败：明确告诉用户缺什么。
                LogWarn($"「{reason}」已置为待识别，但尚未配置 Whisper 模型：请到「设置」里选择或下载 ggml 模型后再点「开始识别」。");
                return;
            }

            Queue.Start();
            ScheduleQueueStartRetry(reason, attemptsLeft: 4);
        }
        catch (Exception ex)
        {
            LogWarn($"启动识别队列失败（{reason}）：" + ex.Message);
        }
    }

    /// <summary>补偿重试：等上一个 worker 收尾之后再把队列真正拉起来；已经在跑就立刻停。</summary>
    private void ScheduleQueueStartRetry(string reason, int attemptsLeft)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < attemptsLeft; i++)
                {
                    await Task.Delay(600).ConfigureAwait(false);

                    if (Queue.IsRunning || Queue.IsPaused) return;              // 已经真的跑起来了
                    if (Queue.State == QueueState.Error) return;                // 明显出错就不要反复戳
                    if (Queue.State == QueueState.Finished && !HasPendingWork()) return;   // 确实没活可干

                    LogWarn($"识别队列似乎没有启动（上一个任务还在收尾），重试启动第 {i + 1} 次：{reason}");
                    Queue.Start();
                }
            }
            catch (Exception ex)
            {
                LogWarn("重试启动识别队列失败：" + ex.Message);
            }
        });
    }

    /// <summary>库里还有没有待识别的条目（重试判断用；查库失败就当作有，宁可多试一次）。</summary>
    private bool HasPendingWork()
    {
        try { return Db.PendingCount(Settings.TranscribeMusic, Math.Max(0, Settings.MinDurationSec)) > 0; }
        catch { return true; }
    }

    /// <summary>队列状态的中文短名（界面状态栏文案用：运行中 / 已暂停 / 空闲）。</summary>
    public string QueueStateText => Queue.State switch
    {
        QueueState.Running => "运行中",
        QueueState.Paused => "已暂停",
        QueueState.Error => "出错",
        _ => "空闲"
    };

    /// <summary>把所有失败项重置为未处理并重新开始识别。</summary>
    public void RetryAllFailed(bool autostart = true)
    {
        Db.ResetStatuses(onlyFailed: true);
        LogInfo("已重置全部失败任务，重新加入识别队列");
        if (autostart) EnsureQueueRunning("重试失败项");
    }

    /// <summary>
    /// 启动时是否开始识别：**只有设置里显式勾选「启动时自动开始识别」才会启动**（默认关闭，改成手动识别）。
    /// 无论勾没勾，这里都不加载 Whisper 模型：模型只在队列后台线程里、真正要识别时才创建。
    /// </summary>
    public void TryAutoStartTranscription()
    {
        try
        {
            if (!Settings.AutoStartTranscription)
            {
                LogInfo("未开启自动识别（默认手动）：启动不加载模型、不启动识别队列；需要识别时点「开始识别」。");
                return;
            }

            if (!IsModelReady)
            {
                LogInfo("设置里开启了自动识别，但尚未配置 Whisper 模型，已跳过；请到「设置」里选择或下载模型。");
                return;
            }

            LogInfo("按设置自动开始识别队列…");
            Queue.Start();
        }
        catch (Exception ex)
        {
            LogWarn("自动开始识别失败：" + ex.Message);
        }
    }

    /// <summary>保存配置（实际写到 <see cref="AppSettings.SettingsFilePath"/>，即 %LOCALAPPDATA%\DFAudioStudio\settings.json）。</summary>
    public void SaveSettings()
    {
        try
        {
            Settings.Save();
            // 之前这里打的是"数据目录\settings.json"，而 Save() 实际写的是 %LOCALAPPDATA% 那份，
            // 排查问题时被这行日志带偏过，所以改成打真正的路径。
            LogInfo("配置已保存到 " + AppSettings.SettingsFilePath);
        }
        catch (Exception ex)
        {
            LogWarn("配置保存失败：" + ex.Message);
        }
    }

    /// <summary>确保导出目录存在。</summary>
    public string EnsureExportDir(params string[] subDirs)
    {
        string dir = Settings.ExportDir;
        if (string.IsNullOrWhiteSpace(dir))
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "DFAudioStudio导出");
        foreach (var sub in subDirs) dir = Path.Combine(dir, sub);
        try { Directory.CreateDirectory(dir); } catch { /* 交给调用方处理写失败 */ }
        return dir;
    }

    /// <summary>把列出的根目录整理成 Scanner 需要的 (根名, 路径) 形式。</summary>
    public List<(string Root, string Path)> BuildScanRoots()
        => new()
        {
            ("Media", Settings.MediaRoot ?? ""),
            ("Localized", Settings.LocalizedRoot ?? "")
        };

    /// <summary>取消用的 CTS 工厂（保持调用点简洁）。</summary>
    public static CancellationTokenSource NewCts() => new();
}
