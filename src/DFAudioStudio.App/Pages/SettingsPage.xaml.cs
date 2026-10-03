using System;
using System.Threading.Tasks;
using DFAudioStudio.App.Services;
using DFAudioStudio.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace DFAudioStudio.App.Pages;

/// <summary>设置页：目录 / 模型 / 参数 / 导出 / 保存。文件与文件夹选择器都做了 InitializeWithWindow 绑定。</summary>
public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    private bool _loadedOnce;

    public SettingsPage()
    {
        ViewModel = new SettingsViewModel(AppServices.Current);
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;

        Loaded += SettingsPage_Loaded;
        // 离开页面时把还在防抖里的改动立刻落盘（不然刚敲完路径就切走会丢）
        Unloaded += (_, _) => ViewModel.FlushAutoSave();
    }

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        // 只在第一次进入时读配置：之后每次改动都是即改即存，重复读反而会把界面上的值闪一下。
        if (_loadedOnce) return;
        _loadedOnce = true;
        ViewModel.LoadFromSettings();
    }

    // ── 目录 / 文件选择（非打包应用必须先 InitializeWithWindow） ──────────────

    private static IntPtr GetHwnd()
    {
        var window = App.MainWindowInstance;
        if (window is null) return IntPtr.Zero;
        try { return WinRT.Interop.WindowNative.GetWindowHandle(window); }
        catch { return IntPtr.Zero; }
    }

    private static async Task<string?> PickFolderAsync()
    {
        var picker = new Windows.Storage.Pickers.FolderPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder
        };
        picker.FileTypeFilter.Add("*");

        var hwnd = GetHwnd();
        if (hwnd != IntPtr.Zero) WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private async void PickMediaRoot_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync();
        if (string.IsNullOrWhiteSpace(path)) return;
        ViewModel.MediaRoot = path;
        ViewModel.FlushAutoSave();
    }

    private async void PickLocalizedRoot_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync();
        if (string.IsNullOrWhiteSpace(path)) return;
        ViewModel.LocalizedRoot = path;
        ViewModel.FlushAutoSave();
    }

    private async void PickExportDir_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync();
        if (string.IsNullOrWhiteSpace(path)) return;
        ViewModel.ExportDir = path;
        ViewModel.FlushAutoSave();
    }

    private async void PickModel_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads
        };
        picker.FileTypeFilter.Add(".bin");
        picker.FileTypeFilter.Add("*");

        var hwnd = GetHwnd();
        if (hwnd != IntPtr.Zero) WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        ViewModel.ModelPath = file.Path;
        ViewModel.FlushAutoSave();
    }

    /// <summary>
    /// 「识别文件夹」：选一个文件夹 → 自动扫里面（含两层子目录）有没有 ggml-*.bin。
    /// 找到一个就自动填入并保存；找到多个列出来让你挑；一个都没有就明确说明。
    /// </summary>
    private async void PickModelFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync();
        if (string.IsNullOrWhiteSpace(path)) return;

        ViewModel.CommitModelPath(path);
        ViewModel.FlushAutoSave();
    }

    /// <summary>模型路径被手敲/粘贴：只扫描并报结论（打字过程中不替换输入框内容）。</summary>
    private void ModelPath_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loadedOnce) return;
        if (!IsUserEdit(sender as TextBox, ViewModel.ModelPath, out var text)) return;
        ViewModel.ReportModelsForTypedPath(text);
    }

    // ── 文本框：只在「用户真的在改」时才写回 ViewModel ───────────────────────
    //
    // 为什么这么绕：自动保存之后，任何一次误判都会被立刻写进设置文件。
    // 实测踩到的坑：WinUI 的 x:Bind 一次性回填可能**晚于 Loaded**（页面下方还没滚到的卡片就是），
    // 会把 ViewModel 的旧值（空字符串）写回控件 → 触发 TextChanged → 被当成用户清空 → 片头视频路径被冲成空。
    // 因此只有「控件有焦点」的文本变化才算用户输入；没焦点又和 ViewModel 不一致，就把控件纠正回 ViewModel 的值。

    /// <summary>判断这次文本变化是不是用户在改；不是的话把控件恢复成 ViewModel 的值（防止把回填当输入）。</summary>
    private static bool IsUserEdit(TextBox? box, string currentValue, out string text)
    {
        text = box?.Text ?? "";
        if (box is null) return false;
        if (string.Equals(text, currentValue, StringComparison.Ordinal)) return false;   // 没变化 / 绑定回填同样的值

        if (box.FocusState == FocusState.Unfocused)
        {
            box.Text = currentValue;    // 不是用户在改：纠正显示，绝不写进设置
            return false;
        }

        return true;
    }

    private void MediaRoot_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsUserEdit(sender as TextBox, ViewModel.MediaRoot, out var text)) ViewModel.MediaRoot = text;
    }

    private void LocalizedRoot_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsUserEdit(sender as TextBox, ViewModel.LocalizedRoot, out var text)) ViewModel.LocalizedRoot = text;
    }

    private void ExportDir_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsUserEdit(sender as TextBox, ViewModel.ExportDir, out var text)) ViewModel.ExportDir = text;
    }

    private void SplashVideoPath_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsUserEdit(sender as TextBox, ViewModel.SplashVideoPath, out var text)) ViewModel.SplashVideoPath = text;
    }

    /// <summary>离开输入框：这次允许自动填入识别到的模型，并立刻落盘。</summary>
    private void ModelPath_LostFocus(object sender, RoutedEventArgs e)
    {
        // 失焦这一刻以控件里的文字为准（用户可能刚粘贴完就点走了）
        if (sender is TextBox box && !string.Equals(box.Text, ViewModel.ModelPath, StringComparison.Ordinal))
            ViewModel.ModelPath = box.Text;

        ViewModel.CommitModelPath();
        ViewModel.FlushAutoSave();
    }

    /// <summary>选择启动片头视频（默认 mp4；只填入路径，真正播放由主窗口负责）。</summary>
    private async void PickSplashVideo_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.VideosLibrary
        };
        picker.FileTypeFilter.Add(".mp4");
        picker.FileTypeFilter.Add(".mov");
        picker.FileTypeFilter.Add(".wmv");
        picker.FileTypeFilter.Add("*");

        var hwnd = GetHwnd();
        if (hwnd != IntPtr.Zero) WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        ViewModel.SplashVideoPath = file.Path;
        ViewModel.FlushAutoSave();
    }

    // ── 其它按钮 ────────────────────────────────────────────────────────────

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.DownloadModelAsync();
    }

    private void CancelDownload_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CancelDownload();
    }
}
