using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DFAudioStudio.App.Services;
using DFAudioStudio.Core.Data;
using DFAudioStudio.Core.Models;
using DFAudioStudio.Core.Services;
using Microsoft.UI.Dispatching;

namespace DFAudioStudio.App.ViewModels;

/// <summary>概览页：统计卡片 + 全量扫描 + 手动开始 / 暂停 / 停止识别 + 日志。</summary>
public sealed class DashboardViewModel : ObservableObject
{
    private const int MaxLogLines = 200;

    private readonly AppServices _services;
    private readonly DispatcherQueue? _ui;
    private readonly Queue<string> _logLines = new();

    private CancellationTokenSource? _scanCts;
    private bool _isScanning;

    public DashboardViewModel(AppServices services)
    {
        _services = services;
        _ui = UiDispatch.Capture();

        _services.Log += OnLogLine;
        _services.Queue.ProgressChanged += OnQueueProgress;

        UpdateQueueState();
    }

    // ── 统计卡片 ────────────────────────────────────────────────────────────

    public string TotalText { get; private set; } = "0";
    public string VoiceText { get; private set; } = "0";
    public string MusicText { get; private set; } = "0";
    public string SfxText { get; private set; } = "0";
    public string AmbienceText { get; private set; } = "0";
    public string UiText { get; private set; } = "0";
    public string OtherText { get; private set; } = "0";
    public string DoneText { get; private set; } = "0";
    public string FailedText { get; private set; } = "0";
    public string PendingText { get; private set; } = "0";
    public string SkippedText { get; private set; } = "0";
    public string WithTranscriptText { get; private set; } = "0";
    public string LastScanText { get; private set; } = "尚未扫描过";
    public string MediaRootText { get; private set; } = "";
    public string LocalizedRootText { get; private set; } = "";
    public string ModelText { get; private set; } = "";

    // ── 队列状态 ────────────────────────────────────────────────────────────

    private string _queueStateText = "空闲（未开始）";
    public string QueueStateText { get => _queueStateText; private set => SetProperty(ref _queueStateText, value); }

    private string _queueButtonText = "▶ 开始识别（手动）";
    /// <summary>识别主按钮文案：空闲＝「▶ 开始识别（手动）」，识别中＝「‖ 暂停」，已暂停＝「▶ 继续」。</summary>
    public string QueueButtonText { get => _queueButtonText; private set => SetProperty(ref _queueButtonText, value); }

    private bool _canStopQueue;
    /// <summary>「■ 停止」按钮是否可用（只有正在跑或暂停时才可用）。</summary>
    public bool CanStopQueue { get => _canStopQueue; private set => SetProperty(ref _canStopQueue, value); }

    private string _queueDetailText = "";
    public string QueueDetailText { get => _queueDetailText; private set => SetProperty(ref _queueDetailText, value); }

    // ── 扫描状态 ────────────────────────────────────────────────────────────

    private string _scanStatusText = "";
    public string ScanStatusText { get => _scanStatusText; private set => SetProperty(ref _scanStatusText, value); }

    private double _scanPercent;
    public double ScanPercent { get => _scanPercent; private set => SetProperty(ref _scanPercent, value); }

