using System.ComponentModel;
using DFAudioStudio.App.Services;
using DFAudioStudio.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace DFAudioStudio.App.Pages;

/// <summary>概览页：统计卡片、全量扫描、开始/暂停识别、运行日志。</summary>
public sealed partial class DashboardPage : Page
{
    public DashboardViewModel ViewModel { get; }

    public DashboardPage()
    {
        ViewModel = new DashboardViewModel(AppServices.Current);
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;

        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Loaded += DashboardPage_Loaded;
    }

    private async void DashboardPage_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshAsync();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DashboardViewModel.LogText)) return;

        // 日志追加后滚到底部（这里已经处于 UI 线程，再排一次队保证布局已完成）。
        DispatcherQueue.TryEnqueue(() =>
        {
            try { LogScroller.ChangeView(null, LogScroller.ScrollableHeight, null); }
            catch { /* 布局尚未就绪时忽略 */ }
        });
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ViewModel.ScanAsync();

    private void CancelScan_Click(object sender, RoutedEventArgs e) => ViewModel.CancelScan();

    private void Queue_Click(object sender, RoutedEventArgs e) => ViewModel.ToggleQueue();

    private void StopQueue_Click(object sender, RoutedEventArgs e) => ViewModel.StopQueue();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ViewModel.RefreshAsync();

    private async void ExportTxt_Click(object sender, RoutedEventArgs e) => await ViewModel.ExportTxtAsync();

    /// <summary>导出识别结果为可回灌的 JSON 交换文件。</summary>
    private async void ExportTranscripts_Click(object sender, RoutedEventArgs e) => await ViewModel.ExportTranscriptsJsonAsync();

    /// <summary>
    /// 导入识别结果：选文件 → 预演（会命中多少、会改多少）→ 弹框确认 → 写库。
    /// 预演和写库都在后台线程，界面不卡；文件选择器要先 InitializeWithWindow（非打包应用）。
    /// </summary>
    private async void ImportTranscripts_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeFilter.Add(".json");

            var window = App.MainWindowInstance;
            if (window is not null)
            {
                try { WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window)); }
                catch (Exception ex) { AppServices.Current.LogWarn("初始化文件选择器失败：" + ex.Message); }
            }

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            string? readError = null;
            var prepared = await ViewModel.PrepareImportAsync(file.Path, err => readError = err);
            if (prepared is null)
            {
                await ShowInfoAsync("导入识别结果", readError ?? "读取失败。");
                return;
            }

            var (plan, parsed) = (prepared.Value.Plan, prepared.Value.File);
            string content = "文件：" + file.Path + "\n\n" + plan.Describe();
            string samples = plan.DescribeSamples();
            if (samples.Length > 0) content += "\n\n" + samples;

            if (plan.Matched == 0)
            {
                await ShowInfoAsync("导入识别结果", content + "\n\n没有一条能匹配上当前索引库，先扫描一次索引再试。");
                return;
            }

            if (plan.WillChange == 0)
            {
                await ShowInfoAsync("导入识别结果", content + "\n\n所有能匹配上的条目内容都已经一样，不需要导入。");
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "确认导入识别结果",
                Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 },
                PrimaryButtonText = "导入",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await ViewModel.ImportTranscriptsAsync(parsed);
        }
        catch (Exception ex)
        {
            AppServices.Current.LogWarn("导入识别结果失败：" + ex.Message);
            await ShowInfoAsync("导入识别结果", "导入失败：" + ex.Message);
        }
    }

    private async Task ShowInfoAsync(string title, string content)
    {
        try
        {
            await new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = title,
                Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 },
                CloseButtonText = "知道了",
                DefaultButton = ContentDialogButton.Close,
            }.ShowAsync();
        }
        catch (Exception ex)
        {
            AppServices.Current.LogWarn("弹出提示失败：" + ex.Message);
        }
    }
}
