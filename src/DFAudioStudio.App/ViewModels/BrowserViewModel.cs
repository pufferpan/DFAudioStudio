using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using DFAudioStudio.App.Services;
using DFAudioStudio.Core.Data;
using DFAudioStudio.Core.Models;
using DFAudioStudio.Core.Services;
using Microsoft.UI.Dispatching;

namespace DFAudioStudio.App.ViewModels;

/// <summary>下拉框里的一项（分区 / 排序 / 状态 / 语言 / 模型都可以复用）。</summary>
public sealed class ChoiceOption
{
    public ChoiceOption(string label, string value = "", AudioCategory? category = null, object? tag = null)
    {
        Label = label;
        Value = value;
        Category = category;
        Tag = tag;
    }

    /// <summary>界面显示的文字。</summary>
    public string Label { get; }

    /// <summary>给代码用的值（如 OrderBy / 语言代码 / 状态枚举名）。</summary>
    public string Value { get; }

    /// <summary>分区筛选时可选的分区；null 表示「不限」。</summary>
    public AudioCategory? Category { get; }

    /// <summary>附加对象（例如 ModelManager.ModelInfo）。</summary>
    public object? Tag { get; }

    public override string ToString() => Label;
}

/// <summary>
/// 列表浏览的公共 ViewModel：分区页、搜索页都用它，查询走 IndexDb.Query / IndexDb.Count。
/// 过滤描述由 <see cref="BrowserScope"/>（独立文件）承载，避免与导航树参数类型混淆。
/// </summary>
public sealed class BrowserViewModel : ObservableObject
{
    private const string AllText = "全部";
    private const int PageLimit = 1000;
    private const int ExportLimit = 20000;

    /// <summary>界面侧过滤时每次从库里取多少条候选（配合下面的预筛，通常一两次就够）。</summary>
    private const int ScanChunkSize = 1000;

    /// <summary>界面侧过滤最多扫多少条，兜底防止超大库卡死（正常库一次预筛就结束）。</summary>
    private const int MaxScanRows = 200_000;

    /// <summary>下拉里最多列多少个「子分类 / 皮肤」取值（按数量倒序，数据来自 DistinctWithCounts）。</summary>
    private const int FilterOptionTop = 400;

    private readonly AppServices _services;
    private readonly DispatcherQueue? _ui;
    private BrowserScope _scope = new();
    private bool _filtersLoaded;

    public BrowserViewModel(AppServices services)
    {
        _services = services;
        _ui = UiDispatch.Capture();

        SortOptions.Add(new ChoiceOption("文件名 A→Z", "Path"));
        SortOptions.Add(new ChoiceOption("时长 长→短", "Duration"));
        SortOptions.Add(new ChoiceOption("时长 短→长", "DurationAsc"));
        SortOptions.Add(new ChoiceOption("文件大小 大→小", "Size"));
        SortOptions.Add(new ChoiceOption("识别状态", "Status"));
        _selectedSort = SortOptions[0];

        StatusOptions.Add(new ChoiceOption("全部状态", ""));
        StatusOptions.Add(new ChoiceOption("未识别", nameof(TranscriptStatus.None)));
        StatusOptions.Add(new ChoiceOption("排队中", nameof(TranscriptStatus.Queued)));
        StatusOptions.Add(new ChoiceOption("识别中", nameof(TranscriptStatus.Running)));
        StatusOptions.Add(new ChoiceOption("已识别", nameof(TranscriptStatus.Done)));
        StatusOptions.Add(new ChoiceOption("失败", nameof(TranscriptStatus.Failed)));
        StatusOptions.Add(new ChoiceOption("已跳过", nameof(TranscriptStatus.Skipped)));
        _selectedStatus = StatusOptions[0];

        Operators.Add(AllText);
        Events.Add(AllText);
        Maps.Add(AllText);
        SubCategories.Add(new ChoiceOption(AllText, ""));
        Skins.Add(new ChoiceOption(AllText, ""));
        _selectedSubCategory = SubCategories[0];
        _selectedSkin = Skins[0];

        RebuildCategoryOptions();    }

