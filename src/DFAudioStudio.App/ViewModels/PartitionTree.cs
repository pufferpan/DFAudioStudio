using System;
using System.Collections.Generic;
using DFAudioStudio.App.Converters;
using DFAudioStudio.Core.Data;
using DFAudioStudio.Core.Models;

namespace DFAudioStudio.App.ViewModels;

/// <summary>
/// 导航树里的一个节点：分组（<see cref="Scope"/> 为 null，<see cref="Children"/> 是它下面的叶子）
/// 或叶子（<see cref="Scope"/> 就是点它要用的过滤描述）。
/// </summary>
public sealed class PartitionNode
{
    private PartitionNode(string title, string key, BrowserScope? scope, bool expandedByDefault)
    {
        Title = title;
        Key = key;
        Scope = scope;
        IsExpandedByDefault = expandedByDefault;
    }

    /// <summary>导航里显示的文字，带真实数量，例如「语音 (53,468)」「干员 302 (1,799)」。</summary>
    public string Title { get; }

    /// <summary>稳定键（分组＝列名，叶子＝BrowserScope.Key），刷新树时用来还原展开与选中状态。</summary>
    public string Key { get; }

    /// <summary>叶子节点的过滤描述；分组节点为 null。</summary>
    public BrowserScope? Scope { get; }

    /// <summary>第一次建树时是否默认展开（只有「按类型」是展开的，避免一上来铺开上百项）。</summary>
    public bool IsExpandedByDefault { get; }

    public List<PartitionNode> Children { get; } = new();

    public bool IsGroup => Scope is null;

    public static PartitionNode Group(string title, string key, bool expandedByDefault = false)
        => new(title, key, null, expandedByDefault);

    public static PartitionNode Leaf(string title, string scopeKey, BrowserScope scope)
        => new(title, scopeKey, scope, false);
}

/// <summary>
/// 分区树构建器：**全部数据来自索引库的真实统计**（不再有写死的「语音 / 音乐 / 二周年 / AZ3」固定页面）。
/// 注意：<see cref="Build"/> 会连续查库（每列一次 GROUP BY），必须在后台线程调用。
/// </summary>
public static class PartitionTreeBuilder
{
    private const int OperatorTop = 30;
    private const int EventTop = 30;
    private const int MapTop = 30;
    private const int SkinTop = 20;
    private const int SubCategoryTop = 30;
    private const int SceneTop = 32;

    private const string CountHint = "数量是建树那一刻库里的真实条数，会随索引 / 识别进度变化，可点顶部「⟳ 刷新分区」重新统计。";

    /// <summary>
    /// 按索引库现有数据生成整棵树（分组顺序＝显示顺序）：按类型 / 按干员 / 按活动 / 按地图 / 按皮肤·联动 / 按子分类。
    /// 库里一条数据都没有时返回空列表（界面会提示先去「概览」扫描索引）。
    /// </summary>
    public static List<PartitionNode> Build(IndexDb db)
    {
        if (db is null) throw new ArgumentNullException(nameof(db));

        var nodes = new List<PartitionNode>();

        AddByCategory(db, nodes);
        // 「按场景/用途」：分类器按内容判定（选人入场 / 对局开局 / 大厅 / 剧情过场 / 电台 / 结算 …）
        AddByValue(db, nodes, "按场景/用途", "SceneTag", SceneTop, "", "场景 / 用途", (s, v) => s.SceneTag = v);
        AddByValue(db, nodes, "按干员", "Operator", OperatorTop, "干员 ", "干员标签", (s, v) => s.Operator = v);
        AddByValue(db, nodes, "按活动", "EventTag", EventTop, "", "活动标签", (s, v) => s.EventTag = v);
        AddByValue(db, nodes, "按地图", "MapTag", MapTop, "", "地图标签", (s, v) => s.MapTag = v);
        AddByValue(db, nodes, "按皮肤/联动", "SkinTag", SkinTop, "", "皮肤 / 联动标签", (s, v) => s.SkinTag = v);
        AddByValue(db, nodes, "按子分类", "SubCategory", SubCategoryTop, "", "子分类", (s, v) => s.SubCategory = v);

        return nodes;
    }

    /// <summary>「按类型」：分区枚举固定，数量来自 CountsByCategory()。</summary>
    private static void AddByCategory(IndexDb db, List<PartitionNode> nodes)
    {
        var counts = db.CountsByCategory();
        var group = PartitionNode.Group("按类型", "Category", expandedByDefault: true);

        foreach (AudioCategory category in Enum.GetValues<AudioCategory>())
        {
            if (!counts.TryGetValue(category, out int count) || count <= 0) continue;

            string name = CategoryText.Of(category);
            var scope = new BrowserScope
            {
                Title = $"分区：{name} · {count:N0} 条",
                Subtitle = $"预设：分区 = {name}（{category}）· {CountHint}",
                Category = category,
                Count = count
            };
            group.Children.Add(PartitionNode.Leaf($"{name} ({count:N0})", scope.Key, scope));
        }

        if (group.Children.Count > 0) nodes.Add(group);
    }

    /// <summary>「按某一列的真实取值」建组：数量来自 DistinctWithCounts()，按数量倒序。</summary>
    private static void AddByValue(IndexDb db, List<PartitionNode> nodes, string groupTitle, string column,
                                   int top, string labelPrefix, string fieldName, Action<BrowserScope, string> apply)
    {
        var group = PartitionNode.Group(groupTitle, column);

        foreach (var (value, count) in db.DistinctWithCounts(column, top))
        {
            if (string.IsNullOrWhiteSpace(value)) continue;

            var scope = new BrowserScope { Count = count };
            apply(scope, value);
            scope.Title = $"分区：{labelPrefix}{value} · {count:N0} 条";
            scope.Subtitle = $"预设：{fieldName} = {value} · {CountHint}";

            group.Children.Add(PartitionNode.Leaf($"{labelPrefix}{value} ({count:N0})", scope.Key, scope));
        }

        if (group.Children.Count > 0) nodes.Add(group);
    }
}
