using DFAudioStudio.App.Services;
using DFAudioStudio.App.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace DFAudioStudio.App.Pages;

/// <summary>
/// 统一浏览页：不再有写死的「语音 / 音乐 / 二周年 / AZ3」固定页面，
/// 导航树里每个叶子把自己的 <see cref="BrowserScope"/> 通过导航参数送进来，页面标题、筛选条件都由它决定。
/// </summary>
public sealed partial class BrowserPage : Page
{
    public BrowserPage()
    {
        InitializeComponent();
        // 复用同一个页面实例：换分区时只重设过滤描述并重查，不必重建控件与 MediaPlayer。
        NavigationCacheMode = NavigationCacheMode.Required;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // 没带参数（理论上不会发生）时给一个「不限分区」的空口径，保证页面可用。
        var scope = e.Parameter as BrowserScope ?? new BrowserScope
        {
            Title = "分区浏览 · 全部分区",
            Subtitle = "未指定分区，可在下方「分区」下拉里收窄。"
        };

        Browser.SetScope(scope);
        AppServices.Current.LogInfo($"浏览分区：{scope.Title}");
    }
}
