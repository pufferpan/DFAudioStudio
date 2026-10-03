using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using DFAudioStudio.App.Pages;
using DFAudioStudio.App.Services;
using DFAudioStudio.App.ViewModels;
using DFAudioStudio.Core.Models;
using DFAudioStudio.Core.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI;
using Windows.Graphics;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace DFAudioStudio.App;

/// <summary>
/// 主窗口：左侧 NavigationView 承载真实层级导航（概览 / 分区导航 / 识别队列 / 设置）+ 右侧 Frame 承载页面。
///
/// 启动路径刻意保持轻量：构造函数只搭界面骨架，**不查库、不加载模型、不启动识别队列**；
/// 分区树等窗口显示出来之后在 Activated 里异步建（Task.Run 查库 → DispatcherQueue 回 UI 线程填 MenuItems）。
///
/// 侧栏用 LeftCompact（关闭时是 48px 图标栏，不会整个消失），PaneHeader 按 IsPaneOpen 切换
/// 「展开态 / 紧凑态」两套布局；主题切换（含淡入淡出动画）也在这个窗口里，因为它要操作根 Grid 与覆盖层。
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>非分区页面的路由（分区叶子走 BrowserScope，不进这张表）。</summary>
    private readonly Dictionary<string, Type> _routes = new(StringComparer.Ordinal);

    private bool _buildingTree;
    private bool _suppressNavigation;
    private bool _firstActivated;
    private bool _bgAnimationStarted;

    /// <summary>分区树是否正在重建（决定 TreeRing 是否该转）。</summary>
    private bool _treeBusy;

    // ── 主题切换（标志位 + 待处理目标，保证快速连点不叠加） ──────────────────
    private static readonly TimeSpan ThemeFadeInDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan ThemeFadeOutDuration = TimeSpan.FromMilliseconds(220);

    private bool _themeAnimating;
    private ElementTheme? _pendingTheme;
    private Storyboard? _themeStoryboard;
    private bool _suppressThemeToggle;

    // ── 启动片头（视频覆盖层 + 一次性「关于」提示） ──────────────────────────

    /// <summary>片头最长播放时间：不管视频能不能播、事件有没有回来，30 秒后一定关掉覆盖层，绝不把主界面卡在片头后面。</summary>
    private static readonly TimeSpan SplashTimeout = TimeSpan.FromSeconds(30);

    private MediaPlayer? _splashMediaPlayer;
    private DispatcherQueueTimer? _splashTimer;
    private bool _splashStarted;      // 只启动一次（RootGrid.Loaded 与首次 Activated 都会调到这里）
    private bool _splashClosing;      // 收尾只做一次（播放结束 / 失败 / 跳过 / 超时可能先后到达）
    private bool _aboutShown;         // 「关于」同一次运行只弹一次

    public MainWindow()
    {
        InitializeComponent();
        Title = "三角洲音频工坊 · DFAudioStudio";
        ApplyWindowIcon();

        _routes["dashboard"] = typeof(DashboardPage);
        _routes["queue"] = typeof(QueuePage);
        _routes["tutorial"] = typeof(TutorialPage);
        _routes["settings"] = typeof(SettingsPage);

        try { AppWindow.Resize(new SizeInt32(1440, 940)); }
        catch (Exception ex) { AppServices.Current.LogWarn("调整窗口大小失败：" + ex.Message); }

        InitTheme();
        InitPaneAdaptiveVisuals();
        InitSpectrumVisual();

#if ALPHA_EXPIRY
        // Alpha 定时过期版：先把覆盖层铺上（校验通过前界面不可用），并标注版本与过期日。
        // 注意：校验复用本窗口的覆盖层，**不额外创建窗口**——先建后关一个窗口会让主窗口渲染后原生崩溃。
        EnsureAlphaOverlay();
        Title = "三角洲音频工坊 · DFAudioStudio（Alpha测试版 · 2026-09-24 过期）";
#endif

        SelectInitialItem();

        // 树要等窗口先显示出来再填，否则会拖慢启动。
        Activated += MainWindow_Activated;

        // 静态事件订阅：窗口关掉就退订，避免残留引用；顺带把片头兜底定时器停掉，
        // 免得窗口已经关了还去弹「关于」。
        Closed += (_, _) =>
        {
            ThemeService.ModeChanged -= OnThemeModeChanged;
            StopSplashOnClose();
        };
    }

    // ── 主题初始化 ──────────────────────────────────────────────────────────

    /// <summary>设置窗口 / 任务栏图标（exe 已内嵌图标，这里显式再设一次，保证 WinUI 窗口标题栏也用同一张图）。</summary>
    private void ApplyWindowIcon()
    {
        try
        {
            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(icon))
            {
                AppWindow.SetIcon(icon);
                AppServices.Current.LogInfo("窗口图标已设置：" + icon);
            }
        }
        catch (Exception ex)
        {
            AppServices.Current.LogWarn("设置窗口图标失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 启动时应用设置里的主题（System → ElementTheme.Default / Light → Light / Dark → Dark）。
    /// 只在这一刻直接赋值、不播动画；之后的变化一律走 OnThemeModeChanged → 淡入淡出。
    /// </summary>
    private void InitTheme()
    {
        try
        {
            ThemeService.Initialize();
            RootGrid.RequestedTheme = ThemeService.ToElementTheme(ThemeService.Current);
            UpdateThemeToggleVisual();
            ThemeService.ModeChanged += OnThemeModeChanged;
        }
        catch (Exception ex)
        {
            AppServices.Current.LogWarn("初始化主题失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 侧栏折叠自适应：
    /// · TreeRing 在折叠态必须折叠（48px 里挤不下）
    /// · 展开态/紧凑态两套 PaneHeader 与主题开关按钮按 IsPaneOpen 切换
    /// （原先用 x:Bind Nav.IsPaneOpen 做可见性，但 WinUI 在 Window 根上不支持该写法，
    ///   编译报 CS1503「无法从 MainWindow 转换为 FrameworkElement」，故改用代码同步）
    /// </summary>
    private void InitPaneAdaptiveVisuals()
    {
        try
        {
            Nav.RegisterPropertyChangedCallback(NavigationView.IsPaneOpenProperty, (_, _) =>
            {
                UpdateTreeRingVisibility();
                UpdatePaneAdaptiveVisuals();
            });
        }
        catch (Exception ex)
        {
            AppServices.Current.LogWarn("监听侧栏折叠状态失败：" + ex.Message);
        }

        UpdateTreeRingVisibility();
        UpdatePaneAdaptiveVisuals();

        void UpdatePaneAdaptiveVisuals()
        {
            bool open = Nav.IsPaneOpen;
            PaneHeaderExpanded.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            PaneHeaderCompact.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
            ThemeToggle.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            ThemeCompactButton.Visibility = open ? Visibility.Collapsed : Visibility.Visible;

            // 折叠成 48px 图标栏时，把导航项里的文字也收掉：
            // WinUI 只会自动隐藏「字符串内容」那类标签，自定义 Content（分区导航 + 进度圈）不会；
            // 而且在本项目的自绘主题下，字符串标签的紧凑态隐藏并不可靠（会被 48px 裁成一条，显示不全），
            // 所以这里显式收：折叠 → Content 置空（图标照常显示）；展开 → 原样恢复。
            SetCompactLabel(NavItemDashboard, "概览", open);
            SetCompactLabel(NavItemQueue, "识别队列", open);
            SetCompactLabel(NavItemTutorial, "新手教程", open);
            SetCompactLabel(NavItemSettings, "设置", open);
            PartitionRootLabel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;

            AppServices.Current.LogInfo(
                $"侧栏{(open ? "展开" : "折叠")}：导航项文字已{(open ? "恢复" : "隐藏")}（面板宽 {(open ? Nav.OpenPaneLength : Nav.CompactPaneLength):F0}）");
        }

    }

    /// <summary>
    /// 侧栏折叠时收起导航项的文字（展开时恢复）。
    /// 折叠态把 Content 置空，并补上 ToolTip / 无障碍名称，避免折叠后既没有文字也没有提示。
    /// </summary>
    private static void SetCompactLabel(NavigationViewItem item, string label, bool paneOpen)
    {
        if (paneOpen)
        {
            if (item.Content is not string) item.Content = label;
            item.ClearValue(ToolTipService.ToolTipProperty);
            item.ClearValue(AutomationProperties.NameProperty);
            return;
        }

        item.Content = null;
        ToolTipService.SetToolTip(item, label);
        AutomationProperties.SetName(item, label);
    }


    // ── 音乐频谱背景（播放音频时随频谱变化，停播后回到三色渐变）─────────────────


    /// <summary>底部柱子（数量 = SpectrumService.BandCount）。</summary>
    private Microsoft.UI.Xaml.Shapes.Rectangle[] _spectrumBars = Array.Empty<Microsoft.UI.Xaml.Shapes.Rectangle>();

    /// <summary>建频谱层：生成柱子 + 按主题上色 + 订阅播放状态与每帧数据。</summary>
    private void InitSpectrumVisual()
    {
        try
        {
            BgSpectrumBars.ColumnDefinitions.Clear();
            BgSpectrumBars.Children.Clear();

            var bars = new List<Microsoft.UI.Xaml.Shapes.Rectangle>(SpectrumService.BandCount);
            for (int i = 0; i < SpectrumService.BandCount; i++)
            {
                BgSpectrumBars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var bar = new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    RadiusX = 3,
                    RadiusY = 3,
                    Margin = new Thickness(1.5, 0, 1.5, 0),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Height = 0,
                };
                Grid.SetColumn(bar, i);
                BgSpectrumBars.Children.Add(bar);
                bars.Add(bar);
            }

            _spectrumBars = bars.ToArray();
            RefreshSpectrumTheme();

            SpectrumService.Current.ActiveChanged += OnSpectrumActiveChanged;
            SpectrumService.Current.FrameUpdated += OnSpectrumFrame;
            AppServices.Current.LogInfo("频谱背景：已就绪（播放音频时随频谱跳动，停播后淡出回到渐变）");
        }
        catch (Exception ex)
        {
            AppServices.Current.LogWarn("初始化频谱背景失败：" + ex.Message);
        }
    }

    /// <summary>柱子与脉冲罩的颜色按当前主题取（浅色用主蓝，深色用亮蓝）。</summary>
    private void RefreshSpectrumTheme()
    {
        try
        {
            bool dark = RootGrid.ActualTheme == ElementTheme.Dark;
            var bottom = dark ? Color.FromArgb(0xC8, 0x8A, 0xB4, 0xFF) : Color.FromArgb(0xC8, 0x0B, 0x57, 0xD0);
            var top = dark ? Color.FromArgb(0x00, 0x8A, 0xB4, 0xFF) : Color.FromArgb(0x00, 0x0B, 0x57, 0xD0);

            foreach (var bar in _spectrumBars)
            {
                bar.Fill = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 1),      // 底部实、顶部淡，像从地面升起的光
                    EndPoint = new Point(0, 0),
                    GradientStops =
                    {
                        new GradientStop { Color = bottom, Offset = 0 },
                        new GradientStop { Color = top, Offset = 1 },
                    },
                };
            }

            BgSpectrumPulse.Background = new LinearGradientBrush
            {
                StartPoint = new Point(0.5, 1),
                EndPoint = new Point(0.5, 0),
                GradientStops =
                {
                    new GradientStop
                    {
                        Color = dark ? Color.FromArgb(0x38, 0x6E, 0xA8, 0xFF) : Color.FromArgb(0x30, 0x0B, 0x57, 0xD0),
                        Offset = 0,
                    },
                    new GradientStop { Color = Colors.Transparent, Offset = 1 },
                },
            };
        }
        catch (Exception ex)
        {
            AppServices.Current.LogWarn("刷新频谱配色失败：" + ex.Message);
        }
    }

    /// <summary>播放状态变化：开始 → 频谱层淡入；停止 → 淡出，回到原来的三色渐变。</summary>
    private void OnSpectrumActiveChanged(bool active)
    {
        try
        {
            RefreshSpectrumTheme();
            var animation = new DoubleAnimation
            {
                To = active ? 0.95 : 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(active ? 520 : 700)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(animation, BgSpectrumLayer);
            Storyboard.SetTargetProperty(animation, "Opacity");
            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Begin();

            AppServices.Current.LogInfo(active
                ? "频谱背景：淡入（音乐播放中）"
                : "频谱背景：淡出（没有播放，回到渐变）");
        }
        catch (Exception ex)
        {
            AppServices.Current.LogWarn("频谱背景淡入淡出失败：" + ex.Message);
        }
    }

    /// <summary>每帧更新柱子高度与脉冲罩（约 30fps，由 SpectrumService 驱动）。</summary>
    private void OnSpectrumFrame()
    {
        try
        {
            if (_spectrumBars.Length == 0) return;

            double strip = Math.Clamp(RootGrid.ActualHeight * 0.30, 120, 300);
            if (Math.Abs(BgSpectrumBars.Height - strip) > 1) BgSpectrumBars.Height = strip;

            var bands = SpectrumService.Current.Snapshot();
            for (int i = 0; i < _spectrumBars.Length && i < bands.Length; i++)
            {
                double height = bands[i] * strip;
                _spectrumBars[i].Height = height < 2 ? 0 : height;   // 静音频段直接归零，视觉更干净
            }

            BgSpectrumPulse.Opacity = Math.Clamp(SpectrumService.Current.Level * 1.2, 0, 0.9);
        }
        catch
        {
            // 每帧回调里出错就直接跳过这一帧
        }
    }

    /// <summary>
    /// 主题模式变化（切换开关 / 设置页下拉都会走到这里）。
    /// 已经在播动画时只记下最新目标，由正在跑的循环取走 —— 连点不会叠加动画。
    /// </summary>
    private void OnThemeModeChanged(AppThemeMode mode)
    {
        var target = ThemeService.ToElementTheme(mode);
        UpdateThemeToggleVisual();

        // 看不到变化的切换就不放动画：例如「浅色 → 跟随系统」而系统本来就是浅色，
        // 这时只需要把 RequestedTheme 从 Light 改成 Default，直接赋值即可，别白闪一下。
        if (RootGrid.RequestedTheme == target || RootGrid.ActualTheme == ThemeService.ResolveVisualTheme(target))
        {
            ApplyThemeToRoot(target);
            return;
        }

        _pendingTheme = target;
        if (_themeAnimating) return;

        _ = RunThemeTransitionLoopAsync();
    }

    /// <summary>串行播放主题切换动画；期间新来的请求只替换「下一个目标」。</summary>
    private async Task RunThemeTransitionLoopAsync()
    {
        _themeAnimating = true;
        try
        {
            while (_pendingTheme is { } next)
            {
                _pendingTheme = null;
                await PlayThemeTransitionAsync(next);
            }
        }
        catch (Exception ex)
        {
            AppServices.Current.LogWarn("主题切换动画失败：" + ex.Message);

            // 动画出问题也不能把主题留在半路：直接把最后一次的目标应用上。
            if (_pendingTheme is { } fallback)
            {
                _pendingTheme = null;
                ApplyThemeToRoot(fallback);
            }
        }
        finally
        {
            _themeAnimating = false;
            StopThemeStoryboard();
            ThemeOverlay.Opacity = 0;
            ThemeOverlay.Visibility = Visibility.Collapsed;
            UpdateThemeToggleVisual();
        }
    }

    /// <summary>
    /// 一次完整的切换：覆盖层淡入到「目标主题的页面底色」(180ms) → 改 RootGrid.RequestedTheme →
    /// 淡出 (220ms)。淡入期间覆盖层是全屏不透明的，所以换主题的那一瞬间用户看不到跳变。
    /// </summary>
    private async Task PlayThemeTransitionAsync(ElementTheme target)
    {
        var visual = ThemeService.ResolveVisualTheme(target);

        try { ThemeOverlay.Background = ThemeService.PageBackgroundBrush(visual); }
        catch (Exception ex) { AppServices.Current.LogWarn("取主题底色失败：" + ex.Message); }

        // ① 淡入
        ThemeOverlay.Visibility = Visibility.Visible;
        ThemeOverlay.Opacity = 0;
        BeginThemeFade(0, 1, ThemeFadeInDuration);
        await DelayOnUiThreadAsync(ThemeFadeInDuration);

        // ② 覆盖层已经不透明：这时候换主题
        ApplyThemeToRoot(target);

        // ③ 淡出，露出新主题
        BeginThemeFade(1, 0, ThemeFadeOutDuration);
        await DelayOnUiThreadAsync(ThemeFadeOutDuration);

        StopThemeStoryboard();
        ThemeOverlay.Opacity = 0;
        ThemeOverlay.Visibility = Visibility.Collapsed;
    }

    private void BeginThemeFade(double from, double to, TimeSpan duration)
    {
        StopThemeStoryboard();

        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(duration),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(animation, ThemeOverlay);
        Storyboard.SetTargetProperty(animation, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        _themeStoryboard = storyboard;
        storyboard.Begin();
    }

    private void StopThemeStoryboard()
    {
        try { _themeStoryboard?.Stop(); } catch { /* 已经停了就忽略 */ }
        _themeStoryboard = null;
    }

    private void ApplyThemeToRoot(ElementTheme target)
    {
        try { RootGrid.RequestedTheme = target; }
        catch (Exception ex) { AppServices.Current.LogWarn("应用主题失败：" + ex.Message); }
    }

    /// <summary>等一段时间后**回到 UI 线程**再继续（不依赖 SynchronizationContext，XAML 操作永远在 UI 线程）。</summary>
    private async Task DelayOnUiThreadAsync(TimeSpan delay)
    {
        await Task.Delay(delay).ConfigureAwait(false);
        if (DispatcherQueue.HasThreadAccess) return;

        var completion = new TaskCompletionSource<object?>();
        if (!DispatcherQueue.TryEnqueue(() => completion.TrySetResult(null)))
            completion.TrySetResult(null);   // 队列已经关了：让流程继续走完，别把界面卡在动画中间

        await completion.Task.ConfigureAwait(false);
    }

    /// <summary>把「侧栏开关 + 紧凑态图标按钮」同步到当前主题（两种入口改的是同一个值）。</summary>
    private void UpdateThemeToggleVisual()
    {
        try
        {
            var visual = ThemeService.ResolveVisualTheme(RootGrid.RequestedTheme);
            bool dark = visual == ElementTheme.Dark;

            _suppressThemeToggle = true;
            ThemeToggle.IsOn = dark;
            ThemeCompactIcon.Glyph = dark ? "\uE708" : "\uE706";   // 月亮 / 太阳
        }
        catch (Exception ex)
        {
            AppServices.Current.LogWarn("同步主题开关失败：" + ex.Message);
        }
        finally
        {
            _suppressThemeToggle = false;
        }
    }

    /// <summary>侧栏里的大开关：切换即切主题并存设置（AppSettings.Theme）。</summary>
    private void ThemeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressThemeToggle) return;   // 程序化赋值不当作用户切换

        ThemeService.SetMode(ThemeToggle.IsOn ? AppThemeMode.Dark : AppThemeMode.Light);
    }

    /// <summary>紧凑态（48px）里的图标按钮：在浅色 / 深色之间对调。</summary>
    private void ThemeCompact_Click(object sender, RoutedEventArgs e)
    {
        var current = ThemeService.ResolveVisualTheme(RootGrid.RequestedTheme);
        ThemeService.SetMode(ThemeService.OppositeOfCurrentVisual(current));
    }

    // ── 启动片头（XAML 里的 SplashOverlay：黑底 + MediaPlayerElement + 右下角「跳过」） ──

    /// <summary>
    /// 启动片头：设置里开着、并且视频文件确实存在 → 显示铺满窗口的覆盖层并播放；
    /// 关着 / 文件不存在 → 直接跳过片头（但「关于」提示照弹一次）。
    /// 幂等：RootGrid.Loaded 与首次 Activated 都会调到这里，用 <see cref="_splashStarted"/> 兜住。
    /// </summary>
    private void TryStartSplash()
    {
        if (_splashStarted) return;
#if ALPHA_EXPIRY
        if (!_alphaGatePassed) return;      // Alpha 版：时间校验通过前不播片头
#endif
        _splashStarted = true;

        try
        {
            var settings = AppServices.Current.Settings;

            if (!settings.PlaySplashOnStartup)
            {
                AppServices.Current.LogInfo("设置里关闭了「启动时播放片头」，直接进入主界面");
                ShowAboutDialogOnce();
                return;
            }

            string path = settings.SplashVideoPath ?? "";
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                AppServices.Current.LogWarn("启动片头视频不可用，已跳过：" +
                    (string.IsNullOrWhiteSpace(path) ? "（未设置 SplashVideoPath）" : path));
                ShowAboutDialogOnce();
                return;
            }

            AppServices.Current.LogInfo("开始播放启动片头：" + path);
            SplashOverlay.Visibility = Visibility.Visible;
            StartSplashTimeoutTimer();
            _ = PlaySplashAsync(path);
        }
        catch (Exception ex)
        {
            AppServices.Current.LogWarn("启动片头失败：" + ex.Message);
            CloseSplashOnUiThread("启动异常");
        }
    }

    /// <summary>30 秒兜底定时器：视频卡住（解码器无响应 / 事件没回来）也要保证覆盖层最终关闭。</summary>
    private void StartSplashTimeoutTimer()
    {
        try
        {
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = SplashTimeout;
            timer.IsRepeating = false;
            timer.Tick += (_, _) => CloseSplash("片头超时（30 秒兜底）");
            _splashTimer = timer;
            timer.Start();
        }
        catch (Exception ex)
        {
            AppServices.Current.LogWarn("启动片头兜底定时器创建失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 真正加载并播放片头。媒体源与音频播放条**共用同一套写法**：
    /// <c>MediaSource.CreateFromStorageFile(await StorageFile.GetFileFromPathAsync(path))</c>，
    /// 并且在 <c>MediaOpened</c> 里补一次 <c>Play()</c>（AutoPlay=true 已经会开始播，这里是兜底），
    /// 不在设完 Source 之后立刻 Play()（Source 还没 open 时 Play() 有被忽略的情况）。
    ///
    /// 为什么不能改成 URI：实测 <c>new Uri(@"G:\工程\mojang.mp4")</c> 不会抛 UriFormatException
    /// （得到 <c>file:///G:/%E5%B7%A5%E7%A8%8B/mojang.mp4</c>），但**这个转义后的 URL 交给 MF 会失败**：
    /// MFCreateSourceReaderFromURL 返回 0x80070003（ERROR_PATH_NOT_FOUND，MF 解不了转义的非 ASCII 路径），
    /// 而同一个文件走「文件流 / StorageFile」能正常解出样本。
    /// 失败时把 Error / ErrorCode / ExtendedErrorCode / ErrorMessage 与路径体检一起写进日志。
    /// </summary>
    private async Task PlaySplashAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);

            var player = new MediaPlayer { AutoPlay = true };
            player.MediaOpened += (_, _) =>
            {
                AppServices.Current.LogInfo("片头视频已开始播放（MediaOpened）");
                try { player.Play(); } catch { /* 兜底重复 Play，失败也不影响（AutoPlay 已经在播） */ }
            };
            player.MediaEnded += (_, _) => CloseSplashOnUiThread("片头播放结束");
            player.MediaFailed += (_, args) =>
            {
                // 与音频播放条共用同一套诊断写法：原因全部进日志，覆盖层一样会收掉（不留黑屏）
                AppServices.Current.LogWarn("片头视频播放失败（MediaFailed）：" + MediaDiagnostics.DescribeFailure(args));
                AppServices.Current.LogWarn("片头视频路径体检：" + MediaDiagnostics.DescribePath(path));
                CloseSplashOnUiThread("片头播放失败");
            };

            _splashMediaPlayer = player;
            SplashPlayer.SetMediaPlayer(player);

            // 只设 Source：XAML 上是 AutoPlay="True"，自建播放器也设了 AutoPlay=true，
            // 打开完成后由 MediaOpened 兜一次 Play()。
            player.Source = MediaSource.CreateFromStorageFile(file);
        }
        catch (Exception ex)
        {
            AppServices.Current.LogWarn("片头视频加载失败：" + MediaDiagnostics.DescribePath(path) + " — " + ex.Message);
            CloseSplashOnUiThread("片头加载失败");
        }
    }

    /// <summary>MediaPlayer 的事件在后台线程触发：统一切回 UI 线程再收尾。</summary>
    private void CloseSplashOnUiThread(string reason)
    {
        if (DispatcherQueue.HasThreadAccess) { CloseSplash(reason); return; }
        DispatcherQueue.TryEnqueue(() => CloseSplash(reason));
    }

    /// <summary>
    /// 关闭片头覆盖层：停掉兜底定时器 → 停播 + 清源 → 收起覆盖层 → 弹一次「关于」。
    /// 幂等（<see cref="_splashClosing"/>），所以「播放结束 / 播放失败 / 点跳过 / 30 秒超时」同时到达也不会重复收尾。
    ///
    /// 播放器**刻意不 Dispose**：它已经被 MediaPlayerElement 接住（SetMediaPlayer），
    /// 强行解绑 / 释放反而可能在控件内部留下悬空引用；覆盖层收起后不再渲染，视频文件句柄也随之释放。
    /// </summary>
    private void CloseSplash(string reason)
    {
        if (_splashClosing) return;
        _splashClosing = true;

        try
        {
            _splashTimer?.Stop();
            _splashTimer = null;      // 不留野定时器

            var player = _splashMediaPlayer;
            _splashMediaPlayer = null;
            if (player is not null)
            {
                try { player.Pause(); } catch { /* 已经停了就忽略 */ }
                try { player.Source = null; } catch { /* 已经清过就忽略 */ }
            }

            SplashOverlay.Visibility = Visibility.Collapsed;
            AppServices.Current.LogInfo("启动片头已关闭：" + reason);
        }
        catch (Exception ex)
        {
            AppServices.Current.LogWarn("关闭启动片头覆盖层失败：" + ex.Message);
        }
        finally
        {
            // 首次使用：片头放完先上「整屏引导」（引导本身就是介绍，这时候再叠一个「关于」很啰嗦）；
            // 非首次：照旧弹一次「关于」。
            if (IsOnboardingPending()) MaybeShowOnboarding();
            else ShowAboutDialogOnce();
        }
    }

    /// <summary>右下角「跳过」：立即收尾。</summary>
    private void SplashSkip_Click(object sender, RoutedEventArgs e) => CloseSplash("用户点了「跳过」");

    /// <summary>
    /// 窗口关闭：停掉片头兜底定时器，并把「收尾」与「关于」都标记为已完成 ——
    /// 之后任何迟到的 MediaEnded / 超时回调都会直接返回，不会在窗口已经关掉之后再去弹框。
    /// </summary>
    private void StopSplashOnClose()
    {
        try
        {
            _splashClosing = true;
            _aboutShown = true;
            _splashTimer?.Stop();
            _splashTimer = null;
        }
        catch { /* 关闭阶段的异常忽略 */ }
    }

    // ── 「关于」（每次运行只弹一次） ────────────────────────────────────────

    private void ShowAboutDialogOnce()
    {
        if (_aboutShown) return;
        _aboutShown = true;
        _ = ShowAboutDialogAsync();
    }

    private async Task ShowAboutDialogAsync()
    {
        try
        {
            // 先让覆盖层收干净这一帧，再弹框（否则同帧里收覆盖层 + 弹对话框会互相打断）
            await Task.Yield();

            // WinUI 3 里 ContentDialog 必须显式指定 XamlRoot，否则 ShowAsync 抛异常
            var xamlRoot = Content?.XamlRoot ?? RootGrid.XamlRoot;
            if (xamlRoot is null)
            {
                AppServices.Current.LogWarn("暂时拿不到 XamlRoot，「关于」提示已跳过");
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = "关于",
                Content = "该软件由 河豚潘PufferPan 制作，完全免费",
                CloseButtonText = "知道了",
                DefaultButton = ContentDialogButton.Close
            };

            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            AppServices.Current.LogWarn("显示「关于」提示失败：" + ex.Message);
        }
    }

    // ── 首次使用引导（铺满整个窗口；只在没走过的时候自动弹） ────────────────

    /// <summary>引导一共几步（1 这是什么 / 2 音频目录 / 3 模型 / 4 开始用）。</summary>
    private const int OnboardingSteps = 4;

    private int _onboardingStep = 1;
    private bool _onboardingChecked;
    private readonly List<Microsoft.UI.Xaml.Shapes.Ellipse> _onboardingDots = new();

    /// <summary>第一次打开软件（或用户从教程页点「重看首次引导」）时，整屏铺开引导。</summary>
    public void ShowOnboarding()
    {
        try
        {
            ObMediaBox.Text = AppServices.Current.Settings.MediaRoot ?? "";
            ObLocalizedBox.Text = AppServices.Current.Settings.LocalizedRoot ?? "";
            RefreshOnboardingModelHint();
            ObPathHint.Text = "";

            OnboardingLayer.Visibility = Visibility.Visible;
            OnboardingLayer.Opacity = 0;
            SetOnboardingStep(1, animateStep: false);

            var fade = new DoubleAnimation
            {
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(260)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(fade, OnboardingLayer);
            Storyboard.SetTargetProperty(fade, "Opacity");
            var sb = new Storyboard();
            sb.Children.Add(fade);
            sb.Begin();

            AnimateStepIn(CurrentOnboardingPanel());
            AppServices.Current.LogInfo("首次使用引导：已显示（第 1 步）");
        }
        catch (Exception ex)
        {
            // 引导出问题绝不能把用户挡在外面：直接判为"已走过"
            AppServices.Current.LogWarn("显示首次使用引导失败，已跳过：" + ex.Message);
            OnboardingLayer.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>关掉引导；<paramref name="completed"/> = true 时记进设置，之后不再自动弹。</summary>
    private void CloseOnboarding(bool completed)
    {
        OnboardingLayer.Visibility = Visibility.Collapsed;
        OnboardingLayer.Opacity = 0;

        if (!completed) return;

        try
        {
            AppServices.Current.Settings.OnboardingCompleted = true;
            AppServices.Current.SaveSettings();
            AppServices.Current.LogInfo("首次使用引导：已完成，写入设置（之后不再自动弹出）");
        }
        catch (Exception ex)
        {
            AppServices.Current.LogWarn("保存引导完成标记失败：" + ex.Message);
        }
    }

    private FrameworkElement CurrentOnboardingPanel() => _onboardingStep switch
    {
        2 => OnboardingStep2,
        3 => OnboardingStep3,
        4 => OnboardingStep4,
        _ => OnboardingStep1,
    };

    /// <summary>切到第 N 步（内部 1 起），并更新进度点 / 按钮文案。</summary>
    private void SetOnboardingStep(int step, bool animateStep = true)
    {
        _onboardingStep = Math.Clamp(step, 1, OnboardingSteps);

        OnboardingStep1.Visibility = _onboardingStep == 1 ? Visibility.Visible : Visibility.Collapsed;
        OnboardingStep2.Visibility = _onboardingStep == 2 ? Visibility.Visible : Visibility.Collapsed;
        OnboardingStep3.Visibility = _onboardingStep == 3 ? Visibility.Visible : Visibility.Collapsed;
        OnboardingStep4.Visibility = _onboardingStep == 4 ? Visibility.Visible : Visibility.Collapsed;

        OnboardingStepLabel.Text = $"第 {_onboardingStep} / {OnboardingSteps} 步";
        ObBackButton.IsEnabled = _onboardingStep > 1;

        bool last = _onboardingStep == OnboardingSteps;
        ObNextText.Text = last ? "开始使用" : "下一步";
        ObNextIcon.Glyph = last ? "\uE73E" : "\uE72A";
        AutomationProperties.SetName(ObNextButton, last ? "开始使用" : "下一步");

        if (last) RefreshOnboardingModelHint();

        UpdateOnboardingDots();
        if (animateStep) AnimateStepIn(CurrentOnboardingPanel());
    }

    private void UpdateOnboardingDots()
    {
        if (_onboardingDots.Count != OnboardingSteps)
        {
            OnboardingDots.Children.Clear();
            _onboardingDots.Clear();
            for (int i = 0; i < OnboardingSteps; i++)
            {
                var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
                {
                    Width = 8,
                    Height = 8,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                _onboardingDots.Add(dot);
                OnboardingDots.Children.Add(dot);
            }
        }

        for (int i = 0; i < _onboardingDots.Count; i++)
        {
            bool active = i + 1 == _onboardingStep;
            var brush = (Brush?)Application.Current.Resources["Accent"];
            _onboardingDots[i].Fill = active
                ? (brush ?? new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue))
                : new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x40, 0x80, 0x80, 0x80));
            _onboardingDots[i].Opacity = active ? 1 : 0.7;
        }
    }

    /// <summary>
    /// 让一步里的文字「从下往上升 + 淡入」。
    /// 缓动用指数 EaseOut（非线性：起步快、收尾几乎停住），同一步内每条错峰 70ms。
    /// </summary>
    private void AnimateStepIn(FrameworkElement panel)
    {
        try
        {
            var items = new List<FrameworkElement>();
            CollectAnimatable(panel, items);

            for (int i = 0; i < items.Count; i++)
                StartRise(items[i], TimeSpan.FromMilliseconds(i * 70));

            AppServices.Current.LogInfo($"首次使用引导：第 {_onboardingStep} 步 {items.Count} 条内容已升入（指数缓动 EaseOut）");
        }
        catch (Exception ex)
        {
            AppServices.Current.LogWarn("引导步骤动画失败（改为直接显示）：" + ex.Message);
            foreach (var child in Flatten(panel))
            {
                child.Opacity = 1;
                if (child.RenderTransform is TranslateTransform t) t.Y = 0;
            }
        }
    }

    /// <summary>一步里要参与动画的直接子元素（标题、正文、按钮行…）。</summary>
    private static void CollectAnimatable(FrameworkElement panel, List<FrameworkElement> into)
    {
        if (panel is Panel p)
        {
            foreach (var child in p.Children)
            {
                if (child is FrameworkElement fe && fe.Visibility == Visibility.Visible) into.Add(fe);
            }
            if (into.Count == 0) into.Add(panel);
            return;
        }

        into.Add(panel);
    }

    private static IEnumerable<FrameworkElement> Flatten(FrameworkElement panel)
    {
        if (panel is Panel p)
        {
            foreach (var child in p.Children)
                if (child is FrameworkElement fe) yield return fe;
        }
        else
        {
            yield return panel;
        }
    }

    /// <summary>单个元素：Y 24 → 0（指数缓动 EaseOut）+ 透明度 0 → 1。</summary>
    private static void StartRise(FrameworkElement element, TimeSpan delay)
    {
        const double offset = 24;

        if (element.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform { Y = offset };
            element.RenderTransform = transform;
        }

        transform.Y = offset;
        element.Opacity = 0;

        var sb = new Storyboard();

        var rise = new DoubleAnimation
        {
            From = offset,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(560)),
            BeginTime = delay,
            EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(rise, transform);
        Storyboard.SetTargetProperty(rise, "Y");

        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(360)),
            BeginTime = delay,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");

        sb.Children.Add(rise);
        sb.Children.Add(fade);
        sb.Begin();
    }

    // 引导里的路径选择：选完立即写设置（和设置页一样自动保存）

    private async void ObPickMedia_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFolderForOnboardingAsync();
        if (string.IsNullOrWhiteSpace(path)) return;
        ObMediaBox.Text = path;
        AppServices.Current.Settings.MediaRoot = path;
        AppServices.Current.SaveSettings();
        ObPathHint.Text = $"音频目录已保存：{path}";
        ObNextButton.Focus(FocusState.Programmatic);
    }

    private async void ObPickLocalized_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFolderForOnboardingAsync();
        if (string.IsNullOrWhiteSpace(path)) return;
        ObLocalizedBox.Text = path;
        AppServices.Current.Settings.LocalizedRoot = path;
        AppServices.Current.SaveSettings();
        ObPathHint.Text = $"本地化目录已保存：{path}";
    }

    private static async Task<string?> PickFolderForOnboardingAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
            };
            picker.FileTypeFilter.Add("*");

            var hwnd = App.MainWindowInstance is null
                ? IntPtr.Zero
                : WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance);
            if (hwnd != IntPtr.Zero) WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
        catch (Exception ex)
        {
            AppServices.Current.LogWarn("引导里选择文件夹失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>引导第 3 步：选一个文件夹 → 自动识别里面有没有模型（找 ggml-*.bin，含子目录）。</summary>
    private async void ObPickModelFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFolderForOnboardingAsync();
        if (string.IsNullOrWhiteSpace(path)) return;

        var settings = AppServices.Current.Settings;
        var found = ModelManager.FindModels(path);

        if (found.Count == 0)
        {
            ObModelScanHint.Text = $"自动识别：{path} 里（含子目录）没有找到模型 —— 请确认放的是 ggml-*.bin，或点「去设置页下载模型」。";
            return;
        }

        var chosen = found[0];   // 已经按 ggml- 优先、再按体积从大到小排好
        settings.ModelPath = chosen.Path;
        AppServices.Current.SaveSettings();

        ObModelScanHint.Text = found.Count == 1
            ? $"自动识别：找到并已启用 {chosen.FileName}（{chosen.SizeText}）。"
            : $"自动识别：找到 {found.Count} 个模型，已先启用最大的 {chosen.FileName}（{chosen.SizeText}）；要换别的可以去设置页挑。";
        RefreshOnboardingModelHint();
    }

    private void ObGoDownloadModel_Click(object sender, RoutedEventArgs e)
    {
        CloseOnboarding(completed: false);      // 不算走完，下次启动还会引导一次
        NavigateTo(typeof(SettingsPage), null, "页面 settings");
        SelectNavByTag("settings");
    }

    private void RefreshOnboardingModelHint()
    {
        var settings = AppServices.Current.Settings;
        bool ready = AppServices.Current.IsModelReady;
        ObModelHint.Text = ready
            ? $"当前模型：{Path.GetFileName(settings.ModelPath)}（可用）"
            : string.IsNullOrWhiteSpace(settings.ModelPath)
                ? "当前还没有配置模型：识别不会启动。"
                : $"当前模型：{Path.GetFileName(settings.ModelPath)}（找不到该文件）";
    }

    private void ObBack_Click(object sender, RoutedEventArgs e) => SetOnboardingStep(_onboardingStep - 1);

    private void ObNext_Click(object sender, RoutedEventArgs e)
    {
        if (_onboardingStep < OnboardingSteps)
        {
            // 走到第 2 步时把当前设置填进输入框（用户可能中途改了）
            if (_onboardingStep == 2)
            {
                ObMediaBox.Text = AppServices.Current.Settings.MediaRoot ?? "";
                ObLocalizedBox.Text = AppServices.Current.Settings.LocalizedRoot ?? "";
            }

            SetOnboardingStep(_onboardingStep + 1);
            return;
        }

        CloseOnboarding(completed: true);
        NavigateTo(typeof(DashboardPage), null, "页面 dashboard");
        SelectNavByTag("dashboard");
    }

    private void ObSkip_Click(object sender, RoutedEventArgs e)
    {
        AppServices.Current.LogInfo("首次使用引导：用户点了「跳过引导」");
        CloseOnboarding(completed: true);
    }

    /// <summary>把左侧导航选中项切到指定 Tag（引导里跳页面时用）。</summary>
    private void SelectNavByTag(string tag)
    {
        try
        {
            foreach (var item in Nav.MenuItems)
                if (item is NavigationViewItem nvi && Equals(nvi.Tag, tag)) { Nav.SelectedItem = nvi; return; }
            foreach (var item in Nav.FooterMenuItems)
                if (item is NavigationViewItem nvi && Equals(nvi.Tag, tag)) { Nav.SelectedItem = nvi; return; }
        }
        catch (Exception ex)
        {
            AppServices.Current.LogWarn("切换导航项失败：" + ex.Message);
        }
    }

    private void SelectInitialItem()
    {
        foreach (var item in Nav.MenuItems)
        {
            if (item is NavigationViewItem { Tag: "dashboard" })
            {
                Nav.SelectedItem = item;
                return;
            }
        }
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_firstActivated) return;
        _firstActivated = true;
        Activated -= MainWindow_Activated;

        // 背景渐变要有东西可画，所以放在窗口真正显示之后启动（标志位保证只 Begin 一次）。
        StartBackgroundAnimation();

        // 启动片头（最早的可用时机之一；真正播放与否由设置决定，幂等）
        TryStartSplash();

        // 没播片头（未配置 / 已关闭）→ 首次使用引导直接铺满窗口
        if (SplashOverlay.Visibility != Visibility.Visible) MaybeShowOnboarding();

        // 再排一次队：让第一帧先画出来（窗口可见、导航骨架已就绪），再开始建树。
        DispatcherQueue.TryEnqueue(() => _ = ReloadPartitionTreeAsync(restoreState: false));
    }

    /// <summary>引导还没走过？（第一次打开软件）</summary>
    private bool IsOnboardingPending()
    {
        if (_onboardingChecked) return false;
        try { return !AppServices.Current.Settings.OnboardingCompleted; }
        catch { return false; }
    }

    /// <summary>该显示就显示一次（幂等：同一次运行只判断一次）。</summary>
    private void MaybeShowOnboarding()
    {
        if (_onboardingChecked) return;
        _onboardingChecked = true;

        try
        {
            if (AppServices.Current.Settings.OnboardingCompleted)
            {
                AppServices.Current.LogInfo("首次使用引导：已走过，跳过");
                return;
            }
        }
        catch (Exception ex)
        {
            AppServices.Current.LogWarn("读取引导标记失败，按已走过处理：" + ex.Message);
            return;
        }

        // 等一帧：让主界面先画出来，引导再铺上去（不然启动那一帧会闪）
        DispatcherQueue.TryEnqueue(ShowOnboarding);
    }

    // ── Alpha 定时过期版 · 启动校验闸门 + 运行期时间看门狗 ────────────────────
