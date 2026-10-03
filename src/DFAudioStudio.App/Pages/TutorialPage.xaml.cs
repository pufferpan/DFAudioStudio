using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;
using DFAudioStudio.App.Services;

namespace DFAudioStudio.App.Pages;

/// <summary>
/// 新手教程：每一步一张卡片，卡片从下方升起 + 淡入，滚到哪一步就升哪一步。
///
/// 动画是非线性的（<see cref="ExponentialEase"/> EaseOut，指数 4）：起步快、收尾慢，
/// 到位的瞬间几乎没有速度，看起来像"被轻轻放上去"，而不是匀速平移。
/// 同一次进入视口的多张卡片之间再加几十毫秒错峰，观感更有层次。
/// </summary>
public sealed partial class TutorialPage : Page
{
    /// <summary>卡片初始下沉的距离（像素）。升到 0 就是原位。</summary>
    private const double StartOffsetY = 56;

    /// <summary>单张卡片的上升时长。</summary>
    private static readonly TimeSpan RiseDuration = TimeSpan.FromMilliseconds(720);

    /// <summary>同一批卡片之间的错峰间隔。</summary>
    private static readonly TimeSpan StaggerStep = TimeSpan.FromMilliseconds(90);

    /// <summary>视口下方多少像素内的卡片算"进入视野"（留一点提前量，滚起来更连贯）。</summary>
    private const double RevealMargin = 60;

    private readonly List<FrameworkElement> _cards = new();
    private readonly HashSet<FrameworkElement> _revealed = new();
    private bool _ready;

    /// <summary>
    /// 动画探针：设置环境变量 <c>DFA_TUTORIAL_PROBE=1</c> 后，卡片上升过程中每 60ms 往日志里记一行
    /// 「t=…ms Y=…」，用来核对缓动到底是不是非线性的（前 25% 时间应该已经走完一大半位移）。
    /// 平时不开，零开销。
    /// </summary>
    private static readonly bool ProbeEnabled =
        Environment.GetEnvironmentVariable("DFA_TUTORIAL_PROBE") == "1";

    public TutorialPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;

