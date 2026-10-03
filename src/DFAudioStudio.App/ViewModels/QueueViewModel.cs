using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using DFAudioStudio.App.Services;
using DFAudioStudio.Core.Data;
using DFAudioStudio.Core.Models;
using DFAudioStudio.Core.Services;
using Microsoft.UI.Dispatching;

namespace DFAudioStudio.App.ViewModels;

/// <summary>识别队列页：状态、当前文件与实时文本、暂停/继续/停止、失败列表（可重试）。</summary>
public sealed class QueueViewModel : ObservableObject
{
    private const int FailedLimit = 300;

    private readonly AppServices _services;
    private readonly DispatcherQueue? _ui;
    private int _refreshing;

    public QueueViewModel(AppServices services)
    {
        _services = services;
        _ui = UiDispatch.Capture();

        _services.Queue.ProgressChanged += OnQueueProgress;
        UpdateState();
    }

    public ObservableCollection<AudioItem> FailedItems { get; } = new();

    private string _stateText = "空闲";
    public string StateText { get => _stateText; private set => SetProperty(ref _stateText, value); }

    private string _currentFile = "（空闲）";
    public string CurrentFile { get => _currentFile; private set => SetProperty(ref _currentFile, value); }

    private string _currentText = "";
    /// <summary>当前文件的识别文本（页面会跟着自动滚动）。</summary>
    public string CurrentText { get => _currentText; private set => SetProperty(ref _currentText, value); }

    private string _doneText = "0";
    public string DoneText { get => _doneText; private set => SetProperty(ref _doneText, value); }

    private string _failedText = "0";
    public string FailedText { get => _failedText; private set => SetProperty(ref _failedText, value); }

    private string _remainingText = "0";
    public string RemainingText { get => _remainingText; private set => SetProperty(ref _remainingText, value); }

    private string _messageText = "";
    public string MessageText { get => _messageText; private set => SetProperty(ref _messageText, value); }

    private string _modelText = "";
    public string ModelText { get => _modelText; private set => SetProperty(ref _modelText, value); }

    private bool _canStart = true;
    public bool CanStart { get => _canStart; private set => SetProperty(ref _canStart, value); }

    private bool _canPause;
    public bool CanPause { get => _canPause; private set => SetProperty(ref _canPause, value); }

    private bool _canResume;
    public bool CanResume { get => _canResume; private set => SetProperty(ref _canResume, value); }

