using DFAudioStudio.Core.Models;

namespace DFAudioStudio.App.ViewModels;

/// <summary>
/// 一个「分区」的过滤描述：导航树的叶子把它塞进 NavigationViewItem.Tag，
/// 点击后整对象作为导航参数交给 BrowserPage → AudioBrowserControl.SetScope。
/// 所有字段留空 = 该条件不参与查询（等价于下拉框里的「全部」）。
/// </summary>
public sealed class BrowserScope
{
    /// <summary>页面大标题，例如「分区：二周年 · 362 条」。</summary>
    public string Title { get; set; } = "音频浏览";

    /// <summary>页面说明（写清预设条件，方便用户确认口径）。</summary>
    public string Subtitle { get; set; } = "";

    /// <summary>预设分区；null = 不限分区。</summary>
    public AudioCategory? Category { get; set; }

    /// <summary>第二个可切换分区（用于「音效 / 环境」这类一个页面两个分区的情况）。</summary>
    public AudioCategory? AltCategory { get; set; }

    /// <summary>预设干员标签，例如「302」。</summary>
    public string Operator { get; set; } = "";

    /// <summary>预设活动标签，例如「二周年」。</summary>
    public string EventTag { get; set; } = "";

    /// <summary>预设地图标签，例如「AZ3核电园区」。</summary>
    public string MapTag { get; set; } = "";

    /// <summary>预设皮肤 / 联动标签，例如「Yanzu」。</summary>
    public string SkinTag { get; set; } = "";

    /// <summary>预设子分类，例如「Voice_SOL」。</summary>
    public string SubCategory { get; set; } = "";

    /// <summary>预设场景 / 用途，例如「选人入场」「对局开局」「剧情过场」（由分类器按内容判定）。</summary>
    public string SceneTag { get; set; } = "";

    /// <summary>预设根目录（Media / Localized），留空表示两个都查。</summary>
    public string Root { get; set; } = "";

    /// <summary>建树时从索引库里读到的真实条数（只用于导航标题 / 页面标题显示）。</summary>
    public int Count { get; set; }

    /// <summary>是否需要显示「分区」下拉（单分区页面隐藏，多分区 / 不限分区页面显示）。</summary>
    public bool NeedsCategorySelector => AltCategory is not null || Category is null;

    /// <summary>
    /// 过滤条件的稳定键：刷新导航树时用它把「当前选中项」还原回去（同一个分区重建前后键相同）。
    /// </summary>
    public string Key => string.Join("|",
        Category?.ToString() ?? "",
        AltCategory?.ToString() ?? "",
        Operator ?? "",
        EventTag ?? "",
        MapTag ?? "",
        SkinTag ?? "",
        SubCategory ?? "",
        SceneTag ?? "",
        Root ?? "");
}
