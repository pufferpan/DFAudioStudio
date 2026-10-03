using DFAudioStudio.Core.Data;
using DFAudioStudio.Core.Models;

namespace DFAudioStudio.Core.Services;

public enum QueueState { Idle, Running, Paused, Finished, Error }

public sealed record QueueProgress(
    QueueState State,
    int Done,
    int Failed,
    int Remaining,
    string CurrentFile,
    string CurrentText,
    double ElapsedSecPerItem,
    string Message);

/// <summary>
/// 识别任务队列：打开软件即可自动开始，支持暂停/继续/停止，结果写回 SQLite（断电重开继续跑，不重复识别）。
/// </summary>
public sealed class TranscriptionQueue
{
    private readonly IndexDb _db;
    private readonly AppSettings _settings;
    private readonly Func<ITranscriber?> _transcriberFactory;

    private CancellationTokenSource? _cts;
    private Task? _worker;
    private volatile bool _paused;

    public int Done { get; private set; }
    public int Failed { get; private set; }
    public QueueState State { get; private set; } = QueueState.Idle;
    public string CurrentFile { get; private set; } = "";
    public string CurrentText { get; private set; } = "";

    public event Action<QueueProgress>? ProgressChanged;

    public TranscriptionQueue(IndexDb db, AppSettings settings, Func<ITranscriber?> transcriberFactory)
    {
        _db = db;
        _settings = settings;
        _transcriberFactory = transcriberFactory;
    }

    public bool IsRunning => State == QueueState.Running;
    public bool IsPaused => State == QueueState.Paused;

    public void Start()
    {
        if (_worker is { IsCompleted: false }) { Resume(); return; }
        _paused = false;
        _cts = new CancellationTokenSource();
        State = QueueState.Running;
        // 注意：这里**不能**查库 —— Start() 是在 UI 线程（按钮事件）里被调用的，
        // 任何 COUNT/GROUP BY 都会卡住界面。只发一个不带库查询的进度事件。
        Report("开始识别：正在后台加载模型…", force: true, queryDb: false);
        _worker = Task.Run(() => WorkerAsync(_cts.Token));
    }

    public void Pause()
    {
        if (State != QueueState.Running) return;
        _paused = true;
        State = QueueState.Paused;
        Report("已暂停");
    }

    public void Resume()
    {
        if (State != QueueState.Paused) return;
        _paused = false;
        State = QueueState.Running;
        Report("继续识别");
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        State = QueueState.Idle;
        Report("已停止");
    }

