using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using DFAudioStudio.App.Services;
using DFAudioStudio.App.ViewModels;
using DFAudioStudio.Core.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace DFAudioStudio.App.Controls;

/// <summary>
/// 可复用的音频浏览控件：筛选栏 + 列表 + 底部播放器卡片（Windows.Media.Playback.MediaPlayer）。
/// 统一由 <see cref="DFAudioStudio.App.Pages.BrowserPage"/> 通过 <see cref="SetScope"/> 传入分区过滤描述。
///
/// 播放时序（点一条音频之后发生什么）：
///   ① <see cref="StartPlaybackAsync"/> 先 <see cref="StopPlayerSource"/>：把上一条 Pause → Position=0 → Source=null；
///   ② 切 UI 到新条目（标题跟着 ViewModel.SelectedItem 走）、进度条清零、列表选中同步，并启动 250ms 定时器；
///   ③ await <c>StorageFile.GetFileFromPathAsync</c> 拿到 <c>StorageFile</c> 再建 <c>MediaSource</c>
///      （让 MF 拿到的不是「未经转义的 file:/// 路径」；期间用户又点了别的条目就用 _playToken 作废这次结果）；
///   ④ 只设置 <c>MediaPlayer.Source</c>，**不紧跟 Play()**：Source 还没 open 时 Play() 有被忽略的情况。
///      <see cref="EnsurePlayer"/> 里 AutoPlay=true，媒体打开后会自己开始播；<see cref="OnMediaOpened"/> 里再兜一次 Play()，
///      并在这一刻才把按钮切成「暂停」、把 NaturalDuration 写进进度条；
///   ⑤ 失败时不再猜原因：<see cref="OnMediaFailed"/> 把 Error / ErrorMessage / ExtendedErrorCode(HRESULT) 全部写日志与
///      播放条的可见错误位，并自动做一次「英文路径副本」隔离验证来区分路径编码问题与解码器问题。
/// </summary>
public sealed partial class AudioBrowserControl : UserControl
{
    private readonly DispatcherQueue? _ui;

    private BrowserScope? _pendingScope;
    private MediaPlayer? _player;
    private DispatcherQueueTimer? _timer;

    /// <summary>当前已加载（或正在加载）的那一条；界面「当前音频」以它和 ViewModel.SelectedItem 为准。</summary>
    private AudioItem? _playing;

    /// <summary>当前 MediaPlayer.Source 对应的条目 Id；Source=null（停播 / 换源中间态）时为 null。</summary>
    private long? _loadedItemId;

    private bool _isPlaying;
    private bool _hasEnded;          // 已播完：再点播放键要重新加载，而不是从末尾续播
    /// <summary>用户主动暂停过：MediaOpened 里就不要再自动 Play()</summary>
    private bool _pauseRequested;

    /// <summary>切换播放引擎时用来续播的位置（秒）；媒体打开后应用一次。</summary>
    private double _pendingSeekSeconds;

    private bool _scrubbing;         // 正在拖动进度条：期间定时器不回写 Slider.Value
    private bool _suppressSlider;    // 程序化写 Slider.Value 时不要再触发 seek
    private bool _suppressVolume;
    private bool _suppressFilters;
    private bool _loaded;

    /// <summary>每次换源自增；await 回来发现 token 变了就丢弃这次结果（用户已经点了别的条目）。</summary>
    private int _playToken;

    // ── 隔离验证：播放失败后自动做一次「纯 ASCII 路径副本」对照 ──────────────
    private string? _probeCopyPath;      // 复制出来的临时副本（ASCII 文件名）；正在播它时非 null
    private string? _probeFirstFailure;  // 原路径那次失败的原因（写结论用）
    private bool _probeInFlight;         // 正在等副本的播放结果（MediaOpened / MediaFailed）
    private long? _probeItemId;          // 已经验证过哪一条（同一条只做一次，避免反复复制/播放）

    private double _volume = 1.0;
    private double _volumeBeforeMute = 1.0;

    public BrowserViewModel ViewModel { get; }

    public AudioBrowserControl()
    {
        ViewModel = new BrowserViewModel(AppServices.Current);
        _ui = UiDispatch.Capture();
        InitializeComponent();

        ViewModel.PlayRequested += OnPlayRequested;
        Loaded += AudioBrowserControl_Loaded;
        Unloaded += AudioBrowserControl_Unloaded;

        // 一开始就把「播放按钮图标 / 音量图标」摆正（此时是「未播放 + 音量 1.0」）
        UpdatePlayButton();
        UpdateVolumeIcon();
    }

    /// <summary>设置本页面的预设筛选（分区 / 活动 / 地图）。可随时调用。</summary>
    public void SetScope(BrowserScope scope)
    {
        _pendingScope = scope;
        if (!_loaded) return;

        ViewModel.ApplyScope(scope);
        _pendingScope = null;
        SyncFilterBoxes();
        _ = ReloadAndSyncAsync();
    }

    /// <summary>外部（例如识别完成后）要求重新查询；查询完会把播放状态与列表选中对齐。</summary>
    public Task ReloadAsync() => ReloadAndSyncAsync();

    // ── 生命周期 ────────────────────────────────────────────────────────────

    private async void AudioBrowserControl_Loaded(object sender, RoutedEventArgs e)
    {
        EnsureTimer();
        UpdatePlayButton();
        UpdateVolumeIcon();

        if (_pendingScope is not null)
        {
            ViewModel.ApplyScope(_pendingScope);
            _pendingScope = null;
        }

        // 先让 ComboBox 与 VM 的预设对齐，再查询；期间忽略下拉的 SelectionChanged。
        SyncFilterBoxes();
        _loaded = true;

        await ViewModel.LoadAsync();
        SyncFilterBoxes();
        SyncPlaybackWithList();
    }