    private bool _scanIndeterminate;
    public bool ScanIndeterminate { get => _scanIndeterminate; private set => SetProperty(ref _scanIndeterminate, value); }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (!SetProperty(ref _isScanning, value)) return;
            OnPropertyChanged(nameof(ScanButtonText));
        }
    }

    public string ScanButtonText => IsScanning ? "正在扫描…" : "扫描索引";

    // ── 日志 ────────────────────────────────────────────────────────────────

    private string _logText = "";
    public string LogText { get => _logText; private set => SetProperty(ref _logText, value); }

    private void OnLogLine(string line)
    {
        // 日志面板节流：识别时会刷很多行，如果每行都重建整段文本 + 重排 TextBlock，UI 线程会被拖死（表现为卡死）
        lock (_logGate) { _logLines.Enqueue(line); while (_logLines.Count > MaxLogLines) _logLines.Dequeue(); }
        if ((DateTime.UtcNow - _lastLogFlushUtc).TotalMilliseconds < LogFlushIntervalMs) return;
        _lastLogFlushUtc = DateTime.UtcNow;

        string text;
        lock (_logGate) text = string.Join(Environment.NewLine, _logLines);
        UiDispatch.Run(_ui, () => LogText = text);
    }

    private readonly object _logGate = new();
    private DateTime _lastLogFlushUtc = DateTime.MinValue;
    private const int LogFlushIntervalMs = 600;

    private void OnQueueProgress(QueueProgress p)
    {
        UiDispatch.Run(_ui, () =>
        {
            UpdateQueueState();
            QueueDetailText = $"完成 {p.Done} · 失败 {p.Failed} · 剩余 {p.Remaining}"
                              + (string.IsNullOrWhiteSpace(p.CurrentFile) ? "" : $" · 当前：{p.CurrentFile}")
                              + (string.IsNullOrWhiteSpace(p.Message) ? "" : $" · {p.Message}");
        });
    }

    private void UpdateQueueState()
    {
        var q = _services.Queue;
        QueueStateText = q.State switch
        {
            QueueState.Running => "识别中",
            QueueState.Paused => "已暂停",
            QueueState.Finished => "已完成",
            QueueState.Error => "出错",
            _ => "空闲（未开始）"
        };
        QueueButtonText = q.State switch
        {
            QueueState.Running => "暂停",
            QueueState.Paused => "继续",
            _ => "开始识别"
        };
        CanStopQueue = q.State is QueueState.Running or QueueState.Paused;

        if (!_services.IsModelReady)
            QueueStateText += "（未配置模型）";
    }

    // ── 刷新统计 ────────────────────────────────────────────────────────────

    public async Task RefreshAsync()
    {
        try
        {
            var snapshot = await Task.Run(() =>
            {
                var cats = _services.Db.CountsByCategory();
                var statuses = _services.Db.CountsByStatus();
                long total = _services.Db.TotalCount();
                string? lastScan = _services.Db.GetKv("LastScanUtc");
                int failed = _services.Db.Count(new ItemQuery { Status = TranscriptStatus.Failed });
                int withTranscript = _services.Db.Count(new ItemQuery { OnlyWithTranscript = true });
                return (cats, statuses, total, lastScan, failed, withTranscript);
            }).ConfigureAwait(true);

            UiDispatch.Run(_ui, () =>
            {
                var cats = snapshot.cats;
                var statuses = snapshot.statuses;

                TotalText = Count(snapshot.total);
                VoiceText = Count(Category(cats, AudioCategory.Voice));
                MusicText = Count(Category(cats, AudioCategory.Music));
                SfxText = Count(Category(cats, AudioCategory.Sfx));
                AmbienceText = Count(Category(cats, AudioCategory.Ambience));
                UiText = Count(Category(cats, AudioCategory.Ui));
                OtherText = Count(Category(cats, AudioCategory.Other));

                DoneText = Count(Status(statuses, TranscriptStatus.Done));
                FailedText = Count(snapshot.failed);
                SkippedText = Count(Status(statuses, TranscriptStatus.Skipped));
                PendingText = Count(Status(statuses, TranscriptStatus.None)
                                    + Status(statuses, TranscriptStatus.Queued)
                                    + Status(statuses, TranscriptStatus.Running));
                WithTranscriptText = Count(snapshot.withTranscript);

                LastScanText = ParseLastScan(snapshot.lastScan);
                MediaRootText = "Media：" + Show(_services.Settings.MediaRoot);
                LocalizedRootText = "Localized：" + Show(_services.Settings.LocalizedRoot);
                ModelText = _services.IsModelReady
                    ? "模型：" + System.IO.Path.GetFileName(_services.Settings.ModelPath)
                    : "模型：未配置";

                OnPropertiesChanged(nameof(TotalText), nameof(VoiceText), nameof(MusicText), nameof(SfxText),
                                    nameof(AmbienceText), nameof(UiText), nameof(OtherText), nameof(DoneText),
                                    nameof(FailedText), nameof(PendingText), nameof(SkippedText),
                                    nameof(WithTranscriptText), nameof(LastScanText), nameof(MediaRootText),
                                    nameof(LocalizedRootText), nameof(ModelText));

                UpdateQueueState();
            });
        }
        catch (Exception ex)
        {
            _services.LogWarn("刷新统计失败：" + ex.Message);
        }
    }

    // ── 扫描 ────────────────────────────────────────────────────────────────

    public async Task ScanAsync()
    {
        if (IsScanning) return;

        IsScanning = true;
        ScanIndeterminate = true;
        ScanPercent = 0;
        ScanStatusText = "正在扫描，请稍候…（首次扫描几万个文件可能需要几分钟）";

        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;

        var progress = new Progress<ScanProgress>(p => UiDispatch.Run(_ui, () =>
        {
            ScanStatusText = $"已检查 {p.Scanned} 个文件 · 新增/更新 {p.Changed} · 当前：{Short(p.CurrentFile)}";
            if (p.TotalHint > 0)
            {
                ScanIndeterminate = false;
                ScanPercent = Math.Min(100, p.Scanned * 100.0 / p.TotalHint);
            }
        }));

        try
        {
            var roots = _services.BuildScanRoots();
            var stats = await _services.Scanner
                .ScanAsync(roots, progress, token, message => _services.LogInfo(message))
                .ConfigureAwait(true);

            ScanIndeterminate = false;
            ScanPercent = 100;
            ScanStatusText = $"扫描完成：共 {stats.TotalFiles} 个文件，新增/更新 {stats.NewOrUpdated}，未变化跳过 {stats.Skipped}，失败 {stats.Failed}。";
            await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
            ScanStatusText = "扫描已取消。";
        }
        catch (Exception ex)
        {
            ScanStatusText = "扫描失败：" + ex.Message;
            _services.LogWarn("扫描失败：" + ex.Message);
        }
        finally
        {
            IsScanning = false;
            ScanIndeterminate = false;
            _scanCts?.Dispose();
            _scanCts = null;
        }
    }

    public void CancelScan()
    {
        try { _scanCts?.Cancel(); } catch { }
    }

    // ── 识别 ────────────────────────────────────────────────────────────────

    /// <summary>开始 / 暂停 / 继续 识别（按钮文案随状态变化）。模型只在真正开始识别时才由队列后台线程加载。</summary>
    public void ToggleQueue()
    {
        var q = _services.Queue;

        if (q.State == QueueState.Running) { q.Pause(); _services.LogInfo("识别已暂停（手动）"); }
        else if (q.State == QueueState.Paused) { q.Resume(); _services.LogInfo("识别已继续"); }
        else
        {
            if (!_services.IsModelReady)
            {
                QueueStateText = "未配置模型";
                _services.LogWarn("请先在「设置」里选择或下载 Whisper ggml 模型。");
                return;
            }
            _services.LogInfo("手动开始识别…");
            q.Start();
        }

        UpdateQueueState();
    }

    /// <summary>停止识别：已完成的不会重复识别，之后随时可以再点「开始识别」接着跑。</summary>
    public void StopQueue()
    {
        _services.Queue.Stop();
        _services.LogInfo("识别已停止（手动）");
        UpdateQueueState();
    }

    // ── 导出 ────────────────────────────────────────────────────────────────

    /// <summary>把所有识别文本导出成一个 TXT 合集。</summary>
    public async Task ExportTxtAsync()
    {
        try
        {
            ScanStatusText = "正在导出识别文本…";
            string dir = _services.EnsureExportDir();
            var items = await Task.Run(() => _services.Db.Query(new ItemQuery
            {
                OnlyWithTranscript = true,
                Limit = 50000,
                OrderBy = "Path"
            })).ConfigureAwait(true);

            string path = await Task.Run(() =>
                ExportService.ExportTxtBundle(items, dir, "全部识别文本.txt")).ConfigureAwait(true);

            ScanStatusText = $"已导出 {items.Count} 条识别文本 → {path}";
            _services.LogInfo(ScanStatusText);
        }
        catch (Exception ex)
        {
            ScanStatusText = "导出失败：" + ex.Message;
            _services.LogWarn("导出识别文本失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 导出识别结果为交换文件（JSON，可再导入回来）。
    /// 与 TXT 导出的区别：这份带 文件名 / 字节数 / 完整路径 / 分段，是**可回灌**的，用来在机器之间搬识别成果。
    /// </summary>
    public async Task ExportTranscriptsJsonAsync()
    {
        try
        {
            ScanStatusText = "正在导出识别结果（JSON）…";
            string dir = _services.EnsureExportDir("识别结果");

            var (path, count) = await Task.Run(() =>
            {
                var items = _services.Db.Query(new ItemQuery
                {
                    OnlyWithTranscript = true,
                    Limit = 50000,
                    OrderBy = "Path",
                });
                string file = TranscriptTransfer.ExportAll(_services.Db, dir);
                return (file, items.Count);
            }).ConfigureAwait(true);

            ScanStatusText = $"已导出 {count:N0} 条识别结果 → {path}";
            _services.LogInfo(ScanStatusText);
        }
        catch (Exception ex)
        {
            ScanStatusText = "导出识别结果失败：" + ex.Message;
            _services.LogWarn("导出识别结果失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 导入识别结果：先解析文件（可能很大，放后台线程），把"会命中多少 / 会改多少"算出来给界面确认。
    /// 返回 null 表示读失败，<paramref name="error"/> 里有原因。
    /// </summary>
    public async Task<(TranscriptFile File, TranscriptImportPlan Plan)?> PrepareImportAsync(string path, Action<string>? onError)
    {
        try
        {
            ScanStatusText = "正在读取识别结果文件…";
            var prepared = await Task.Run(() =>
            {
                var file = TranscriptFile.Read(path, out string error);
                if (file is null) return (File: (TranscriptFile?)null, Plan: (TranscriptImportPlan?)null, Error: error);

                var plan = TranscriptTransfer.Plan(_services.Db, file);
                return (File: file, Plan: plan, Error: "");
            }).ConfigureAwait(true);

            if (prepared.File is null || prepared.Plan is null)
            {
                ScanStatusText = "导入失败：" + prepared.Error;
                onError?.Invoke(prepared.Error);
                return null;
            }

            ScanStatusText = "待确认：" + prepared.Plan.Describe();
            return (prepared.File, prepared.Plan);
        }
        catch (Exception ex)
        {
            ScanStatusText = "导入失败：" + ex.Message;
            _services.LogWarn("读取导入文件失败：" + ex.Message);
            onError?.Invoke(ex.Message);
            return null;
        }
    }

    /// <summary>确认后真正写库（后台线程），完成后刷新统计。</summary>
    public async Task ImportTranscriptsAsync(TranscriptFile file)
    {
        try
        {
            ScanStatusText = "正在导入识别结果…";
            var outcome = await Task.Run(() =>
                TranscriptTransfer.Apply(_services.Db, file, (done, total) =>
                    UiDispatch.Run(_ui, () => ScanStatusText = $"正在导入… {done:N0} / {total:N0}"))).ConfigureAwait(true);

            _services.LogInfo("导入识别结果：" + outcome.Describe());
            ScanStatusText = outcome.Describe();
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ScanStatusText = "导入失败：" + ex.Message;
            _services.LogWarn("导入识别结果失败：" + ex.Message);
        }
    }

    // ── 小工具 ──────────────────────────────────────────────────────────────

    private static int Category(Dictionary<AudioCategory, int> map, AudioCategory key)
        => map.TryGetValue(key, out int v) ? v : 0;

    private static int Status(Dictionary<TranscriptStatus, int> map, TranscriptStatus key)
        => map.TryGetValue(key, out int v) ? v : 0;

    private static string Count(long n) => n.ToString("N0");

    private static string Show(string value)
        => string.IsNullOrWhiteSpace(value) ? "未设置" : value;

    private static string Short(string path)
        => string.IsNullOrWhiteSpace(path) ? "-" : System.IO.Path.GetFileName(path);

    private static string ParseLastScan(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "尚未扫描过";
        return DateTimeOffset.TryParse(raw, out var t)
            ? "上次扫描：" + t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : "上次扫描：" + raw;
    }
}