    private async Task WorkerAsync(CancellationToken ct)
    {
        try
        {
            // 后台识别线程降优先级：保证界面（UI 线程）在有识别任务时依然跟手，不会"卡死"
            try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch { }

            State = QueueState.Running;
            Report("正在加载识别模型（首次较慢，界面可继续操作）…", force: true);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var transcriber = _transcriberFactory();
            if (transcriber is null)
            {
                State = QueueState.Error;
                Report("未配置 Whisper 模型，无法识别。请到「设置」里选择或下载模型。", force: true);
                return;
            }
            Report($"模型已就绪（{sw.Elapsed.TotalSeconds:F1}s）：{transcriber.ModelName}", force: true);

            int concurrency = Math.Clamp(_settings.Parallelism, 1, 8);
            double minDur = Math.Max(0, _settings.MinDurationSec);

            int stale = _db.ResetStaleRunning();
            if (stale > 0) System.Diagnostics.Debug.WriteLine($"[队列] 复位 {stale} 条上次中断时卡住的任务");

            int skippedShort = _db.SkipTooShort(minDur);
            if (skippedShort > 0)
                System.Diagnostics.Debug.WriteLine($"[队列] 跳过 {skippedShort} 条过短碎片（< {minDur}s）");

            int skippedKeys = _db.SkipByKeywords((_settings.SkipKeywords ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries));
            if (skippedKeys > 0)
                System.Diagnostics.Debug.WriteLine($"[队列] 跳过 {skippedKeys} 条非语音素材（呼吸/脚步/拟音等）");

            var pendingLock = new object();
            int initial = _db.PendingCount(_settings.TranscribeMusic, minDur);
            if (initial == 0)
            {
                State = QueueState.Finished;
                Report("没有待识别的任务（全部已完成）", force: true);
                return;
            }
            _cachedRemaining = initial;
            _lastRemainingUtc = DateTime.UtcNow;

            while (!ct.IsCancellationRequested)
            {
                while (_paused && !ct.IsCancellationRequested)
                    await Task.Delay(300, ct).ConfigureAwait(false);

                List<(long Id, string Path)> batch;
                lock (pendingLock)
                    batch = _db.NextPending(_settings.TranscribeMusic, concurrency, minDur);

                if (batch.Count == 0)
                {
                    State = QueueState.Finished;
                    Report("全部任务已完成");
                    return;
                }

                foreach (var (id, path) in batch) _db.UpdateStatus(id, TranscriptStatus.Queued);

                var tasks = batch.Select(async item =>
                {
                    var (id, path) = item;
                    if (ct.IsCancellationRequested) return;
                    try
                    {
                        _db.UpdateStatus(id, TranscriptStatus.Running);
                        CurrentFile = System.IO.Path.GetFileName(path);
                        Report("识别中: " + CurrentFile);

                        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeoutCts.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, _settings.TimeoutMinutes)));

                        var result = await transcriber.TranscribeAsync(path, _settings.Language, timeoutCts.Token).ConfigureAwait(false);

                        if (string.IsNullOrWhiteSpace(result.Text))
                        {
                            _db.UpdateStatus(id, TranscriptStatus.Skipped, "无语音内容");
                        }
                        else
                        {
                            _db.SaveTranscript(id, result);
                            Done++;
                            CurrentText = result.Text;
                            System.Diagnostics.Debug.WriteLine($"[OK] {CurrentFile}: {result.Text[..Math.Min(60, result.Text.Length)]}");
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                    catch (Exception ex)
                    {
                        Failed++;
                        _db.UpdateStatus(id, TranscriptStatus.Failed, ex.Message);
                    }
                });

                await Task.WhenAll(tasks).ConfigureAwait(false);
                Report("");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            State = QueueState.Error;
            Report("队列异常: " + ex.Message);
        }
    }

    // 进度上报节流：识别很快时（每秒几十条）如果不节流，会把 UI 线程刷爆 → 表现为"卡死"
    private DateTime _lastReportUtc = DateTime.MinValue;
    private string _lastMessage = "";
    private int _cachedRemaining;
    private DateTime _lastRemainingUtc = DateTime.MinValue;
    private const int ReportIntervalMs = 400;       // 最快 400ms 一次
    private const int RemainingRefreshSec = 3;      // 剩余数量（较重的查询）最多 3 秒算一次

    private void Report(string message, bool force = false, bool queryDb = true)
    {
        var now = DateTime.UtcNow;
        bool messageChanged = !string.Equals(message, _lastMessage, StringComparison.Ordinal);
        if (!force && !messageChanged && (now - _lastReportUtc).TotalMilliseconds < ReportIntervalMs) return;

        _lastReportUtc = now;
        _lastMessage = message;

        if (queryDb && (force || (now - _lastRemainingUtc).TotalSeconds >= RemainingRefreshSec))
        {
            try
            {
                _cachedRemaining = _db.PendingCount(_settings.TranscribeMusic, Math.Max(0, _settings.MinDurationSec));
                _lastRemainingUtc = now;
            }
            catch { /* 查询失败不影响识别 */ }
        }

        ProgressChanged?.Invoke(new QueueProgress(State, Done, Failed, _cachedRemaining, CurrentFile, CurrentText, 0, message));
    }
}