        Loaded += TutorialPage_Loaded;
    }

    private void TutorialPage_Loaded(object sender, RoutedEventArgs e)
    {
        PrepareCards();
        RevealVisibleCards(force: true);
    }

    /// <summary>给每张卡片挂上"在下方 + 透明"的初始状态（幂等）。</summary>
    private void PrepareCards()
    {
        _cards.Clear();
        foreach (var child in StepsPanel.Children)
        {
            if (child is not FrameworkElement card) continue;
            _cards.Add(card);
            ResetCard(card);
        }

        // 标题也跟着升一下，但不参与"滚动逐张出现"的队列
        ResetCard(HeaderPanel);
        _revealed.Remove(HeaderPanel);
        _ready = true;
    }

    private static void ResetCard(FrameworkElement card)
    {
        card.Opacity = 0;
        card.RenderTransform = new TranslateTransform { Y = StartOffsetY };
    }

    /// <summary>把当前（以及滚动后新进入视口的）卡片升上来。</summary>
    private void RevealVisibleCards(bool force = false)
    {
        if (!_ready) return;

        try
        {
            // 先让标题升上来（每次重播都会再放一次）
            if (force || !_revealed.Contains(HeaderPanel))
            {
                _revealed.Add(HeaderPanel);
                RevealCard(HeaderPanel, TimeSpan.Zero, "标题");
            }

            double viewportBottom = Scroller.ViewportHeight + Scroller.VerticalOffset + RevealMargin;
            var batch = 0;

            foreach (var card in _cards)
            {
                if (_revealed.Contains(card)) continue;
                if (CardTop(card) > viewportBottom) continue;     // 还在视口下方，等着

                _revealed.Add(card);
                RevealCard(card, TimeSpan.FromMilliseconds(batch * StaggerStep.TotalMilliseconds), $"第{batch + 1}张");
                batch++;
            }

            if (batch > 0) AppServices.Current.LogInfo($"教程页：{batch} 张卡片已升入视口（指数缓动 EaseOut）");
        }
        catch (Exception ex)
        {
            // 动画出问题不能让整页白屏：直接把所有卡片显示出来
            AppServices.Current.LogWarn("教程页动画失败，改为直接显示：" + ex.Message);
            foreach (var card in _cards)
            {
                card.Opacity = 1;
                if (card.RenderTransform is TranslateTransform t) t.Y = 0;
            }
        }
    }

    /// <summary>卡片顶部相对滚动内容的位置。</summary>
    private double CardTop(FrameworkElement card)
    {
        try
        {
            var point = card.TransformToVisual(Scroller).TransformPoint(new Point(0, 0));
            return point.Y + Scroller.VerticalOffset;
        }
        catch
        {
            return 0;   // 还没布局好，当成已经在视口里
        }
    }

    /// <summary>
    /// 单张卡片：位移 56 → 0（指数缓动 EaseOut，非线性）+ 透明度 0 → 1（三次缓动）。
    /// </summary>
    private void RevealCard(FrameworkElement card, TimeSpan delay, string tag)
    {
        if (card.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform { Y = StartOffsetY };
            card.RenderTransform = transform;
        }
        transform.Y = StartOffsetY;
        card.Opacity = 0;

        var storyboard = new Storyboard();

        var rise = new DoubleAnimation
        {
            From = StartOffsetY,
            To = 0,
            Duration = new Duration(RiseDuration),
            BeginTime = delay,
            EasingFunction = new ExponentialEase { Exponent = 4, EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(rise, transform);
        Storyboard.SetTargetProperty(rise, "Y");

        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(420)),
            BeginTime = delay,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, card);
        Storyboard.SetTargetProperty(fade, "Opacity");

        storyboard.Children.Add(rise);
        storyboard.Children.Add(fade);
        storyboard.Begin();

        if (ProbeEnabled) StartProbe(tag, transform, delay);
    }

    /// <summary>自检用：采样动画中的 Y，核对缓动曲线（默认关闭，靠环境变量打开）。</summary>
    private void StartProbe(string tag, TranslateTransform transform, TimeSpan delay)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(60);
        timer.IsRepeating = true;
        timer.Tick += (_, _) =>
        {
            double elapsedMs = clock.Elapsed.TotalMilliseconds - delay.TotalMilliseconds;
            AppServices.Current.LogInfo($"教程动画探针 {tag} t={elapsedMs:F0}ms Y={transform.Y:F1}");
            if (elapsedMs > RiseDuration.TotalMilliseconds + 240) timer.Stop();
        };
        timer.Start();
    }

    private void Scroller_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e) => RevealVisibleCards();

    /// <summary>重播：全部复位，再从当前视口重新升一遍。</summary>
    private void Replay_Click(object sender, RoutedEventArgs e)
    {
        _revealed.Clear();
        PrepareCards();
        try { Scroller.ChangeView(null, 0, null, disableAnimation: true); } catch { /* 忽略 */ }
        RevealVisibleCards(force: true);
        AppServices.Current.LogInfo("教程页：动画已重播");
    }

    private void Top_Click(object sender, RoutedEventArgs e)
    {
        try { Scroller.ChangeView(null, 0, null, disableAnimation: false); } catch { /* 忽略 */ }
    }

    /// <summary>重新铺开「首次使用引导」（铺满整个窗口的那套），方便回头再看一遍。</summary>
    private void ReplayOnboarding_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (App.MainWindowInstance is MainWindow main) main.ShowOnboarding();
            else AppServices.Current.LogWarn("拿不到主窗口，无法重看首次引导");
        }
        catch (Exception ex)
        {
            AppServices.Current.LogWarn("重看首次引导失败：" + ex.Message);
        }
    }
}