    // ── 集合 ────────────────────────────────────────────────────────────────

    /// <summary>当前筛选结果（最多 PageLimit 条）。</summary>
    public ObservableCollection<AudioItem> Items { get; } = new();

    public ObservableCollection<ChoiceOption> CategoryOptions { get; } = new();
    public ObservableCollection<ChoiceOption> SortOptions { get; } = new();
    public ObservableCollection<ChoiceOption> StatusOptions { get; } = new();
    public ObservableCollection<string> Operators { get; } = new();
    public ObservableCollection<string> Events { get; } = new();
    public ObservableCollection<string> Maps { get; } = new();

    /// <summary>子分类下拉（首项「全部」；其余来自 DistinctWithCounts("SubCategory")）。</summary>
    public ObservableCollection<ChoiceOption> SubCategories { get; } = new();

    /// <summary>皮肤 / 联动下拉（首项「全部」；其余来自 DistinctWithCounts("SkinTag")）。</summary>
    public ObservableCollection<ChoiceOption> Skins { get; } = new();

    // ── 绑定属性 ────────────────────────────────────────────────────────────

    private string _title = "音频浏览";
    public string Title { get => _title; private set => SetProperty(ref _title, value); }

    private string _subtitle = "";
    public string Subtitle { get => _subtitle; private set => SetProperty(ref _subtitle, value); }

