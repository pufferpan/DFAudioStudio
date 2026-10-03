using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace DFAudioStudio.App.Services;

/// <summary>主题模式（与 <see cref="DFAudioStudio.Core.Services.AppSettings.Theme"/> 的取值一一对应）。</summary>
public enum AppThemeMode
{
    /// <summary>跟随系统（ElementTheme.Default）。</summary>
    System = 0,
    Light = 1,
    Dark = 2
}

/// <summary>
/// 应用级主题：单一事实来源 + 广播。
///
/// 设计：
/// · 当前模式存在 <see cref="DFAudioStudio.Core.Services.AppSettings.Theme"/>（"System" / "Light" / "Dark"）；
/// · 本类只负责「读设置 / 存设置 / 广播变化 / 解析资源」，
///   真正的 <c>RootGrid.RequestedTheme</c> 赋值与淡入淡出动画在主窗口 MainWindow.xaml.cs（因为动画需要那个窗口的元素）；
/// · 主窗口的 ToggleSwitch 与设置页的三选下拉都通过 <see cref="ModeChanged"/> 互相同步（两个入口改的是同一个值）。
/// </summary>
public static class ThemeService
{
    private static bool _initialized;

    /// <summary>当前主题模式。</summary>
    public static AppThemeMode Current { get; private set; } = AppThemeMode.System;

    /// <summary>主题模式变化（已经写入设置）。主窗口据此播放切换动画并改 RequestedTheme。</summary>
    public static event Action<AppThemeMode>? ModeChanged;

    /// <summary>从设置里读一次（幂等）。App 启动时（主窗口构造前）调用。</summary>
    public static AppThemeMode Initialize()
    {
        if (_initialized) return Current;
        _initialized = true;

        try { Current = Parse(AppServices.Current.Settings.Theme); }
        catch (Exception ex) { AppServices.Current.LogWarn("读取主题设置失败，按「跟随系统」处理：" + ex.Message); }

        return Current;
    }

    /// <summary>字符串 → 主题模式（无法识别一律按「跟随系统」）。</summary>
    public static AppThemeMode Parse(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "light" => AppThemeMode.Light,
        "dark" => AppThemeMode.Dark,
        _ => AppThemeMode.System
    };

    /// <summary>主题模式 → 设置文件里的字符串。</summary>
    public static string ToSettingValue(AppThemeMode mode) => mode switch
    {
        AppThemeMode.Light => "Light",
        AppThemeMode.Dark => "Dark",
        _ => "System"
    };

    /// <summary>界面上的中文名。</summary>
    public static string ToDisplayName(AppThemeMode mode) => mode switch
    {
        AppThemeMode.Light => "浅色",
        AppThemeMode.Dark => "深色",
        _ => "跟随系统"
    };

    /// <summary>主题模式 → 根元素 RequestedTheme。</summary>
    public static ElementTheme ToElementTheme(AppThemeMode mode) => mode switch
    {
        AppThemeMode.Light => ElementTheme.Light,
        AppThemeMode.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default
    };

    /// <summary>
    /// 切换主题（可选写设置）并广播。
    /// 即使模式没变也会广播一次：新出现的界面元素（例如刚打开的设置页）可以借此同步到当前值。
    /// </summary>
    public static void SetMode(AppThemeMode mode, bool persist = true)
    {
        Initialize();
        Current = mode;

        if (persist)
        {
            try
            {
                var services = AppServices.Current;
                string value = ToSettingValue(mode);
                if (!string.Equals(services.Settings.Theme, value, StringComparison.Ordinal))
                {
                    services.Settings.Theme = value;
                    services.SaveSettings();
                }
            }
            catch (Exception ex)
            {
                AppServices.Current.LogWarn("保存主题设置失败：" + ex.Message);
            }
        }

        try { ModeChanged?.Invoke(mode); }
        catch (Exception ex) { AppServices.Current.LogWarn("应用主题失败：" + ex.Message); }
    }

    /// <summary>在两种「具体」主题之间切换（ToggleSwitch / 紧凑态按钮用）：当前是深色就切浅色，反之亦然。</summary>
    public static AppThemeMode OppositeOfCurrentVisual(ElementTheme currentVisual)
        => currentVisual == ElementTheme.Dark ? AppThemeMode.Light : AppThemeMode.Dark;

    /// <summary>
    /// 目标 ElementTheme（可能是 Default）→ 一眼能看出「到底会变成浅色还是深色」。
    /// Default（跟随系统）时向系统要一次「应用模式」：系统背景偏黑就是深色。
    /// </summary>
    public static ElementTheme ResolveVisualTheme(ElementTheme target)
    {
        if (target != ElementTheme.Default) return target;

        try
        {
            var ui = new Windows.UI.ViewManagement.UISettings();
            var bg = ui.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
            int luma = (5 * bg.R + 9 * bg.G + 2 * bg.B) / 16;   // 感知亮度（BT.601 近似）
            return luma < 128 ? ElementTheme.Dark : ElementTheme.Light;
        }
        catch { /* 取不到系统颜色就不猜 */ }

        try
        {
            return Application.Current.RequestedTheme == ApplicationTheme.Dark ? ElementTheme.Dark : ElementTheme.Light;
        }
        catch { return ElementTheme.Light; }
    }

    /// <summary>
    /// 取「某个具体主题」下的页面底色画刷（切换动画的覆盖层用它，避免切换瞬间闪白/闪黑）。
    /// 令牌在 Styles/AppleTheme.xaml 的 ThemeDictionaries 里，而那个字典是**合并**进 Application.Resources 的，
    /// 所以这里要连合并字典一起找。
    /// </summary>
    public static Brush PageBackgroundBrush(ElementTheme visualTheme)
    {
        string themeKey = visualTheme == ElementTheme.Dark ? "Dark" : "Light";

        try
        {
            var resources = Application.Current?.Resources;
            if (resources is not null)
            {
                var found = FindIn(resources, themeKey);
                if (found is not null) return found;

                foreach (var merged in resources.MergedDictionaries)
                {
                    found = FindIn(merged, themeKey);
                    if (found is not null) return found;
                }
            }
        }
        catch { /* 找不到就用下面的兜底色 */ }

        return new SolidColorBrush(visualTheme == ElementTheme.Dark
            ? Windows.UI.Color.FromArgb(255, 0, 0, 0)
            : Windows.UI.Color.FromArgb(255, 255, 255, 255));

        Brush? FindIn(ResourceDictionary dictionary, string key)
        {
            if (!dictionary.ThemeDictionaries.TryGetValue(key, out object? themeDictionary)) return null;
            if (themeDictionary is not ResourceDictionary typed) return null;
            if (!typed.TryGetValue("PageBackground", out object? brush)) return null;
            return brush as Brush;
        }
    }
}