    /// <summary>
    /// 卸载：停表 + 停播 + Dispose 播放器，并把播放状态清空（页面用 NavigationCacheMode.Required，
    /// 切走再回来会重新 Loaded —— 那时状态是干净的）。定时器直接置空，
    /// 下次 Loaded 由 <see cref="EnsureTimer"/> 重建，避免留下野定时器。
    /// </summary>
    private void AudioBrowserControl_Unloaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _playToken++;                 // 让正在进行的 await / 事件回调结果作废
            _scrubbing = false;
            _timer?.Stop();
            _timer = null;
            StopPlayerSource();
            _player?.Dispose();
            _dsp?.Dispose();
            DeleteProbeCopy();            // 源已经停了，临时副本可以删
        }
        catch { /* 卸载阶段的异常忽略 */ }
        finally
        {
            _player = null;
            _dsp = null;
            _dspActive = false;
            _playing = null;
            _loadedItemId = null;
            _isPlaying = false;
            _hasEnded = false;
            _pauseRequested = false;
            _probeItemId = null;
            _probeInFlight = false;
        }
    }

    /// <summary>ViewModel 里的下拉值 → 实际控件（列表被重新填充后需要重新对齐）。</summary>
    private void SyncFilterBoxes()
    {
        _suppressFilters = true;
        try
        {
            CategoryBox.SelectedItem = ViewModel.SelectedCategory;
            OperatorBox.SelectedItem = ViewModel.SelectedOperator;
            EventBox.SelectedItem = ViewModel.SelectedEvent;
            MapBox.SelectedItem = ViewModel.SelectedMap;
            SubCategoryBox.SelectedItem = ViewModel.SelectedSubCategory;
            SkinBox.SelectedItem = ViewModel.SelectedSkin;
            StatusBox.SelectedItem = ViewModel.SelectedStatus;
            SortBox.SelectedItem = ViewModel.SelectedSort;
        }
        catch { /* 下拉项尚未就绪时忽略 */ }
        finally
        {
            _suppressFilters = false;
        }
    }

    // ── 筛选 / 导出 ─────────────────────────────────────────────────────────

    private void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 初始化阶段的下拉赋值不触发查询，避免用空预设抢在 Loaded 之前跑一遍查询。
        if (!_loaded || _suppressFilters) return;
        _ = ReloadAndSyncAsync();
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await ReloadAndSyncAsync();

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshFilterOptionsAsync();
        SyncFilterBoxes();
        await ReloadAndSyncAsync();
    }

    private async void ExportCsv_Click(object sender, RoutedEventArgs e) => await ViewModel.ExportCsvAsync();

    private async void ExportSrt_Click(object sender, RoutedEventArgs e) => await ViewModel.ExportSrtAsync();

    private async void Redo_Click(object sender, RoutedEventArgs e)
    {
        // 重跑 + 刷新列表（重置状态、清空旧文本、必要时启动队列都在 ViewModel 里）
        await ViewModel.ReTranscribeSelectedAsync();

        // 刷新列表会重建所有行：把选中项重新对上，
        // 否则「重新识别」按钮会因为失去选中项立刻变灰（用户会以为没生效）。
        // 只在当前列表里确实还有这一条时才回填 —— 否则 ListView 会把选中项置空；
        // quiet=true：不要用「已停止播放」把上面那句重跑提示冲掉。
        SyncPlaybackWithList(quiet: true);
        try { TranscriptScroller.ChangeView(null, 0, null); } catch { /* 布局尚未就绪时忽略 */ }
    }

    /// <summary>打开文件位置：优先在资源管理器里选中该文件，失败再退化成打开所在文件夹。</summary>
    private async void Reveal_Click(object sender, RoutedEventArgs e)
    {
        var item = ViewModel.SelectedItem ?? _playing;
        if (item is null)
        {
            ViewModel.Report("先选中一条音频。");
            return;
        }

        string path = item.Path ?? "";
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ViewModel.Report("该文件已不在原位置：" + path);
            AppServices.Current.LogWarn("打开文件位置失败（文件不存在）：" + path);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "/select,\"" + path + "\"",
                UseShellExecute = true
            });
            ViewModel.Report("已在资源管理器中定位：" + item.FileName);
        }
        catch (Exception ex)
        {
            AppServices.Current.LogWarn("用资源管理器定位文件失败（改打开所在文件夹）：" + ex.Message);
            try
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    await Windows.System.Launcher.LaunchFolderPathAsync(dir);
                    ViewModel.Report("已打开所在文件夹：" + dir);
                }
                else
                {
                    ViewModel.Report("打开文件位置失败：" + ex.Message);
                }
            }
            catch (Exception inner)
            {
                AppServices.Current.LogWarn("打开所在文件夹也失败：" + inner.Message);
                ViewModel.Report("打开文件位置失败：" + inner.Message);
            }
        }
    }

    // ── 列表 ────────────────────────────────────────────────────────────────

    private void ItemList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.SelectedItem = ItemList.SelectedItem as AudioItem;
        try { TranscriptScroller.ChangeView(null, 0, null); } catch { /* 布局尚未就绪时忽略 */ }
    }

    /// <summary>
    /// 单击列表里的一条：**正在播的时候跟着切过去**。
    ///
    /// 为什么单独用 ItemClick 而不是在 SelectionChanged 里做：
    /// · SelectionChanged 只表示"选中变了"，用方向键移动高亮、或者代码里程序化改选中项（刷新后对齐播放状态）也会触发它 —— 
    ///   在那里起播会导致"按一下方向键就重新加载一遍音频"、以及刷新列表时把正在播的条目重播；
    /// · ItemClick 只认真实点击，正好是"点谁就播谁"这个语义。
    ///
    /// 没在播时单击仍然只是选中（保持原来的用法），想播还是双击或按播放键。
    /// </summary>
    private void ItemList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not AudioItem item) return;

        ViewModel.SelectedItem = item;

        if (!_isPlaying) return;                      // 当前没在播：只选中，不放音
        if (_loadedItemId == item.Id) return;        // 点的就是正在播的这条，不用重来

        AppServices.Current.LogInfo($"单击切换播放：{_playing?.FileName ?? "(无)"} → {item.FileName}");
        _ = StartPlaybackAsync(item);
    }

    private void ItemList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is { } item) _ = StartPlaybackAsync(item);
    }

    /// <summary>重新查询 + 让「选中项 / 高亮 / 播放状态」三者一致（所有刷新入口都走这里）。</summary>
    private async Task ReloadAndSyncAsync()
    {
        await ViewModel.LoadAsync();
        SyncPlaybackWithList();
    }

    /// <summary>
    /// 列表刷新后对齐播放状态：AudioItem 每次查询都是新对象，所以必须按 Id 重新定位。
    /// · 当前播放项还在新结果里 → 换成新集合里的那个实例，并同步 ViewModel.SelectedItem 与列表高亮；
    /// · 已经被筛掉 → 停止播放并复位界面（按钮回 ▶、进度回 0:00）；
    /// · 没有在播 → 让列表高亮跟随 ViewModel 的选中项。
    /// <paramref name="quiet"/> = true 时不写状态栏（例如「重新识别」刷新后要保留它自己的提示）。
    /// </summary>
    private void SyncPlaybackWithList(bool quiet = false)
    {
        var playing = _playing;
        if (playing is not null)
        {
            var match = FindById(ViewModel.Items, playing.Id);
            if (match is not null)
            {
                _playing = match;
                if (ViewModel.SelectedItem?.Id != match.Id) ViewModel.SelectedItem = match;
                if (!ReferenceEquals(ItemList.SelectedItem, match)) ItemList.SelectedItem = match;
                return;
            }

            StopPlaybackAndResetUi(quiet ? null : "当前播放的音频已不在筛选结果里，已停止播放。");
            return;
        }

        var selected = ViewModel.SelectedItem;
        var selMatch = selected is null ? null : FindById(ViewModel.Items, selected.Id);
        if (ViewModel.SelectedItem is not null) ViewModel.SelectedItem = selMatch;
        if (!ReferenceEquals(ItemList.SelectedItem, selMatch)) ItemList.SelectedItem = selMatch;
        if (selMatch is null) ResetTimeUi(TimeSpan.Zero);
    }

    private static AudioItem? FindById(IList<AudioItem> items, long id)
    {
        for (int i = 0; i < items.Count; i++)
            if (items[i].Id == id) return items[i];
        return null;
    }

    private static int IndexOfId(IList<AudioItem> items, long id)
    {
        for (int i = 0; i < items.Count; i++)
            if (items[i].Id == id) return i;
        return -1;
    }

    // ── 播放控制 ────────────────────────────────────────────────────────────

    private void OnPlayRequested(AudioItem item) => _ = StartPlaybackAsync(item);

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_isPlaying) { PausePlayback(); return; }

        if (ViewModel.SelectedItem is not { } item)
        {
            ViewModel.Report("请先在列表里选择一条音频。");
            return;
        }

        // 暂停中的同一条（源还在、没播完）→ 直接续播，不必重新读文件
        bool engineReady = _dspActive ? _dsp is not null : _player is not null;
        if (_loadedItemId == item.Id && !_hasEnded && engineReady)
        {
            ResumePlayback(item);
            return;
        }

        _ = StartPlaybackAsync(item);
    }

    private void PausePlayback()
    {
        if (_dspActive)
        {
            // DSP 引擎暂停：ReleaseResources=false，缓存的音频留着，续播不丢进度
            try { _dsp?.Pause(); } catch { /* 忽略 */ }
        }
        else
        {
            try { _player?.Pause(); } catch { /* 播放器已经没了就忽略 */ }
        }
        _isPlaying = false;
        _pauseRequested = true;   // 打开过程中按暂停也不许 MediaOpened 再抢着播
        UpdatePlayButton();
    }

    private void ResumePlayback(AudioItem item)
    {
        try
        {
            if (_dspActive) _dsp!.Play();
            else _player!.Play();
            _isPlaying = true;
            _hasEnded = false;
            _pauseRequested = false;
            UpdatePlayButton();
            _timer?.Start();
            ViewModel.Report($"继续播放：{item.FileName}");
        }
        catch (Exception ex)
        {
            ReportFailure("继续播放失败", ex.Message, item);
        }
    }

    /// <summary>
    /// 开始播放一条音频。详细的时序说明见类型注释。
    ///
    /// 媒体源一律用 <c>MediaSource.CreateFromStorageFile(await StorageFile.GetFileFromPathAsync(item.Path))</c>。
    ///
    /// 为什么不能回到旧代码的 <c>MediaSource.CreateFromUri(new Uri(item.Path))</c>（第 197 行那种写法）：
    /// · <c>new Uri(path)</c> 本身**不抛异常**（实测 <c>file:///I:/%E6%96%B0%E5%BB%BA…/x.wav</c>）——
    ///   「UriFormatException 导致播放失败」这个结论是错的；
    /// · 但 MF 的 file:// 处理**解不了转义后的非 ASCII 路径**：把这个 AbsoluteUri 交给
    ///   MFCreateSourceReaderFromURL 会返回 <c>0x80070003</c>（ERROR_PATH_NOT_FOUND）；
    /// · 同一个文件换成「不转义的中文原样 URL」或「打开文件流（StorageFile 的走法）」都能正常解出音频样本；
    ///   纯 ASCII 路径即使带 %20 转义也正常 —— 问题只出在「转义的非 ASCII 路径」上。
    /// 失败时另有详细日志（Error/ExtendedErrorCode/ErrorMessage + 路径体检）与英文路径副本隔离验证兜底。
    /// </summary>
    private async Task StartPlaybackAsync(AudioItem item)
    {
        int token = ++_playToken;

        try
        {
            EnsurePlayer();
            var player = _player;
            if (player is null)
            {
                ViewModel.Report("播放器不可用，无法播放。");
                return;
            }

            // 每次真正开始一次播放都留一行日志：排查"点了没反应 / 播的还是上一条"时最有用
            AppServices.Current.LogInfo($"开始播放：{item.FileName}（{TuneSpeed:F2}× / {TunePitch:+0;-0;0} 半音）");

            // 列表选中 + 高亮同步（双击 / 播放键 / 上一首下一首 都从这里走）
            ViewModel.SelectedItem = item;
            if (!ReferenceEquals(ItemList.SelectedItem, item))
            {
                ItemList.SelectedItem = ViewModel.Items.Contains(item) ? item : null;
                try { ItemList.ScrollIntoView(item); } catch { /* 不在列表里时忽略 */ }
            }

            if (string.IsNullOrWhiteSpace(item.Path) || !File.Exists(item.Path))
            {
                StopPlayerSource();
                _playing = item;
                ResetTimeUi(TimeSpan.Zero);
                ReportFailure("该文件无法播放", "找不到文件：" + item.Path, item);
                return;
            }

            // ① 切换条目：先把上一条彻底停掉，否则新条目可能不播或两条叠着播
            StopPlayerSource();
            DeleteProbeCopy();                       // 上一个副本已经没人放了，顺手清掉

            // ② 立刻切到新条目：进度清零、时长未知、按钮先回 ▶（真正出声后再变 ⏸）
            _playing = item;
            _isPlaying = false;
            _hasEnded = false;
            _pauseRequested = false;
            _loadedItemId = null;
            ClearPlayerError();                      // 新一次尝试：把上一条的错误位收起
            ResetTimeUi(TimeSpan.Zero);
            _timer?.Start();

            // ③ 变速 / 变调不是中性值 → 走 SoundTouch DSP 引擎（系统播放器做不到变速保持音调）
            if (TuneNeeded && TryStartDsp(item, 0))
            {
                _loadedItemId = item.Id;
                _isPlaying = true;
                _pauseRequested = false;
                UpdatePlayButton();
                ViewModel.Report($"正在播放：{item.FileName}（变速 {TuneSpeed:F2}× · 变调 {TunePitch:+0;-0;0} 半音）");
                return;
            }

            // ④ 路径可能含中文 / 空格：用 StorageFile 而不是手工拼 URI（见方法注释）
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(item.Path);

            // 期间用户又点了别的条目 / 控件已卸载 → 这次结果作废
            if (token != _playToken || !ReferenceEquals(_playing, item)) return;

            // ⑤ 只设 Source，不紧跟 Play()：Source 还没 open 时 Play() 可能被忽略。
            //    AutoPlay=true（EnsurePlayer）会自己开始；OnMediaOpened 里再兜一次 Play() 并在那时切按钮。
            player.Source = MediaSource.CreateFromStorageFile(file);
            _loadedItemId = item.Id;
            // 背景频谱：本条音频开始播放时挂上（暂停 / 停止 / 换源 / 卸载都会 Detach）
            SpectrumService.Current.Attach(new SpectrumService.MediaPlayerClock(player), item.Path);
            ViewModel.Report($"正在打开：{item.FileName}");
        }
        catch (Exception ex)
        {
            if (token != _playToken) return;   // 已经被更晚的一次点击取代
            StopPlayerSource();
            _playing = item;
            ResetTimeUi(TimeSpan.Zero);
            AppServices.Current.LogWarn($"播放失败（设置媒体源阶段）：{MediaDiagnostics.DescribePath(item.Path)} — {ex.GetType().Name}：{ex.Message}");
            ReportFailure("该文件无法播放", ex.Message, item);
        }
    }

    /// <summary>按列表顺序切到上一条 / 下一条：在当前 ViewModel.Items 里找当前项的索引 ±1，越界不动。</summary>
    private void PlaySibling(int delta)
    {
        var items = ViewModel.Items;
        if (items.Count == 0)
        {
            ViewModel.Report("列表里没有音频。");
            return;
        }

        var current = ViewModel.SelectedItem ?? _playing;
        if (current is null)
        {
            ViewModel.Report("先选中一条音频，再用「上一首 / 下一首」。");
            return;
        }

        int index = IndexOfId(items, current.Id);
        if (index < 0)
        {
            ViewModel.Report("当前音频已不在列表里（可能被筛选条件排除）。");
            return;
        }

        int next = index + delta;
        if (next < 0 || next >= items.Count)
        {
            ViewModel.Report(delta > 0 ? "已经是列表最后一条。" : "已经是列表第一条。");
            return;
        }

        // 切换后自动播放；列表选中同步在 StartPlaybackAsync 里做
        _ = StartPlaybackAsync(items[next]);
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => PlaySibling(-1);

    private void Next_Click(object sender, RoutedEventArgs e) => PlaySibling(+1);

    // ── 播放引擎：默认系统 MediaPlayer；变速 / 变调不是中性值时切到 SoundTouch DSP ──────

    /// <summary>变速/变调时的播放引擎（SoundTouch：变速保持音调、变调保持速度）。</summary>
    private DspAudioPlayer? _dsp;

    /// <summary>当前声音是不是从 DSP 引擎出来的（决定进度 / 暂停 / 跳转读哪一边）。</summary>
    private bool _dspActive;

    /// <summary>当前变速倍率（1.00 = 原速）。</summary>
    private double TuneSpeed => SpeedSlider?.Value ?? 1.0;

    /// <summary>当前变调半音数（0 = 原调）。</summary>
    private double TunePitch => PitchSlider?.Value ?? 0.0;

    /// <summary>是否需要 DSP 引擎：只要变速不是 1.00× 或变调不是 0 半音就要。</summary>
    private bool TuneNeeded => Math.Abs(TuneSpeed - 1.0) > 0.001 || Math.Abs(TunePitch) > 0.001;

    /// <summary>当前活动引擎的播放位置（秒）。两个引擎的取法不同，界面统一走这里。</summary>
    private double ActivePositionSeconds()
    {
        if (_dspActive)
        {
            try { return _dsp?.Position.TotalSeconds ?? 0; } catch { return 0; }
        }
        try { return _player?.PlaybackSession?.Position.TotalSeconds ?? 0; } catch { return 0; }
    }

    /// <summary>当前活动引擎的总时长。</summary>
    private TimeSpan ActiveDuration()
    {
        if (_dspActive) return _dsp?.Duration ?? TimeSpan.Zero;
        try { return _player?.PlaybackSession?.NaturalDuration ?? TimeSpan.Zero; } catch { return TimeSpan.Zero; }
    }

    /// <summary>把当前曲目装进 DSP 引擎；解不了（NAudio 不支持的格式）返回 false。</summary>
    /// <param name="autoplay">false = 只装好并定位，保持暂停（切换引擎时用户本来是暂停的就不该自己响）。</param>
    private bool TryStartDsp(AudioItem item, double seconds, bool autoplay = true)
    {
        try
        {
            if (_dsp is null)
            {
                _dsp = new DspAudioPlayer();
                _dsp.PlaybackEnded += OnDspPlaybackEnded;
            }

            _dsp.Load(item.Path);
            _dsp.Volume = (float)_volume;
            _dsp.Speed = TuneSpeed;
            _dsp.Semitones = TunePitch;
            if (seconds > 0) _dsp.Position = TimeSpan.FromSeconds(seconds);
            if (autoplay) _dsp.Play();

            _dspActive = true;
            SpectrumService.Current.Attach(_dsp, item.Path);
            return true;
        }
        catch (Exception ex)
        {
            _dspActive = false;
            AppServices.Current.LogWarn($"变速/变调引擎解不了这条音频，改用系统播放器：{item.FileName} — {ex.GetType().Name}：{ex.Message}");
            return false;
        }
    }

    /// <summary>DSP 引擎自然播完：复位界面。</summary>
    private void OnDspPlaybackEnded()
    {
        UiDispatch.Run(_ui, () =>
        {
            if (!_dspActive) return;
            var finished = _playing;
            _isPlaying = false;
            _hasEnded = true;
            _timer?.Stop();
            UpdatePlayButton();
            SetSliderValue(0);
            PositionText.Text = FormatTime(TimeSpan.Zero);
            ViewModel.Report(finished is null ? "播放结束。" : $"播放结束：{finished.FileName}");
        });
    }

    /// <summary>
    /// 变速 / 变调滑块变化：
    /// · 已经在 DSP 引擎上 → 直接改参数（实时生效，不重新加载文件）；
    /// · 还没有 → 两个值不是中性就切到 DSP，从当前位置接着放。
    /// </summary>
    private void OnTuneChanged(string source, double newValue)
    {
        // XAML 载入阶段（构造函数之前）就会触发一次 ValueChanged，那时两个读数文本还没建出来
        if (SpeedValueText is not null) SpeedValueText.Text = $"{TuneSpeed:F2}×";
        if (PitchValueText is not null) PitchValueText.Text = $"{TunePitch:+0;-0;+0} 半音";

        // 已经在 DSP 引擎上：参数实时改，不重载文件（拖动过程中就能听出变化）
        if (_dspActive && _dsp is not null)
        {
            _dsp.Speed = TuneSpeed;
            _dsp.Semitones = TunePitch;
        }

        // 引擎切换要等值稳定：WinUI 的 Slider 会带动画，一拖动就会连发一串中间值，
        // 中间值直接切引擎会导致反复重载（卡顿 + 破音），所以延后 300ms 用最终值判一次。
        ScheduleEngineEvaluation();
    }

    /// <summary>延后 300ms 再判断该用哪个引擎（避免拖动过程中的一串中间值各切一次）。</summary>
    private void ScheduleEngineEvaluation()
    {
        if (_tuneDebounce is null)
        {
            _tuneDebounce = DispatcherQueue.CreateTimer();
            _tuneDebounce.Interval = TimeSpan.FromMilliseconds(300);
            _tuneDebounce.IsRepeating = false;
            _tuneDebounce.Tick += (_, _) => EvaluateEngine();
        }

        _tuneDebounce.Stop();
        _tuneDebounce.Start();
    }

    /// <summary>按当前（已稳定）的滑块值决定用哪个引擎。</summary>
    private void EvaluateEngine()
    {
        bool wantDsp = TuneNeeded;
        if (wantDsp == _dspActive) return;      // 已经在目标引擎上

        if (wantDsp) SwitchToDspEngine();
        else SwitchBackToMediaPlayer();
    }

    private DispatcherQueueTimer? _tuneDebounce;

    /// <summary>用 DSP 引擎接管当前曲目（从当前进度接着放，暂停中就保持暂停）。</summary>
    private void SwitchToDspEngine()
    {
        var item = _playing;
        if (item is null || _loadedItemId is null) return;   // 没有在放的曲目：等下次播放时自然走 DSP

        bool wasPlaying = _isPlaying;
        double position = ActivePositionSeconds();
        try { _player?.Pause(); } catch { /* 忽略 */ }
        SpectrumService.Current.Detach();

        if (!TryStartDsp(item, position, wasPlaying))
        {
            SetPlayerError($"这条音频不支持变速/变调（引擎解不了），已继续用系统播放器：{item.FileName}");
            if (wasPlaying) { try { _player?.Play(); } catch { /* 忽略 */ } }
            return;
        }

        _hasEnded = false;
        _pauseRequested = !wasPlaying;
        UpdatePlayButton();
        _timer?.Start();
        AppServices.Current.LogInfo($"变速/变调：已切到 DSP 引擎（{TuneSpeed:F2}× / {TunePitch:+0;-0;0} 半音，{(wasPlaying ? "继续播放" : "保持暂停")}）— {item.FileName}");
        ViewModel.Report($"已启用变速/变调引擎：{TuneSpeed:F2}× · {TunePitch:+0;-0;0} 半音");
    }

    /// <summary>回到中性值：把进度交还系统播放器（原来是暂停的就别自己响）。</summary>
    private void SwitchBackToMediaPlayer()
    {
        var item = _playing;
        bool wasPlaying = _isPlaying;
        double position = ActivePositionSeconds();

        _dsp?.Stop();
        _dspActive = false;
        SpectrumService.Current.Detach();

        if (item is null || _loadedItemId is null) return;

        AppServices.Current.LogInfo($"变速/变调：已回到原速原调，交还系统播放器 @ {position:F1}s（{(wasPlaying ? "继续播放" : "保持暂停")}）— {item.FileName}");
        _pauseRequested = !wasPlaying;                    // OnMediaOpened 里据此决定要不要自动播
        _pendingSeekSeconds = Math.Max(0, position);
        _ = StartPlaybackAsync(item);
    }

    // ── 播放器 ──────────────────────────────────────────────────────────────

    private void EnsurePlayer()
    {
        if (_player is not null) return;

        // AutoPlay=true：设完 Source 由媒体自己开始播放，不依赖「设完 Source 紧跟 Play()」——
        // Source 尚未 open 时调用 Play() 有被忽略的情况（这是 MediaPlayer 的常见坑）。
        var player = new MediaPlayer { AutoPlay = true };
        player.Volume = _volume;
        player.MediaOpened += (_, _) => UiDispatch.Run(_ui, OnMediaOpened);
        player.MediaEnded += (_, _) => UiDispatch.Run(_ui, OnMediaEnded);
        player.MediaFailed += (_, args) => UiDispatch.Run(_ui, () => OnMediaFailed(args));
        _player = player;
    }

    /// <summary>
    /// 媒体打开完成：NaturalDuration 这时才可信 → 写进度条 Maximum；
    /// 再兜一次 Play()（AutoPlay 已经在播时是空操作），并在这一刻把按钮切成「暂停」。
    /// </summary>
    private void OnMediaOpened()
    {
        var player = _player;
        UpdateDurationInfo();

        // 从 DSP 引擎交还回来时的续播位置：源打开后才能 seek
        if (_pendingSeekSeconds > 0)
        {
            try
            {
                var target = TimeSpan.FromSeconds(_pendingSeekSeconds);
                if (player is not null && target < player.PlaybackSession.NaturalDuration)
                    player.PlaybackSession.Position = target;
            }
            catch (Exception ex)
            {
                AppServices.Current.LogWarn("交还系统播放器后定位进度失败：" + ex.Message);
            }
            _pendingSeekSeconds = 0;
        }

        // 用户如果在打开过程中按了暂停 / 是从「暂停状态」切引擎过来的，就不要抢着播。
        // 注意 MediaPlayer 建的时候是 AutoPlay=true，所以这里必须**显式 Pause()**，只跳过 Play() 是不够的。
        if (_pauseRequested)
        {
            try { player?.Pause(); } catch (Exception ex) { AppServices.Current.LogWarn("保持暂停状态失败：" + ex.Message); }
        }
        else
        {
            try { player?.Play(); } catch (Exception ex) { AppServices.Current.LogWarn("MediaOpened 后补 Play() 失败：" + ex.Message); }
        }

        _isPlaying = !_pauseRequested;
        _hasEnded = false;
        UpdatePlayButton();
        _timer?.Start();

        if (_probeInFlight) { ReportProbeVerdict(success: true); return; }

        ClearPlayerError();
        if (_playing is { } item) ViewModel.Report($"正在播放：{item.FileName}");
    }

    /// <summary>
    /// 播放结束：复位播放状态 —— _isPlaying=false、按钮图标回 ▶、进度条回到 0:00（总时长保留）。
    /// 「再点一次播放键」会走 StartPlaybackAsync 重新加载（_hasEnded=true），从头开始播。
    /// </summary>
    private void OnMediaEnded()
    {
        var finished = _playing;

        _isPlaying = false;
        _hasEnded = true;
        _timer?.Stop();
        // 播完了就没有数据源了：告诉频谱可以真正停掉（停表省电），背景淡出回渐变
        SpectrumService.Current.Detach();
        UpdatePlayButton();

        try { _player?.PlaybackSession.Position = TimeSpan.Zero; } catch { /* 已结束的会话忽略 */ }
        SetSliderValue(0);
        PositionText.Text = FormatTime(TimeSpan.Zero);

        ViewModel.Report(finished is null ? "播放结束。" : $"播放结束：{finished.FileName}");
    }

    /// <summary>
    /// 播放失败：把 <see cref="MediaPlayerFailedEventArgs"/> 的 **Error / ErrorCode / ExtendedErrorCode / ErrorMessage**
    /// 全部写进日志（<see cref="AppServices.LogWarn"/>），同时显示在播放条的可见错误位与状态栏（不静默吞掉）。
    /// 另外立刻做一次「英文路径副本」隔离验证：副本能播 → 路径/编码问题；副本也不能播 → 解码器/文件问题。
    /// </summary>
    private void OnMediaFailed(MediaPlayerFailedEventArgs args)
    {
        var item = _playing;
        bool isProbe = _probeInFlight;
        string source = isProbe ? (_probeCopyPath ?? "(副本路径丢失)") : (item?.Path ?? "");
        string prefix = isProbe ? "隔离验证（英文路径副本）也失败" : "播放失败（MediaFailed）";

        string detail = MediaDiagnostics.DescribeFailure(args);
        AppServices.Current.LogWarn($"{prefix}：{item?.FileName ?? "—"} — {detail}");
        AppServices.Current.LogWarn($"{prefix} 路径体检：{MediaDiagnostics.DescribePath(source)}");

        // 主动换源 / 停播时 Source 已被置空：这是上一条媒体迟到的回声，只记日志，不打扰用户
        if (_loadedItemId is null) return;

        _isPlaying = false;
        _hasEnded = false;
        _timer?.Stop();
        UpdatePlayButton();

        if (isProbe)
        {
            ReportProbeVerdict(success: false);
            return;
        }

        SetPlayerError($"无法播放：{item?.FileName ?? "当前音频"}（{detail}）");
        ViewModel.Report($"该文件无法播放：{item?.FileName ?? "当前音频"}（{detail}）");

        // 文件在、但播不出来：用「英文路径副本」把原因定下来（同一个条目只做一次）
        if (item is not null && File.Exists(item.Path)) _ = ProbeWithAsciiCopyAsync(item, detail);
    }

    /// <summary>
    /// 隔离验证：把同一个文件复制到纯 ASCII 的临时路径，再用**完全相同**的方式播一次。
    /// · 副本能播 → 原路径（中文 / 空格 / 百分号）是嫌疑点：路径编码问题；
    /// · 副本也不能播 → 更像解码器 / 文件本身的问题（与路径无关）。
    /// 结论写日志 + 播放条错误位 + 状态栏；副本文件在切歌 / 卸载时清理。
    /// </summary>
    private async Task ProbeWithAsciiCopyAsync(AudioItem item, string firstFailure)
    {
        const long MaxProbeBytes = 64L * 1024 * 1024;   // 只为诊断复制文件：超过 64MB 就不折腾了

        if (_probeItemId == item.Id) return;
        _probeItemId = item.Id;

        if (MediaDiagnostics.IsAscii(item.Path))
        {
            AppServices.Current.LogWarn("隔离验证：原路径本身就是纯 ASCII，跳过「英文路径副本」对照（失败更像解码器 / 文件问题）。");
            return;
        }

        long size;
        try { size = new FileInfo(item.Path).Length; } catch { size = -1; }
        if (size > MaxProbeBytes)
        {
            AppServices.Current.LogWarn($"隔离验证：文件 {size / 1024.0 / 1024.0:F1} MB 超过 64 MB，跳过英文路径对照。");
            return;
        }

        string probePath;
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "DFAudioStudio", "probe");
            Directory.CreateDirectory(dir);
            string ext = Path.GetExtension(item.Path);
            if (string.IsNullOrWhiteSpace(ext)) ext = ".wav";
            probePath = Path.Combine(dir, $"probe_{item.Id}{ext}");   // 文件名是纯 ASCII
            File.Copy(item.Path, probePath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppServices.Current.LogWarn("隔离验证：复制到英文路径失败，已跳过：" + ex.Message);
            return;
        }

        AppServices.Current.LogWarn(
            $"隔离验证：已把「{item.FileName}」复制到 {probePath}（副本路径纯ASCII={MediaDiagnostics.IsAscii(probePath)}），准备用同一套方式重播。");

        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(probePath);
            var player = _player;
            if (player is null) return;

            _probeCopyPath = probePath;
            _probeFirstFailure = firstFailure;
            _probeInFlight = true;

            _pauseRequested = false;
            _isPlaying = false;
            player.Source = MediaSource.CreateFromStorageFile(file);
            _loadedItemId = item.Id;
            _timer?.Start();

            SetPlayerError($"无法播放：{item.FileName}（{firstFailure}）· 正在用英文路径副本复验…");
            ViewModel.Report("原路径播放失败，正在用英文路径副本复验（结论会写进日志与播放条错误位）。");
        }
        catch (Exception ex)
        {
            AppServices.Current.LogWarn("隔离验证：读取英文路径副本失败：" + ex.Message);
            _probeInFlight = false;
        }
    }

    /// <summary>给出隔离验证结论（日志 + 播放条错误位 + 状态栏），并结束这一次验证。</summary>
    private void ReportProbeVerdict(bool success)
    {
        string probePath = _probeCopyPath ?? "(副本)";
        string original = _probeFirstFailure ?? "(未记录原因)";
        string originalPath = _playing?.Path ?? "";

        _probeInFlight = false;

        if (success)
        {
            string ok = $"隔离验证结论：同一个文件放到英文路径（{probePath}）后可以正常播放 → 原路径更像是路径 / 编码问题（原路径：{originalPath}；现在播放的是临时副本）。";
            AppServices.Current.LogWarn(ok);
            SetPlayerError($"英文路径副本可播 → 疑似路径 / 编码问题（当前播放临时副本 {probePath}）");
            ViewModel.Report(ok);
        }
        else
        {
            string fail = $"隔离验证结论：把同一个文件复制到英文路径（{probePath}）后依然播放失败 → 更像是解码器 / 文件本身的问题（与路径无关；原路径失败原因：{original}）。";
            AppServices.Current.LogWarn(fail);
            SetPlayerError($"无法播放：{_playing?.FileName ?? "当前音频"}（{original}）；英文路径副本也失败 → 疑似解码器 / 文件问题");
            ViewModel.Report(fail);
        }
    }

    /// <summary>清理英文路径副本（调用前必须确保已经不再播放它：<see cref="StopPlayerSource"/> 之后）。</summary>
    private void DeleteProbeCopy()
    {
        string? path = _probeCopyPath;

        _probeCopyPath = null;
        _probeFirstFailure = null;
        _probeInFlight = false;

        if (string.IsNullOrEmpty(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 删不掉就留在临时目录，不影响播放 */ }
    }

    /// <summary>失败提示统一出口：写日志 + 状态栏 + 播放条可见错误位 + 界面复位。</summary>
    private void ReportFailure(string prefix, string detail, AudioItem? item)
    {
        _isPlaying = false;
        _hasEnded = false;
        UpdatePlayButton();
        _timer?.Stop();

        string name = item?.FileName ?? "当前音频";
        SetPlayerError($"{prefix}：{name}（{detail}）");
        ViewModel.Report($"{prefix}：{name}（{detail}）");
        AppServices.Current.LogWarn($"{prefix}：{item?.Path ?? ""} — {detail}");
    }

    // ── 播放条上的可见错误位（不能只在日志里写） ────────────────────────────

    /// <summary>写 / 收起播放条上的错误提示；传空串或 null 表示收起。</summary>
    private void SetPlayerError(string? message)
    {
        if (PlayerErrorText is null) return;

        if (string.IsNullOrWhiteSpace(message))
        {
            PlayerErrorText.Text = "";
            PlayerErrorText.Visibility = Visibility.Collapsed;
            return;
        }

        PlayerErrorText.Text = message;
        PlayerErrorText.Visibility = Visibility.Visible;
    }

    private void ClearPlayerError() => SetPlayerError(null);

    /// <summary>停掉当前源：两个引擎都停 → 位置归零 → Source=null（换条目 / 卸载 / 失败都走这里）。</summary>
    private void StopPlayerSource()
    {
        // 背景频谱跟着停（能量会自然衰减，背景淡出回到渐变）
        SpectrumService.Current.Detach();

        if (_dspActive || _dsp is not null)
        {
            try { _dsp?.Stop(); } catch { /* 忽略 */ }
            _dspActive = false;
        }

        var player = _player;
        if (player is null) return;

        try { player.Pause(); } catch { /* 忽略 */ }
        try { player.PlaybackSession.Position = TimeSpan.Zero; } catch { /* 源已失效时忽略 */ }
        try { player.Source = null; } catch { /* 忽略 */ }
        _loadedItemId = null;
    }

    /// <summary>停止播放并复位界面（被筛选条件排除时用）。</summary>
    private void StopPlaybackAndResetUi(string? message)
    {
        _playToken++;            // 让还在等 StorageFile 的那次加载作废
        StopPlayerSource();
        DeleteProbeCopy();
        _timer?.Stop();

        _playing = null;
        _isPlaying = false;
        _hasEnded = false;

        UpdatePlayButton();
        ResetTimeUi(TimeSpan.Zero);

        if (!string.IsNullOrEmpty(message)) ViewModel.Report(message);
    }

    /// <summary>按钮图标：E768 = ▶ 播放，E769 = ⏸ 暂停（同时更新 ToolTip 与无障碍名称）。</summary>
    private void UpdatePlayButton()
    {
        var icon = new FontIcon
        {
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 18,
            Glyph = _isPlaying ? "\uE769" : "\uE768",
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White)
        };
        PlayPauseButton.Content = icon;
        ToolTipService.SetToolTip(PlayPauseButton, _isPlaying ? "暂停" : "播放");
        AutomationProperties.SetName(PlayPauseButton, _isPlaying ? "暂停" : "播放");
    }

    // ── 音量 / 速度 ─────────────────────────────────────────────────────────

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressVolume) return;

        _volume = Math.Clamp(e.NewValue, 0d, 1d);
        ApplyVolume();
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        bool muted = _volume <= 0.001;

        if (muted)
        {
            _volume = _volumeBeforeMute > 0.001 ? _volumeBeforeMute : 1.0;
        }
        else
        {
            _volumeBeforeMute = _volume;
            _volume = 0;
        }

        // 程序化改 Slider.Value 时不要触发上面的 handler（避免二次计算）
        _suppressVolume = true;
        try { VolumeSlider.Value = _volume; }
        finally { _suppressVolume = false; }

        ApplyVolume();
        ViewModel.Report(_volume <= 0.001 ? "已静音。" : $"音量 {_volume * 100:F0}%。");
    }

    /// <summary>变速滑块：值变了就更新读数并决定是否切引擎。</summary>
    private void SpeedSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) => OnTuneChanged("变速", e.NewValue);

    /// <summary>变调滑块（半音）：值变了就更新读数并决定是否切引擎。</summary>
    private void PitchSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) => OnTuneChanged("变调", e.NewValue);

    private void ApplyVolume()
    {
        EnsurePlayer();
        if (_player is not null)
        {
            try { _player.Volume = _volume; }
            catch (Exception ex) { AppServices.Current.LogWarn("设置音量失败：" + ex.Message); }
        }

        if (_dsp is not null)
        {
            try { _dsp.Volume = (float)_volume; }
            catch (Exception ex) { AppServices.Current.LogWarn("设置音量失败（DSP 引擎）：" + ex.Message); }
        }

        UpdateVolumeIcon();
    }

    private void UpdateVolumeIcon()
    {
        if (VolumeIcon is null) return;   // 构造早期可能还没生成
        bool muted = _volume <= 0.001;
        VolumeIcon.Glyph = muted ? "\uE74F" : "\uE767";   // 静音 / 喇叭
        ToolTipService.SetToolTip(MuteButton, muted ? "取消静音" : "静音");
    }

    // ── 进度条 ──────────────────────────────────────────────────────────────

    private void EnsureTimer()
    {
        if (_timer is not null) return;

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.IsRepeating = true;
        _timer.Tick += OnTimerTick;
    }

    private void OnTimerTick(DispatcherQueueTimer sender, object args) => UpdateDurationInfo();

    /// <summary>
    /// 定时器 / MediaOpened 共用的同步逻辑（两个引擎都走这里）：
    /// · 总时长用活动引擎的时长写进 Slider.Maximum，**但时长还是 0（未知）时绝对不写** ——
    ///   Maximum=0 会让 Slider 完全拖不动；
    /// · 拖动中（_scrubbing）不回写 Value，否则手还在拖就被定时器拉回去；
    /// · 其余时候把当前位置写进滑块并刷新时间码。
    /// </summary>
    private void UpdateDurationInfo()
    {
        if (!_dspActive && _player?.PlaybackSession is null) return;

        var duration = ActiveDuration();
        double total = duration.TotalSeconds;
        if (double.IsFinite(total) && total > 0 && Math.Abs(ProgressSlider.Maximum - total) > 0.05)
            SetSliderMaximum(total);

        TotalTimeText.Text = FormatTime(duration);

        if (_scrubbing) return;

        double position = ActivePositionSeconds();
        SetSliderValue(position);
        PositionText.Text = FormatTime(TimeSpan.FromSeconds(position));
    }

    /// <summary>写 Slider.Value（带抑制标志，避免被当成用户 seek）。</summary>
    private void SetSliderValue(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) seconds = 0;

        _suppressSlider = true;
        try
        {
            ProgressSlider.Value = Math.Min(seconds, ProgressSlider.Maximum);
        }
        finally
        {
            _suppressSlider = false;
        }
    }

    /// <summary>设置滑条上限（同样带抑制标志：Maximum 变小会把 Value 夹回去并触发 ValueChanged → 假 seek）。</summary>
    private void SetSliderMaximum(double totalSeconds)
    {
        _suppressSlider = true;
        try
        {
            ProgressSlider.Maximum = totalSeconds;
        }
        finally
        {
            _suppressSlider = false;
        }
    }

    /// <summary>进度 / 时间码一起复位；total 传零表示「总时长也还不知道」。</summary>
    private void ResetTimeUi(TimeSpan total)
    {
        SetSliderValue(0);
        PositionText.Text = FormatTime(TimeSpan.Zero);
        TotalTimeText.Text = FormatTime(total);
    }

    private void ProgressSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSlider) return;   // 程序化写入（定时器 / 复位）不当成用户拖动

        // 拖动中就实时 seek（定时器此时不会回写 Value，所以不会被顶回去）
        if (_dspActive)
        {
            try { if (_dsp is not null) _dsp.Position = TimeSpan.FromSeconds(e.NewValue); }
            catch { /* 忽略 */ }
        }
        else
        {
            var session = _player?.PlaybackSession;
            if (session is null) return;
            try { session.Position = TimeSpan.FromSeconds(e.NewValue); }
            catch { /* 不可跳转的媒体忽略 */ }
        }

        PositionText.Text = FormatTime(TimeSpan.FromSeconds(e.NewValue));
    }

    private void ProgressSlider_PointerPressed(object sender, PointerRoutedEventArgs e) => _scrubbing = true;

    private void ProgressSlider_PointerReleased(object sender, PointerRoutedEventArgs e) => EndScrub();

    private void ProgressSlider_PointerCanceled(object sender, PointerRoutedEventArgs e) => EndScrub();

    private void ProgressSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndScrub();

    /// <summary>拖动结束：解除抑制并按活动引擎的真实位置再同步一次。</summary>
    private void EndScrub()
    {
        if (!_scrubbing) return;
        _scrubbing = false;

        if (!_dspActive && _player?.PlaybackSession is null) return;

        double position = ActivePositionSeconds();
        SetSliderValue(position);
        PositionText.Text = FormatTime(TimeSpan.FromSeconds(position));
    }

    private static string FormatTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}