    private AudioItem? _selectedItem;
    public AudioItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetProperty(ref _selectedItem, value)) return;
            OnPropertiesChanged(nameof(SelectedTitle), nameof(SelectedTranscript),
                                nameof(SelectedMeta), nameof(SelectedPlayerMeta),
                                nameof(HasSelection), nameof(HasSelectedTranscript));
        }
    }

    public string SelectedTitle => _selectedItem is null ? "未选择音频" : _selectedItem.FileName;

    public string SelectedTranscript => _selectedItem?.Transcript ?? "";

    public bool HasSelection => _selectedItem is not null;

    public bool HasSelectedTranscript => !string.IsNullOrWhiteSpace(_selectedItem?.Transcript);

    /// <summary>
    /// 底部播放条第二行：只留最有用的四个字段「分区 · 场景 · 时长 · 大小」。
    /// 比 <see cref="SelectedMeta"/> 短得多，播放条一行放得下；没有选中时给一句操作提示。
    /// </summary>
    public string SelectedPlayerMeta => _selectedItem is null
        ? "在上方列表里点一条音频即可播放，双击直接播放。"
        : $"{CategoryTextOf(_selectedItem.Category)} · {Value(_selectedItem.SceneTag)} · {_selectedItem.DurationText} · {_selectedItem.SizeText}";

    public string SelectedMeta => _selectedItem is null
        ? "在上方列表里点一条音频即可播放，双击直接播放。"
        : $"{CategoryTextOf(_selectedItem.Category)} · 子分类 {Value(_selectedItem.SubCategory)} · 场景 {Value(_selectedItem.SceneTag)} · 干员 {Value(_selectedItem.Operator)} · 事件 {Value(_selectedItem.EventTag)} · 地图 {Value(_selectedItem.MapTag)} · 皮肤 {Value(_selectedItem.SkinTag)} · 时长 {_selectedItem.DurationText} · {_selectedItem.SizeText}";

    private string _searchText = "";
    /// <summary>搜索关键词（匹配文件名 / 识别文本 / 子分类）。</summary>
    public string SearchText { get => _searchText; set => SetProperty(ref _searchText, value); }

    // 下面几个筛选属性都忽略 null 写入：
    // 预设值（分区树点进来的标签）在「下拉列表还没填好」时不在 ItemsSource 里，
    // ComboBox 会把 SelectedItem 置空并可能把 null 回写，那样预设条件就被抹掉了。
    // 「不加该条件」统一用「全部」表达（字符串列表里是 "全部"，ChoiceOption 列表里是 Value 为空的项）。

    private string _selectedOperator = AllText;
    public string SelectedOperator { get => _selectedOperator; set { if (value is null) return; SetProperty(ref _selectedOperator, value); } }

    private string _selectedEvent = AllText;
    public string SelectedEvent { get => _selectedEvent; set { if (value is null) return; SetProperty(ref _selectedEvent, value); } }

    private string _selectedMap = AllText;
    public string SelectedMap { get => _selectedMap; set { if (value is null) return; SetProperty(ref _selectedMap, value); } }

    private ChoiceOption? _selectedSubCategory = new(AllText, "");
    /// <summary>子分类筛选；Value 为空 = 「全部」（不加该条件）。</summary>
    public ChoiceOption? SelectedSubCategory
    {
        get => _selectedSubCategory;
        set { if (value is null) return; SetProperty(ref _selectedSubCategory, value); }
    }

    private ChoiceOption? _selectedSkin = new(AllText, "");
    /// <summary>皮肤 / 联动筛选；Value 为空 = 「全部」（不加该条件）。</summary>
    public ChoiceOption? SelectedSkin
    {
        get => _selectedSkin;
        set { if (value is null) return; SetProperty(ref _selectedSkin, value); }
    }

    private ChoiceOption? _selectedCategory;
    public ChoiceOption? SelectedCategory { get => _selectedCategory; set => SetProperty(ref _selectedCategory, value); }

    private ChoiceOption? _selectedSort;
    public ChoiceOption? SelectedSort { get => _selectedSort; set => SetProperty(ref _selectedSort, value); }

    private ChoiceOption? _selectedStatus;
    public ChoiceOption? SelectedStatus { get => _selectedStatus; set => SetProperty(ref _selectedStatus, value); }

    private bool _showCategorySelector;
    /// <summary>是否显示「分区」下拉。</summary>
    public bool ShowCategorySelector { get => _showCategorySelector; private set => SetProperty(ref _showCategorySelector, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }

    private bool _isEmpty;
    public bool IsEmpty { get => _isEmpty; private set => SetProperty(ref _isEmpty, value); }

    private int _totalCount;
    public int TotalCount { get => _totalCount; private set => SetProperty(ref _totalCount, value); }

    public string TotalCountText => TotalCount.ToString("N0");

    private string _statusText = "就绪";
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    /// <summary>由控件写入一行状态提示（例如播放 / 播放失败）。</summary>
    public void Report(string message) => StatusText = message;

    /// <summary>播放请求：控件里的 MediaPlayer 负责真正播放（VM 不碰播放器）。</summary>
    public event Action<AudioItem>? PlayRequested;

    // ── 预设 ────────────────────────────────────────────────────────────────

    /// <summary>应用页面预设（导航进入时由页面调用）。</summary>
    public void ApplyScope(BrowserScope scope)
    {
        _scope = scope ?? new BrowserScope();
        _filtersLoaded = false;   // 预设变了，下拉里的预设项要重新合并
        Title = _scope.Title;
        Subtitle = string.IsNullOrWhiteSpace(_scope.Subtitle) ? BuildScopeSummary() : _scope.Subtitle;
        ShowCategorySelector = _scope.NeedsCategorySelector;
        RebuildCategoryOptions();

        // 预设的标签直接作为筛选项的默认值；预设里没有的条件要显式回到「全部」，
        // 否则从上一个分区切过来时旧的筛选条件会残留（页面实例是复用的）。
        SelectedOperator = string.IsNullOrWhiteSpace(_scope.Operator) ? AllText : _scope.Operator;
        SelectedEvent = string.IsNullOrWhiteSpace(_scope.EventTag) ? AllText : _scope.EventTag;
        SelectedMap = string.IsNullOrWhiteSpace(_scope.MapTag) ? AllText : _scope.MapTag;
        SelectedSubCategory = PresetOption(_scope.SubCategory);
        SelectedSkin = PresetOption(_scope.SkinTag);
    }

    /// <summary>预设值 → 下拉项（空值＝「全部」项，Value 为空）。</summary>
    private static ChoiceOption PresetOption(string? value)
        => string.IsNullOrWhiteSpace(value) ? new ChoiceOption(AllText, "") : new ChoiceOption(value, value);

    private string BuildScopeSummary()
    {
        var parts = new List<string>();
        if (_scope.Category is { } c) parts.Add("分区 " + CategoryTextOf(c));
        if (_scope.AltCategory is { } a) parts.Add("可切换 " + CategoryTextOf(a));
        if (!string.IsNullOrWhiteSpace(_scope.Operator)) parts.Add("干员 " + _scope.Operator);
        if (!string.IsNullOrWhiteSpace(_scope.EventTag)) parts.Add("活动标签 " + _scope.EventTag);
        if (!string.IsNullOrWhiteSpace(_scope.MapTag)) parts.Add("地图标签 " + _scope.MapTag);
        if (!string.IsNullOrWhiteSpace(_scope.SkinTag)) parts.Add("皮肤 / 联动 " + _scope.SkinTag);
        if (!string.IsNullOrWhiteSpace(_scope.SubCategory)) parts.Add("子分类 " + _scope.SubCategory);
        if (!string.IsNullOrWhiteSpace(_scope.Root)) parts.Add("根目录 " + _scope.Root);
        parts.Add($"最多显示 {PageLimit} 条");
        return string.Join(" · ", parts);
    }

    private void RebuildCategoryOptions()
    {
        CategoryOptions.Clear();

        if (_scope.Category is { } main && _scope.AltCategory is null)
        {
            CategoryOptions.Add(new ChoiceOption(CategoryTextOf(main), main.ToString(), main));
            _selectedCategory = CategoryOptions[0];
            OnPropertyChanged(nameof(SelectedCategory));
            return;
        }

        if (_scope.Category is { } first && _scope.AltCategory is { } second)
        {
            CategoryOptions.Add(new ChoiceOption(CategoryTextOf(first), first.ToString(), first));
            CategoryOptions.Add(new ChoiceOption(CategoryTextOf(second), second.ToString(), second));
        }

        CategoryOptions.Add(new ChoiceOption("全部分区", "", null));
        foreach (AudioCategory c in Enum.GetValues<AudioCategory>())
        {
            if (CategoryOptions.Count(o => o.Category == c) > 0) continue;
            CategoryOptions.Add(new ChoiceOption(CategoryTextOf(c), c.ToString(), c));
        }

        _selectedCategory = CategoryOptions[0];
        OnPropertyChanged(nameof(SelectedCategory));
    }

    // ── 查询 ────────────────────────────────────────────────────────────────

    private AudioCategory? EffectiveCategory
    {
        get
        {
            if (!ShowCategorySelector) return _scope.Category ?? SelectedCategory?.Category;
            return SelectedCategory?.Category;
        }
    }

    private static string? NullIfAll(string? text)
        => string.IsNullOrWhiteSpace(text) || text == AllText ? null : text;

    private static TranscriptStatus? ParseStatus(ChoiceOption? option)
    {
        if (option is null || string.IsNullOrWhiteSpace(option.Value)) return null;
        return Enum.TryParse<TranscriptStatus>(option.Value, out var s) ? s : null;
    }

    private ItemQuery BuildQuery(int limit, bool onlyWithTranscript = false) => new()
    {
        Category = EffectiveCategory,
        Root = string.IsNullOrWhiteSpace(_scope.Root) ? null : _scope.Root,
        Operator = NullIfAll(SelectedOperator),
        // 活动 / 地图以「下拉框当前值」为准，预设只是把下拉框预置好（用户可以再切换）。
        EventTag = NullIfAll(SelectedEvent),
        MapTag = NullIfAll(SelectedMap),
        SubCategory = NullIfAll(SelectedSubCategory?.Value),
        SkinTag = NullIfAll(SelectedSkin?.Value),
        // 场景/用途由分区（导航树叶子的 BrowserScope）直接决定
        SceneTag = string.IsNullOrWhiteSpace(_scope.SceneTag) ? null : _scope.SceneTag,
        Status = ParseStatus(SelectedStatus),
        Search = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
        OnlyWithTranscript = onlyWithTranscript,
        OrderBy = string.IsNullOrWhiteSpace(SelectedSort?.Value) ? "Path" : SelectedSort!.Value,
        Limit = limit,
        Offset = 0
    };

    /// <summary>一次查询的结果：条目、真实总数、是否因安全上限没扫完。</summary>
    private sealed record QueryOutcome(List<AudioItem> Items, int Total, bool PartialScan);

    /// <summary>
    /// 执行当前筛选条件（全部条件都下推到 SQL：分区 / 干员 / 活动 / 地图 / 子分类 / 皮肤 / 场景 / 状态 / 关键词）。
    /// 注意：本方法会查库，必须由调用方放到后台线程执行。
    /// </summary>
    private QueryOutcome RunQuery(int limit, bool onlyWithTranscript = false)
    {
        var query = BuildQuery(limit, onlyWithTranscript);
        return new QueryOutcome(_services.Db.Query(query), _services.Db.Count(query), false);
    }

    /// <summary>兼容旧调用点（现已全部走 SQL，保留方法名以免大改）。</summary>
    private QueryOutcome RunQueryLegacy(int limit, bool onlyWithTranscript = false)
    {
        string? sub = SelectedSubCategory?.Value;
        string? skin = SelectedSkin?.Value;
        bool hasSub = !string.IsNullOrWhiteSpace(sub);
        bool hasSkin = !string.IsNullOrWhiteSpace(skin);

        if (!hasSub && !hasSkin)
        {
            var query = BuildQuery(limit, onlyWithTranscript);
            return new QueryOutcome(_services.Db.Query(query), _services.Db.Count(query), false);
        }

        // 一次只筛一个「界面侧条件」且用户没在搜索框里打字时，才能借用 Search 做预筛。
        string? prefilter = !string.IsNullOrWhiteSpace(SearchText) || (hasSub && hasSkin)
            ? null
            : (hasSub ? sub : skin);

        var matched = new List<AudioItem>();
        int scanned = 0;
        int offset = 0;
        bool partial = false;

        while (true)
        {
            var chunkQuery = BuildQuery(ScanChunkSize, onlyWithTranscript);
            chunkQuery.Offset = offset;
            chunkQuery.Search = prefilter ?? chunkQuery.Search;

            var chunk = _services.Db.Query(chunkQuery);
            if (chunk.Count == 0) break;

            foreach (var item in chunk)
            {
                if (hasSub && !string.Equals(item.SubCategory, sub, StringComparison.Ordinal)) continue;
                if (hasSkin && !string.Equals(item.SkinTag, skin, StringComparison.Ordinal)) continue;
                scanned++;
                if (matched.Count < limit) matched.Add(item);
            }

            offset += chunk.Count;
            if (chunk.Count < ScanChunkSize) break;       // 库已经扫到底
            if (offset >= MaxScanRows) { partial = true; break; }
        }

        return new QueryOutcome(matched, scanned, partial);
    }

    /// <summary>
    /// 按当前筛选条件刷新列表（线程安全：内部 Task.Run + 回 UI 线程）。
    ///
    /// 选中项的恢复（关键）：刷新会重建 <see cref="Items"/>，而 AudioItem 是每次查询新建的对象，
    /// 所以「旧对象」在新集合里根本不存在 —— 必须按 <c>Id</c> 重新定位。
    /// · <paramref name="preserveSelectionId"/> 给了就恢复那一条（「重新识别」走这条路径）；
    /// · 没给就记住**调用这一刻**的选中项 Id，刷新后若它还在结果集里就重新选中，不在就清空
    ///   （清空会让 HasSelection 变 false，于是「重新识别」按钮变灰 —— 这正是我们想要的语义）。
    /// </summary>
    public async Task LoadAsync(long? preserveSelectionId = null)
    {
        if (IsBusy) return;

        // 在真正开始查库之前先记下来：await 期间用户可能已经改了选中项，以调用一刻为准。
        long? wantedSelectionId = preserveSelectionId ?? SelectedItem?.Id;

        IsBusy = true;
        StatusText = "正在读取索引…";
        try
        {
            await EnsureFilterOptionsAsync().ConfigureAwait(true);

            var outcome = await Task.Run(() => RunQuery(PageLimit)).ConfigureAwait(true);

            UiDispatch.Run(_ui, () =>
            {
                Items.Clear();
                foreach (var item in outcome.Items) Items.Add(item);
                TotalCount = outcome.Total;
                IsEmpty = Items.Count == 0;

                AudioItem? keep = null;
                if (wantedSelectionId is { } wanted)
                {
                    foreach (var candidate in Items)
                    {
                        if (candidate.Id != wanted) continue;
                        keep = candidate;
                        break;
                    }
                }

                // 找不回同一条（例如筛选条件已经把它排除）→ keep 为 null → 选择被清空、HasSelection=false
                SelectedItem = keep;
                StatusText = BuildStatusText(outcome);
            });
        }
        catch (Exception ex)
        {
            UiDispatch.Run(_ui, () => StatusText = "读取失败：" + ex.Message);
            _services.LogWarn("查询索引失败：" + ex.Message);
        }
        finally
        {
            UiDispatch.Run(_ui, () => IsBusy = false);
        }
    }

    private string BuildStatusText(QueryOutcome outcome)
    {
        if (outcome.Total == 0)
            return "没有匹配的音频。若还没建立索引，请到「概览」点「全量扫描索引」。";

        string localNote = UsesLocalFilter ? "（子分类 / 皮肤筛选在界面侧精确过滤）" : "";
        if (outcome.PartialScan)
            return $"已在库里扫描前 {MaxScanRows:N0} 条，命中 {outcome.Total:N0} 条{localNote}；缩小筛选条件可以得到完整结果。";
        if (outcome.Items.Count < outcome.Total)
            return $"显示前 {outcome.Items.Count} / 共 {outcome.Total:N0} 条{localNote}（缩小筛选条件可看到其余条目）";
        return $"共 {outcome.Total:N0} 条{localNote} · 导出目录：{_services.Settings.ExportDir}";
    }

    /// <summary>当前是否用到了「子分类 / 皮肤」这两个只能界面侧过滤的条件。</summary>
    private bool UsesLocalFilter
        => !string.IsNullOrWhiteSpace(SelectedSubCategory?.Value)
           || !string.IsNullOrWhiteSpace(SelectedSkin?.Value);

    /// <summary>重新从库里取一遍干员 / 活动 / 地图 / 子分类 / 皮肤下拉（扫描或识别后调用）。</summary>
    public async Task RefreshFilterOptionsAsync()
    {
        _filtersLoaded = false;
        await EnsureFilterOptionsAsync().ConfigureAwait(true);
    }

    private async Task EnsureFilterOptionsAsync()
    {
        if (_filtersLoaded) return;

        var operators = await Task.Run(() => SafeDistinct("Operator")).ConfigureAwait(true);
        var events = await Task.Run(() => SafeDistinct("EventTag")).ConfigureAwait(true);
        var maps = await Task.Run(() => SafeDistinct("MapTag")).ConfigureAwait(true);
        var subCategories = await Task.Run(() => SafeDistinctWithCounts("SubCategory")).ConfigureAwait(true);
        var skins = await Task.Run(() => SafeDistinctWithCounts("SkinTag")).ConfigureAwait(true);

        UiDispatch.Run(_ui, () =>
        {
            // 清空 ObservableCollection 会让 ComboBox 把选择置空并回写 ViewModel，
            // 所以先把当前选择记下来，填完列表再写回去。
            string operatorSel = NullIfAll(SelectedOperator) ?? AllText;
            string eventSel = NullIfAll(SelectedEvent) ?? AllText;
            string mapSel = NullIfAll(SelectedMap) ?? AllText;
            string subSel = SelectedSubCategory?.Value ?? "";
            string skinSel = SelectedSkin?.Value ?? "";

            Merge(Operators, operators);
            Merge(Events, events);
            Merge(Maps, maps);
            MergeOptions(SubCategories, subCategories);
            MergeOptions(Skins, skins);

            // 预设的标签 / 值即使在库里暂时没有，也要出现在下拉里，方便用户看清口径。
            EnsurePreset(Operators, _scope.Operator);
            EnsurePreset(Events, _scope.EventTag);
            EnsurePreset(Maps, _scope.MapTag);
            EnsurePresetOption(SubCategories, _scope.SubCategory);
            EnsurePresetOption(Skins, _scope.SkinTag);

            SelectedOperator = operatorSel;
            SelectedEvent = eventSel;
            SelectedMap = mapSel;
            SelectedSubCategory = FindOption(SubCategories, subSel);
            SelectedSkin = FindOption(Skins, skinSel);

            _filtersLoaded = true;
        });
    }

    private List<string> SafeDistinct(string column)
    {
        try { return _services.Db.Distinct(column); }
        catch (Exception ex)
        {
            _services.LogWarn($"读取 {column} 标签失败：{ex.Message}");
            return new List<string>();
        }
    }

    /// <summary>下拉用的「值 + 数量」列表（来自 DistinctWithCounts，按数量倒序）。</summary>
    private List<ChoiceOption> SafeDistinctWithCounts(string column)
    {
        try
        {
            var list = new List<ChoiceOption>();
            foreach (var (value, count) in _services.Db.DistinctWithCounts(column, FilterOptionTop))
                list.Add(new ChoiceOption($"{value} ({count:N0})", value));
            return list;
        }
        catch (Exception ex)
        {
            _services.LogWarn($"读取 {column} 取值失败：{ex.Message}");
            return new List<ChoiceOption>();
        }
    }

    private static void Merge(ObservableCollection<string> target, List<string> values)
    {
        target.Clear();
        target.Add(AllText);
        foreach (var v in values) target.Add(v);
    }

    /// <summary>把「值 + 数量」重建进下拉，首项固定是「全部」（值为空 = 不加该条件）。</summary>
    private static void MergeOptions(ObservableCollection<ChoiceOption> target, List<ChoiceOption> values)
    {
        target.Clear();
        target.Add(new ChoiceOption(AllText, ""));
        foreach (var v in values) target.Add(v);
    }

    private static void EnsurePreset(ObservableCollection<string> target, string preset)
    {
        if (string.IsNullOrWhiteSpace(preset)) return;
        if (target.Contains(preset)) return;
        target.Insert(Math.Min(1, target.Count), preset);
    }

    /// <summary>预设值不在下拉里时补一项（带数量），保证点击分区后筛选条件不会丢。</summary>
    private static void EnsurePresetOption(ObservableCollection<ChoiceOption> target, string preset)
    {
        if (string.IsNullOrWhiteSpace(preset)) return;
        if (target.Any(o => string.Equals(o.Value, preset, StringComparison.Ordinal))) return;
        target.Insert(Math.Min(1, target.Count), new ChoiceOption(preset, preset));
    }

    /// <summary>按值找下拉项；找不到（或值为空）就回落到第一项「全部」。</summary>
    private static ChoiceOption? FindOption(ObservableCollection<ChoiceOption> target, string? value)
    {
        if (target.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(value)) return target[0];
        foreach (var option in target)
            if (string.Equals(option.Value, value, StringComparison.Ordinal)) return option;
        return target[0];
    }

    // ── 导出 ────────────────────────────────────────────────────────────────

    /// <summary>把当前筛选结果导出成 CSV 清单。</summary>
    public async Task ExportCsvAsync()
    {
        try
        {
            StatusText = "正在导出 CSV…";
            string dir = _services.EnsureExportDir();
            var items = (await Task.Run(() => RunQuery(ExportLimit)).ConfigureAwait(true)).Items;
            string fileName = SafeFileName(Title) + "·音频索引.csv";
            string path = await Task.Run(() => ExportService.ExportCsv(items, dir, fileName)).ConfigureAwait(true);

            UiDispatch.Run(_ui, () => StatusText = $"已导出 {items.Count} 条 → {path}");
            _services.LogInfo($"导出 CSV（{items.Count} 条）：{path}");
        }
        catch (Exception ex)
        {
            UiDispatch.Run(_ui, () => StatusText = "导出 CSV 失败：" + ex.Message);
            _services.LogWarn("导出 CSV 失败：" + ex.Message);
        }
    }

    /// <summary>导出字幕：选中一条则导出该条；没选中则把当前筛选里有识别文本的全部导出。</summary>
    public async Task ExportSrtAsync()
    {
        try
        {
            StatusText = "正在导出 SRT…";
            string dir = _services.EnsureExportDir("SRT");

            if (SelectedItem is { } one)
            {
                var item = one;
                string path = await Task.Run(() => ExportService.ExportSrt(item, dir)).ConfigureAwait(true);
                UiDispatch.Run(_ui, () => StatusText = "已导出字幕 → " + path);
                _services.LogInfo("导出 SRT：" + path);
                return;
            }

            var items = (await Task.Run(() => RunQuery(500, onlyWithTranscript: true)).ConfigureAwait(true)).Items;

            int done = 0;
            foreach (var item in items)
            {
                var current = item;
                await Task.Run(() => ExportService.ExportSrt(current, dir)).ConfigureAwait(true);
                done++;
            }

            string message = done == 0
                ? "没有可导出的识别文本（先选一条音频，或先跑识别）。"
                : $"已导出 {done} 个字幕 → {dir}";
            UiDispatch.Run(_ui, () => StatusText = message);
            _services.LogInfo(message);
        }
        catch (Exception ex)
        {
            UiDispatch.Run(_ui, () => StatusText = "导出 SRT 失败：" + ex.Message);
            _services.LogWarn("导出 SRT 失败：" + ex.Message);
        }
    }

    // ── 操作 ────────────────────────────────────────────────────────────────

    /// <summary>请求播放当前选中项（由 AudioBrowserControl 里的 MediaPlayer 执行）。</summary>
    public void PlaySelected()
    {
        if (SelectedItem is { } item) PlayRequested?.Invoke(item);
        else StatusText = "请先在列表里选择一条音频。";
    }

    /// <summary>
    /// 把选中项重新加入识别队列（任何时候都能用：不要求队列正在 Running）：
    /// 状态重置为 0（未处理）+ 清空旧的识别文本 → 队列没在跑就启动（模型没配置时给出明确提示）→ 刷新列表并保持选中。
    /// </summary>
    public async Task ReTranscribeSelectedAsync()
    {
        if (SelectedItem is not { } item)
        {
            StatusText = "先选中一条音频";
            return;
        }

        long id = item.Id;
        string name = item.FileName;

        // 重置状态（0＝未处理）+ 清空旧识别文本 + 队列没在跑就启动（模型没配好会有明确日志/文案）
        _services.RequestReTranscribe(item);

        // 刷新列表时按 Id 恢复选中那一条：否则按钮会因为失去选中项立刻变灰。
        await LoadAsync(id);

        // 反馈写在 LoadAsync 之后 —— 列表刷新会把 StatusText 覆盖成查询统计。
        // 状态栏统一报出「这一次重跑 + 队列当前状态」，用户点完立刻能确认到底有没有入队。
        StatusText = _services.IsModelReady
            ? $"已把「{name}」重新加入识别队列（当前队列：{_services.QueueStateText}）。"
            : $"已把「{name}」置为待识别（当前队列：{_services.QueueStateText}）；尚未配置 Whisper 模型，配置后点「开始识别」即可。";
    }

    /// <summary>
    /// 兼容保留的同步入口（老调用点）。真正的重跑 + 刷新走 <see cref="ReTranscribeSelectedAsync"/>。
    /// </summary>
    public void ReTranscribeSelected() => _ = ReTranscribeSelectedAsync();

    public string CategoryTextOf(AudioCategory c) => Converters.CategoryText.Of(c);

    private static string Value(string s) => string.IsNullOrWhiteSpace(s) ? "-" : s;

    internal static string SafeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "音频";
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }
}