#if ALPHA_EXPIRY

    private Grid? _alphaOverlay;
    private TextBlock? _alphaHeadline;
    private TextBlock? _alphaMessage;
    private TextBlock? _alphaDetail;
    private ProgressRing? _alphaRing;
    private StackPanel? _alphaButtons;
    private TaskCompletionSource<bool>? _alphaDecision;
    private DispatcherQueueTimer? _alphaWatchdog;
    private int _alphaWatchdogFailures;
    private bool _alphaGatePassed;

    /// <summary>当前是否深色（覆盖层配色跟随界面主题）。</summary>
    private bool IsDarkTheme()
    {
        if (RootGrid.RequestedTheme == ElementTheme.Dark) return true;
        if (RootGrid.RequestedTheme == ElementTheme.Light) return false;
        return Application.Current.RequestedTheme == ApplicationTheme.Dark;
    }

    /// <summary>
    /// 创建铺满窗口的校验覆盖层（位于所有内容之上）。
    /// 说明：覆盖层挂在 RootGrid 上而**不额外创建窗口**——先建后关一个窗口会让主窗口渲染后原生崩溃。
    /// </summary>
    private void EnsureAlphaOverlay()
    {
        if (_alphaOverlay is not null) return;

        bool dark = IsDarkTheme();
        var foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            dark ? Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xF2, 0xF2, 0xF7)
                 : Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x1D, 0x1D, 0x1F));
        var secondary = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            dark ? Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xA9, 0xA9, 0xB2)
                 : Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x6E, 0x6E, 0x73));
        var warn = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xD9, 0x6A, 0x0B));

        _alphaHeadline = new TextBlock
        {
            Text = "正在校验版本有效期…",
            FontSize = 30,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = foreground,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        _alphaRing = new ProgressRing
        {
            IsActive = true,
            Width = 44,
            Height = 44,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = foreground,
        };
        _alphaMessage = new TextBlock
        {
            FontSize = 19,
            LineHeight = 34,
            Foreground = warn,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _alphaDetail = new TextBlock
        {
            Text = "通过 NTP 服务器获取网络时间（不信任本机时钟）",
            FontSize = 13,
            Foreground = secondary,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var retryButton = new Button { Content = "重新校验" };
        retryButton.Click += (_, _) => _alphaDecision?.TrySetResult(true);
        var exitButton = new Button { Content = "退出程序" };
        exitButton.Click += (_, _) => _alphaDecision?.TrySetResult(false);

        _alphaButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _alphaButtons.Children.Add(retryButton);
        _alphaButtons.Children.Add(exitButton);

        var panel = new StackPanel
        {
            Spacing = 18,
            MaxWidth = 760,
            Padding = new Thickness(48),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(_alphaHeadline);
        panel.Children.Add(_alphaRing);
        panel.Children.Add(_alphaMessage);
        panel.Children.Add(_alphaDetail);
        panel.Children.Add(_alphaButtons);

        _alphaOverlay = new Grid
        {
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                dark ? Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x1C, 0x1C, 0x1E) : Microsoft.UI.Colors.White),
        };
        _alphaOverlay.Children.Add(panel);
        Grid.SetRowSpan(_alphaOverlay, 100);
        Grid.SetColumnSpan(_alphaOverlay, 100);
        RootGrid.Children.Add(_alphaOverlay);
    }

    /// <summary>
    /// 启动闸门：通过公网 NTP 服务器校验网络时间（不看本机时钟）。
    /// 通过 → 返回 true 继续加载界面；未通过 → 停留在统一提示上，只允许「重新校验 / 退出程序」。
    /// </summary>
    private async Task<bool> RunAlphaStartupCheckAsync()
    {
        ShowAlphaChecking();
        while (true)
        {
            TimeCheckResult result;
            try
            {
                result = await TimeGuard.CheckAsync();
            }
            catch (Exception ex)
            {
                AppServices.Current.LogWarn("ALPHA 校时异常：" + ex);
                result = new TimeCheckResult
                {
                    Status = TimeCheckStatus.Unreachable,
                    LocalUtc = DateTimeOffset.UtcNow,
                    Detail = "时间校验过程发生异常：" + ex.Message,
                };
            }

            if (result.IsOk)
            {
                AppServices.Current.LogInfo("ALPHA 启动校验通过，继续加载界面");
                _alphaGatePassed = true;
                HideAlphaOverlay();
                return true;
            }

            AppServices.Current.LogWarn($"ALPHA 启动拦截：status={result.Status} detail={result.Detail}");
            ShowAlphaBlocked(result);
            _alphaDecision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool retry = await _alphaDecision.Task;
            _alphaDecision = null;
            if (!retry)
            {
                AppServices.Current.LogWarn("ALPHA 用户选择退出程序");
                Environment.Exit(0);
                return false;
            }
            ShowAlphaChecking();
        }
    }

    /// <summary>校验中：转圈 + 说明文案。</summary>
    private void ShowAlphaChecking()
    {
        if (_alphaOverlay is null) return;
        _alphaOverlay.Visibility = Visibility.Visible;
        _alphaHeadline!.Text = "正在校验版本有效期…";
        _alphaHeadline.FontSize = 30;
        _alphaRing!.Visibility = Visibility.Visible;
        _alphaRing.IsActive = true;
        _alphaMessage!.Visibility = Visibility.Collapsed;
        _alphaDetail!.Text = "通过 NTP 服务器获取网络时间（不信任本机时钟）";
        _alphaButtons!.Visibility = Visibility.Collapsed;
    }

    /// <summary>拦截：显示统一过期文案 + 失败原因 + 两个按钮。</summary>
    private void ShowAlphaBlocked(TimeCheckResult result)
    {
        if (_alphaOverlay is null) return;
        _alphaOverlay.Visibility = Visibility.Visible;
        _alphaHeadline!.Text = "版本校验未通过";
        _alphaHeadline.FontSize = 24;
        _alphaRing!.IsActive = false;
        _alphaRing.Visibility = Visibility.Collapsed;
        _alphaMessage!.Text = TimeGuard.ExpiryMessage;
        _alphaMessage.Visibility = Visibility.Visible;
        _alphaDetail!.Text = result.Detail;
        _alphaButtons!.Visibility = Visibility.Visible;
    }

    private void HideAlphaOverlay()
    {
        if (_alphaOverlay is null) return;
        _alphaOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 运行期复核：每 15 分钟用 NTP 重新取一次网络时间。
    /// 已过期 / 系统时间与 NTP 不一致 → 立即拦截；网络暂时不可达 → 连续两次失败才拦截（容忍单次抖动）。
    /// </summary>
    private void StartAlphaWatchdog()
    {
        _alphaWatchdog = DispatcherQueue.CreateTimer();
        _alphaWatchdog.Interval = TimeSpan.FromMinutes(15);
        _alphaWatchdog.IsRepeating = true;
        _alphaWatchdog.Tick += async (_, _) => await AlphaWatchdogTickAsync();
        _alphaWatchdog.Start();
        AppServices.Current.LogInfo("ALPHA 看门狗已启动（每 15 分钟复核一次网络时间）");
    }

    private async Task AlphaWatchdogTickAsync()
    {
        try
        {
            var result = await TimeGuard.CheckAsync();
            if (result.IsOk)
            {
                _alphaWatchdogFailures = 0;
                return;
            }

            bool hardFail = result.Status is TimeCheckStatus.Expired or TimeCheckStatus.ClockMismatch;
            _alphaWatchdogFailures++;
            AppServices.Current.LogWarn($"ALPHA 看门狗校验失败（第 {_alphaWatchdogFailures} 次）：{result.Status} {result.Detail}");
            if (!hardFail && _alphaWatchdogFailures < 2)
                return;

            _alphaWatchdog?.Stop();
            if (await RunAlphaRuntimeBlockAsync(result))
                return;

            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            AppServices.Current.LogWarn("ALPHA 看门狗异常：" + ex);
        }
    }

    /// <summary>运行期拦截：覆盖层显示统一提示；返回 true 表示重试后校验恢复通过。</summary>
    private async Task<bool> RunAlphaRuntimeBlockAsync(TimeCheckResult result)
    {
        while (true)
        {
            ShowAlphaBlocked(result);
            _alphaDecision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool retry = await _alphaDecision.Task;
            _alphaDecision = null;
            if (!retry) return false;

            ShowAlphaChecking();
            result = await TimeGuard.CheckAsync();
            if (result.IsOk)
            {
                _alphaWatchdogFailures = 0;
                HideAlphaOverlay();
                _alphaWatchdog?.Start();
                AppServices.Current.LogInfo("ALPHA 重新校验通过，继续运行");
                return true;
            }
        }
    }
#endif

    // ── 背景渐变 ────────────────────────────────────────────────────────────

    /// <summary>根 Grid 第一次布局完成：这是最早「动画一定能跑起来」的时机。</summary>
    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
#if ALPHA_EXPIRY
        // Alpha 定时过期版：必须先用公网 NTP 校验网络时间（系统时间与 NTP 不一致 / 已过期 / 无法校时都不放行）
        if (!await RunAlphaStartupCheckAsync())
            return;
        StartAlphaWatchdog();
#endif
        StartBackgroundAnimation();
        TryStartSplash();      // 幂等：与 Activated 里的调用只会生效一次
        UpdateTreeRingVisibility();
        UpdateThemeToggleVisual();
    }

    /// <summary>
    /// 启动主窗口的三色来回渐变（BgStoryboard：AutoReverse + Forever，定义在 MainWindow.xaml 的 RootGrid.Resources）。
    /// Activated 与 RootGrid.Loaded 都会调到这里，用标志位保证只启动一次；重入 / 失败都不影响界面。
    /// </summary>
    private void StartBackgroundAnimation()
    {
        if (_bgAnimationStarted) return;
        _bgAnimationStarted = true;

        try
        {
            BgStoryboard.Begin();
        }
        catch (Exception ex)
        {
            AppServices.Current.LogWarn("启动背景渐变动画失败：" + ex.Message);
        }
    }

    // ── 导航 ────────────────────────────────────────────────────────────────

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppressNavigation) return;
        if (args.SelectedItem is not NavigationViewItem item) return;

        // 分区树叶子的 Tag 就是 BrowserScope（过滤描述）。
        if (item.Tag is BrowserScope scope)
        {
            NavigateTo(typeof(BrowserPage), scope, $"分区 {scope.Title}");
            return;
        }

        // 固定页面：概览 / 识别队列 / 设置。
        if (item.Tag is string tag && _routes.TryGetValue(tag, out var pageType))
            NavigateTo(pageType, null, $"页面 {tag}");

        // Tag 为 "group:xxx" 的分组标题、以及 "partitions" 根节点：只展开 / 收起，不导航。
    }

    private void NavigateTo(Type pageType, object? parameter, string logText)
    {
        if (parameter is null && ContentFrame.CurrentSourcePageType == pageType) return;

        ContentFrame.Navigate(pageType, parameter, new EntranceNavigationTransitionInfo());

        // 没有后退按钮，留着后退栈只会越积越多。
        try { ContentFrame.BackStack.Clear(); } catch { /* 导航尚未落定时忽略 */ }

        AppServices.Current.LogInfo("打开" + logText);
    }

    // ── 分区树（按索引库真实数据生成） ──────────────────────────────────────

    private async void RefreshTree_Click(object sender, RoutedEventArgs e)
        => await ReloadPartitionTreeAsync(restoreState: true);

    /// <summary>
    /// 重新生成分区导航树：后台线程查库（每列一次 GROUP BY），回到 UI 线程填 MenuItems。
    /// 刷新时保留各分组的展开状态与当前选中的分区。
    /// </summary>
    private async Task ReloadPartitionTreeAsync(bool restoreState)
    {
        if (_buildingTree) return;
        _buildingTree = true;

        var (expandedKeys, selectedKey) = restoreState ? CaptureTreeState() : (new HashSet<string>(), "");
        SetTreeBusy(true);

        var services = AppServices.Current;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var nodes = await Task.Run(() => PartitionTreeBuilder.Build(services.Db));
            long ms = stopwatch.ElapsedMilliseconds;

            DispatcherQueue.TryEnqueue(() =>
            {
                ApplyTree(nodes, expandedKeys, selectedKey, ms);
                _buildingTree = false;
            });
        }
        catch (Exception ex)
        {
            services.LogWarn("生成分区导航失败：" + ex.Message);
            DispatcherQueue.TryEnqueue(() =>
            {
                TreeStatusText.Text = "分区导航生成失败：" + ex.Message;
                SetTreeBusy(false);
                _buildingTree = false;
            });
        }
    }

    private (HashSet<string> Expanded, string Selected) CaptureTreeState()
    {
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var obj in PartitionRoot.MenuItems)
        {
            if (obj is NavigationViewItem { IsExpanded: true } group && group.Tag is string key)
                expanded.Add(key);
        }

        string selected = Nav.SelectedItem is NavigationViewItem { Tag: BrowserScope scope } ? scope.Key : "";
        return (expanded, selected);
    }

    private void ApplyTree(List<PartitionNode> nodes, HashSet<string> expandedKeys, string selectedKey, long elapsedMs)
    {
        _suppressNavigation = true;
        try
        {
            PartitionRoot.MenuItems.Clear();

            int leafCount = 0;
            NavigationViewItem? toSelect = null;

            foreach (var node in nodes)
            {
                var groupItem = new NavigationViewItem
                {
                    Content = node.Title,
                    Tag = "group:" + node.Key,
                    // 分组标题只负责展开 / 收起，选中样式留给叶子，避免点一下就没高亮。
                    SelectsOnInvoked = false
                };

                NavigationViewItem? groupOfSelection = null;
                foreach (var leaf in node.Children)
                {
                    if (leaf.Scope is null) continue;

                    var leafItem = new NavigationViewItem { Content = leaf.Title, Tag = leaf.Scope };
                    groupItem.MenuItems.Add(leafItem);
                    leafCount++;

                    if (!string.IsNullOrEmpty(selectedKey) && string.Equals(leaf.Scope.Key, selectedKey, StringComparison.Ordinal))
                    {
                        toSelect = leafItem;
                        groupOfSelection = groupItem;
                    }
                }

                PartitionRoot.MenuItems.Add(groupItem);

                // 展开状态：刷新前展开过的保持展开；首次建树只有「按类型」默认展开。
                groupItem.IsExpanded = expandedKeys.Count > 0
                    ? expandedKeys.Contains(node.Key)
                    : node.IsExpandedByDefault;

                // 被选中的叶子所在分组必须展开，否则选中项看不见。
                if (groupOfSelection is not null) groupItem.IsExpanded = true;
            }

            if (nodes.Count == 0)
            {
                PartitionRoot.MenuItems.Add(new NavigationViewItem
                {
                    Content = "索引为空：请到「概览」点「全量扫描索引」",
                    IsEnabled = false
                });
                TreeStatusText.Text = "分区导航：索引库还没有数据，扫描后点「⟳ 刷新分区」即可生成。";
            }
            else
            {
                int groupCount = PartitionRoot.MenuItems.Count;
                TreeStatusText.Text =
                    $"分区导航：{groupCount} 组 / {leafCount} 个分区 · 建树用时 {elapsedMs} ms（数量随识别进度变化，随时可刷新）";
            }

            // 还原刷新前选中的分区（不重复导航，避免刷新树把页面重置一遍）。
            if (toSelect is not null) Nav.SelectedItem = toSelect;
        }
        finally
        {
            _suppressNavigation = false;
            SetTreeBusy(false);
        }
    }

    /// <summary>
    /// 建树忙闲。TreeRing 的可见性由「忙」与「侧栏是否展开」共同决定：
    /// 折叠成 48px 图标栏时必须 Collapsed，否则 12px 的圈会把导航项挤爆（而且紧凑态模板本来也不显示内容）。
    /// </summary>
    private void SetTreeBusy(bool busy)
    {
        _treeBusy = busy;
        UpdateTreeRingVisibility();
        RefreshTreeButton.IsEnabled = !busy;
        if (busy) TreeStatusText.Text = "正在按索引库重建分区树…";
    }

    private void UpdateTreeRingVisibility()
    {
        try
        {
            bool show = _treeBusy && Nav.IsPaneOpen;
            TreeRing.IsActive = show;
            TreeRing.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            AppServices.Current.LogWarn("同步分区树进度圈失败：" + ex.Message);
        }
    }
}
