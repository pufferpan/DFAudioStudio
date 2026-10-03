using System.ComponentModel;
using DFAudioStudio.App.Services;
using DFAudioStudio.App.ViewModels;
using DFAudioStudio.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace DFAudioStudio.App.Pages;

/// <summary>识别队列页：状态 / 当前文本 / 暂停继续停止 / 失败重试。</summary>
public sealed partial class QueuePage : Page
{
    public QueueViewModel ViewModel { get; }

    public QueuePage()
    {
        ViewModel = new QueueViewModel(AppServices.Current);
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Loaded += QueuePage_Loaded;
    }

    private async void QueuePage_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshAsync();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(QueueViewModel.CurrentText)) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            try { TextScroller.ChangeView(null, TextScroller.ScrollableHeight, null); }
            catch { /* 布局尚未就绪时忽略 */ }
        });
    }

    private void Start_Click(object sender, RoutedEventArgs e) => ViewModel.Start();

    private void Pause_Click(object sender, RoutedEventArgs e) => ViewModel.Pause();

    private void Resume_Click(object sender, RoutedEventArgs e) => ViewModel.Resume();

    private void Stop_Click(object sender, RoutedEventArgs e) => ViewModel.Stop();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ViewModel.RefreshAsync();

    private async void RetryAll_Click(object sender, RoutedEventArgs e) => await ViewModel.RetryAllFailedAsync();

    private async void RetryOne_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            AppServices.Current.LogWarn("重试失败项的按钮不是 FrameworkElement，无法定位数据项。");
            return;
        }

        // ListViewItem 会把数据项放进 DataContext（Tag 兜底：XAML 里显式绑了 Tag="{x:Bind}"）
        if (element.DataContext is AudioItem item) { await ViewModel.RetryOneAsync(item); return; }
        if (element.Tag is AudioItem tagged) { await ViewModel.RetryOneAsync(tagged); return; }

        ViewModel.Report("没能取到这一条的数据，请点「刷新」后重试。");
        AppServices.Current.LogWarn("重试失败项：按钮上没有 AudioItem（DataContext / Tag 都是空的）。");
    }
}