    private bool _canStop;
    public bool CanStop { get => _canStop; private set => SetProperty(ref _canStop, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }

    private string _failedHeader = "失败列表";
    public string FailedHeader { get => _failedHeader; private set => SetProperty(ref _failedHeader, value); }

    // ── 队列事件 ────────────────────────────────────────────────────────────

    private void OnQueueProgress(QueueProgress p)
    {
        UiDispatch.Run(_ui, () =>
        {
            DoneText = p.Done.ToString("N0");
            FailedText = p.Failed.ToString("N0");
            RemainingText = p.Remaining.ToString("N0");
            CurrentFile = string.IsNullOrWhiteSpace(p.CurrentFile) ? "（空闲）" : p.CurrentFile;
            if (!string.IsNullOrWhiteSpace(p.CurrentText)) CurrentText = p.CurrentText;
            MessageText = p.Message;
            UpdateState();
        });

        // 只在失败数变化时刷新失败列表（否则每个进度事件都查库，会把 UI 拖死）
        if (p.Failed != _lastFailedCount)
        {
            _lastFailedCount = p.Failed;
            _ = RefreshFailedAsync();
        }
    }

    private int _lastFailedCount;

    private void UpdateState()
    {
        var q = _services.Queue;
        StateText = q.State switch
        {
            QueueState.Running => "识别中",
            QueueState.Paused => "已暂停",
            QueueState.Finished => "已完成",
            QueueState.Error => "出错",
            _ => "空闲"
        };

        CanStart = q.State is QueueState.Idle or QueueState.Finished or QueueState.Error;
        CanPause = q.State == QueueState.Running;
        CanResume = q.State == QueueState.Paused;
        CanStop = q.State is QueueState.Running or QueueState.Paused;

        ModelText = _services.IsModelReady
            ? "模型：" + System.IO.Path.GetFileName(_services.Settings.ModelPath)
              + $" · 并发 {_services.Settings.Parallelism} · 语言 {_services.Settings.Language}"
              + (_services.Settings.TranscribeMusic ? " · 含音乐" : " · 仅语音")
            : "尚未配置 Whisper 模型：请到「设置」里选择或下载 ggml 模型后再开始识别。";
    }

    // ── 操作 ────────────────────────────────────────────────────────────────

    public async Task RefreshAsync()
    {
        UiDispatch.Run(_ui, () => IsBusy = true);
        try
        {
            await RefreshCountersAsync();
            await RefreshFailedAsync();
            UiDispatch.Run(_ui, UpdateState);
        }
        finally
        {
            UiDispatch.Run(_ui, () => IsBusy = false);
        }
    }

    private async Task RefreshCountersAsync()
    {
        var snapshot = await Task.Run(() =>
        {
            var statuses = _services.Db.CountsByStatus();
            int failed = _services.Db.Count(new ItemQuery { Status = TranscriptStatus.Failed });
            int pending = _services.Db.NextPending(_services.Settings.TranscribeMusic, 100000).Count;
            return (statuses, failed, pending);
        }).ConfigureAwait(true);

        UiDispatch.Run(_ui, () =>
        {
            int done = snapshot.statuses.TryGetValue(TranscriptStatus.Done, out int d) ? d : 0;
            DoneText = done.ToString("N0");
            FailedText = snapshot.failed.ToString("N0");
            RemainingText = snapshot.pending.ToString("N0");
        });
    }

    private async Task RefreshFailedAsync()
    {
        // 进度事件可能很密集，同一时间只允许一个刷新在跑。
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            var items = await Task.Run(() => _services.Db.Query(new ItemQuery
            {
                Status = TranscriptStatus.Failed,
                Limit = FailedLimit,
                OrderBy = "Path"
            })).ConfigureAwait(true);

            UiDispatch.Run(_ui, () =>
            {
                FailedItems.Clear();
                foreach (var item in items) FailedItems.Add(item);
                FailedHeader = items.Count == 0 ? "失败列表（暂无失败任务）" : $"失败列表（{items.Count} 条，可单独重试）";
            });
        }
        catch (Exception ex)
        {
            _services.LogWarn("读取失败任务失败：" + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    public void Start()
    {
        if (!_services.IsModelReady)
        {
            MessageText = "请先在「设置」里配置 Whisper 模型。";
            _services.LogWarn("识别未启动：尚未配置模型。");
            return;
        }

        // 走 EnsureQueueRunning：它在「上一个 worker 还没收尾」时会补偿重试，避免点了没反应
        _services.EnsureQueueRunning("开始识别");
        UpdateState();
    }

    public void Pause()
    {
        _services.Queue.Pause();
        UpdateState();
    }

    public void Resume()
    {
        _services.Queue.Resume();
        UpdateState();
    }

    public void Stop()
    {
        _services.Queue.Stop();
        UpdateState();
    }

    /// <summary>重试单条失败任务（任何时候都能点：状态重置为未处理 + 清空旧文本 + 队列没在跑就启动）。</summary>
    public async Task RetryOneAsync(AudioItem? item)
    {
        if (item is null)
        {
            Report("没有可重试的条目。");
            return;
        }

        // 统一走 AppServices.RequestReTranscribe：重置状态 / 清空旧文本 / 模型没配好时给出明确提示。
        _services.RequestReTranscribe(item);

        // 队列页自己的一行提示也带上队列状态，点完立刻能确认有没有真的入队。
        MessageText = _services.IsModelReady
            ? $"已把「{item.FileName}」重新加入识别队列（当前队列：{_services.QueueStateText}）。"
            : $"已把「{item.FileName}」置为待识别（当前队列：{_services.QueueStateText}）；尚未配置 Whisper 模型，配置后点「开始识别」即可。";

        UpdateState();
        await RefreshAsync();
    }

    /// <summary>页面写入一行提示（例如重试按钮取不到数据项时）。</summary>
    public void Report(string message) => MessageText = message;

    /// <summary>一键重试全部失败任务。</summary>
    public async Task RetryAllFailedAsync()
    {
        _services.RetryAllFailed();
        UpdateState();
        await RefreshAsync();
    }
}
