using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DFAudioStudio.App.Converters;
using DFAudioStudio.App.Services;
using DFAudioStudio.Core.Services;
using Microsoft.UI.Dispatching;

namespace DFAudioStudio.App.ViewModels;

/// <summary>设置页：目录 / 模型 / 语言 / 并发 / 启动时自动开始识别（默认关闭）/ 导出目录 的编辑与保存。</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly DispatcherQueue? _ui;

    private CancellationTokenSource? _downloadCts;

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        _ui = UiDispatch.Capture();

        Languages.Add(new ChoiceOption("中文（简体）", "zh"));
        Languages.Add(new ChoiceOption("英语", "en"));
        Languages.Add(new ChoiceOption("日语", "ja"));
        Languages.Add(new ChoiceOption("韩语", "ko"));
        Languages.Add(new ChoiceOption("自动检测", "auto"));

        foreach (var model in ModelManager.Catalog)
            Models.Add(new ChoiceOption($"{model.Name} · {model.SizeText} — {model.Note}", model.Name, null, model));

        // 主题：跟随系统 / 浅色 / 深色（Value 与 AppSettings.Theme 的取值一致）
        ThemeOptions.Add(new ChoiceOption("跟随系统", nameof(AppThemeMode.System)));
        ThemeOptions.Add(new ChoiceOption("浅色", nameof(AppThemeMode.Light)));
        ThemeOptions.Add(new ChoiceOption("深色", nameof(AppThemeMode.Dark)));

        // 与主窗口侧栏底部的开关同步：两边改的是同一个值，谁改都会广播到另一边。
        ThemeService.ModeChanged += OnThemeModeChanged;

        LoadFromSettings();
    }

    public ObservableCollection<ChoiceOption> Languages { get; } = new();

    public ObservableCollection<ChoiceOption> Models { get; } = new();

    // ── 外观 / 主题 ─────────────────────────────────────────────────────────

    public ObservableCollection<ChoiceOption> ThemeOptions { get; } = new();

    private ChoiceOption? _selectedTheme;
    private bool _suppressThemeChange;

    /// <summary>
    /// 主题（跟随系统 / 浅色 / 深色）。选择后立即生效并立即写入设置（和侧栏开关的行为保持一致），
    /// 切换动画由主窗口负责播放。
    /// </summary>
    public ChoiceOption? SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            // ComboBox 在填充 ItemsSource 时会把 SelectedItem 置空并回写 null —— 直接忽略，别把当前主题冲掉
            if (value is null) return;
            if (!SetProperty(ref _selectedTheme, value)) return;
            if (_suppressThemeChange) return;

            ThemeService.SetMode(ThemeService.Parse(value.Value));
        }
    }

    private void OnThemeModeChanged(AppThemeMode mode) => SyncThemeSelection(mode);

    /// <summary>把下拉同步到当前主题（不回流触发 SetMode）。</summary>
    private void SyncThemeSelection(AppThemeMode mode)
    {
        try
        {
            _suppressThemeChange = true;
            SelectedTheme = Find(ThemeOptions, ThemeService.ToSettingValue(mode))
                            ?? (ThemeOptions.Count > 0 ? ThemeOptions[0] : null);
        }
        finally
        {
            _suppressThemeChange = false;
        }
    }

    // ── 目录 / 路径 ─────────────────────────────────────────────────────────

    private string _mediaRoot = "";
    public string MediaRoot
    {
        get => _mediaRoot;
        set { if (SetProperty(ref _mediaRoot, value)) AutoSave("音频目录（Media）"); }
    }

    private string _localizedRoot = "";
    public string LocalizedRoot
    {
        get => _localizedRoot;
        set { if (SetProperty(ref _localizedRoot, value)) AutoSave("音频目录（Localized）"); }
    }

    private string _modelPath = "";
    /// <summary>
    /// 模型路径。允许两种写法：
    /// · 指向 <c>ggml-*.bin</c> 文件本身；
    /// · 指向一个**文件夹** —— 会自动扫描里面（含两层子目录）有没有模型，找到就自动填上。
    /// </summary>
    public string ModelPath
    {
        get => _modelPath;
        set
        {
            if (!SetProperty(ref _modelPath, value)) return;
            AutoDetectModel();
            AutoSave("模型路径");
        }
    }

    private string _exportDir = "";
    public string ExportDir
    {
        get => _exportDir;
        set { if (SetProperty(ref _exportDir, value)) AutoSave("导出目录"); }
    }

    // ── 识别参数 ────────────────────────────────────────────────────────────

    private ChoiceOption? _selectedLanguage;
    public ChoiceOption? SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            // 下拉在填充 ItemsSource 时会把 SelectedItem 置空并回写 null —— 忽略，别把语言冲成默认值
            if (value is null) return;
            if (SetProperty(ref _selectedLanguage, value) && !_loading) AutoSave("识别语言", immediate: true);
        }
    }

    private double _parallelism = 1;
    public double Parallelism
    {
        get => _parallelism;
        set
        {
            if (double.IsNaN(value)) return;   // NumberBox 清空时会送 NaN，别写进去
            if (SetProperty(ref _parallelism, value) && !_loading) AutoSave("识别并发数", immediate: true);
        }
    }

    private double _timeoutMinutes = 10;
    public double TimeoutMinutes
    {
        get => _timeoutMinutes;
        set
        {
            if (double.IsNaN(value)) return;
            if (SetProperty(ref _timeoutMinutes, value) && !_loading) AutoSave("单文件超时", immediate: true);
        }
    }

    private bool? _transcribeMusic;
    public bool? TranscribeMusic
    {
        get => _transcribeMusic;
        set { if (SetProperty(ref _transcribeMusic, value) && !_loading) AutoSave("连音乐一起识别", immediate: true); }
    }

    private bool? _autoStartTranscription;
    /// <summary>启动时自动开始识别。**默认关闭**：启动更快，需要时手动点「开始识别（手动）」。由 AppSettings.Load() 填入实际值。</summary>
    public bool? AutoStartTranscription
    {
        get => _autoStartTranscription;
        set { if (SetProperty(ref _autoStartTranscription, value) && !_loading) AutoSave("启动时自动开始识别", immediate: true); }
    }

    // ── 启动片头 ────────────────────────────────────────────────────────────

    private bool _playSplashOnStartup = true;
    /// <summary>启动时播放片头视频（对应 AppSettings.PlaySplashOnStartup）。默认开。</summary>
    public bool PlaySplashOnStartup
    {
        get => _playSplashOnStartup;
        set { if (SetProperty(ref _playSplashOnStartup, value) && !_loading) AutoSave("启动片头", immediate: true); }
    }

    private string _splashVideoPath = "";
    /// <summary>片头视频路径（对应 AppSettings.SplashVideoPath）。</summary>
    public string SplashVideoPath
    {
        get => _splashVideoPath;
        set
        {
            if (!SetProperty(ref _splashVideoPath, value)) return;
            OnPropertyChanged(nameof(SplashVideoHint));
            AutoSave("片头视频路径");
        }
    }

    /// <summary>片头视频当前状态：路径为空或文件不存在时，启动会跳过片头（界面照常显示）。</summary>
    public string SplashVideoHint => string.IsNullOrWhiteSpace(SplashVideoPath)
        ? "未设置片头视频：启动时不会播放片头。"
        : (System.IO.File.Exists(SplashVideoPath)
            ? "片头视频：" + SplashVideoPath
            : "片头视频：" + SplashVideoPath + "（找不到该文件：启动时会跳过片头）");

    // ── 模型下载 ────────────────────────────────────────────────────────────

    private ChoiceOption? _selectedModel;
    public ChoiceOption? SelectedModel { get => _selectedModel; set => SetProperty(ref _selectedModel, value); }

    private bool? _useMirror = true;
    /// <summary>使用 hf-mirror.com 镜像（国内网络更稳）。</summary>
    public bool? UseMirror { get => _useMirror; set => SetProperty(ref _useMirror, value); }

    private double _downloadPercent;
    public double DownloadPercent { get => _downloadPercent; private set => SetProperty(ref _downloadPercent, value); }

    private string _downloadText = "";
    public string DownloadText { get => _downloadText; private set => SetProperty(ref _downloadText, value); }

    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        private set { if (SetProperty(ref _isDownloading, value)) OnPropertyChanged(nameof(CanDownload)); }
    }

    public bool CanDownload => !IsDownloading;

    // ── 提示 ────────────────────────────────────────────────────────────────

    private string _statusText = "改动会自动保存，不需要手动点保存。";
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    private string _modelHint = "";
    public string ModelHint { get => _modelHint; private set => SetProperty(ref _modelHint, value); }

    private string _folderScanHint = "";
    /// <summary>「这个文件夹里有没有模型」的自动识别结果。</summary>
    public string FolderScanHint { get => _folderScanHint; private set => SetProperty(ref _folderScanHint, value); }

    /// <summary>在所选文件夹里扫到的模型（多个时界面显示下拉让人选）。</summary>
    public ObservableCollection<ModelManager.ModelFile> ModelsInFolder { get; } = new();

    private ModelManager.ModelFile? _selectedModelInFolder;
    /// <summary>扫描结果下拉里选中的那个模型：选中即写入模型路径。</summary>
    public ModelManager.ModelFile? SelectedModelInFolder
    {
        get => _selectedModelInFolder;
        set
        {
            if (!SetProperty(ref _selectedModelInFolder, value)) return;
            if (_loading || value is null) return;
            ModelPath = value.Path;   // 触发自动保存
        }
    }

    /// <summary>扫描结果是否多于一个（界面据此决定要不要显示下拉）。</summary>
    public bool HasMultipleModelsInFolder => ModelsInFolder.Count > 1;

    /// <summary>同上，直接给 Visibility 用（x:Bind 不做 bool→Visibility 的隐式转换）。</summary>
    public Microsoft.UI.Xaml.Visibility MultipleModelsVisibility
        => HasMultipleModelsInFolder ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public string DataDirText => "数据目录：" + _services.Settings.DataDir + "　·　模型目录：" + _services.ModelsDir;

    // ── 自动保存 ────────────────────────────────────────────────────────────

    private bool _loading;
    private bool _loaded;                 // LoadFromSettings 跑过没有
    private DispatcherQueueTimer? _autoSaveTimer;
    private string _pendingSaveReason = "";
    private DateTime _lastSavedAt = DateTime.MinValue;

    /// <summary>
    /// 自动保存。文本类（路径）走 500ms 防抖，避免一个字一个字敲的时候疯狂写盘；
    /// 下拉 / 开关 / 数字这类一次到位的改动直接立即保存（<paramref name="immediate"/>）。
    ///
    /// 重要闸门：**设置还没从文件读进来之前，一律不写盘**。
    /// 设置页刚构造时，各个控件会把自己的默认值（空字符串 / 开关默认开）回填给 ViewModel，
    /// 那些事件如果在读配置之前触发自动保存，就会把默认值当成用户选择写进设置文件
    /// （实测：进一次设置页，片头视频路径被冲成空、片头开关被改成开）。
    /// </summary>
    public void AutoSave(string reason, bool immediate = false)
    {
        if (_loading || !_loaded) return;

        _pendingSaveReason = reason;

        if (immediate)
        {
            FlushAutoSave();
            return;
        }

        if (_autoSaveTimer is null)
        {
            _autoSaveTimer = _ui?.CreateTimer();
            if (_autoSaveTimer is null)
            {
                FlushAutoSave();   // 拿不到 UI 队列（理论上不会）就退回立即保存
                return;
            }

            _autoSaveTimer.Interval = TimeSpan.FromMilliseconds(500);
            _autoSaveTimer.IsRepeating = false;
            _autoSaveTimer.Tick += (_, _) => FlushAutoSave();
        }

        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    /// <summary>把挂起的自动保存立刻写盘（文本框失焦、选完路径、离开页面时调）。</summary>
    public void FlushAutoSave()
    {
        try { _autoSaveTimer?.Stop(); } catch { }
        if (_loading || !_loaded) return;
        SaveToSettings(_pendingSaveReason);
        _pendingSaveReason = "";
    }

    // ── 读写配置 ────────────────────────────────────────────────────────────

    public void LoadFromSettings()
    {
        var s = _services.Settings;

        _loading = true;
        try
        {
            MediaRoot = s.MediaRoot ?? "";
            LocalizedRoot = s.LocalizedRoot ?? "";
            ModelPath = s.ModelPath ?? "";
            ExportDir = s.ExportDir ?? "";
            Parallelism = s.Parallelism;
            TimeoutMinutes = s.TimeoutMinutes;
            TranscribeMusic = s.TranscribeMusic;
            AutoStartTranscription = s.AutoStartTranscription;
            PlaySplashOnStartup = s.PlaySplashOnStartup;
            SplashVideoPath = s.SplashVideoPath ?? "";

            SelectedLanguage = Find(Languages, s.Language) ?? Languages[0];

            // 主题：显示当前真正生效的模式（ThemeService 是单一事实来源；System 跟随系统）
            ThemeService.Initialize();
            SyncThemeSelection(ThemeService.Current);
        }
        finally
        {
            _loading = false;
            _loaded = true;     // 到这一步设置才算"读进来了"，此后才允许自动保存
        }

        RefreshModelHint();
        AutoDetectModel();          // 进入设置页先看一眼「现在这个模型路径 / 模型目录里到底有没有模型」
        StatusText = "改动会自动保存，不需要手动点保存。";
    }

    /// <summary>保存到设置文件。<paramref name="reason"/> 只用于状态栏文案。</summary>
    public void SaveToSettings(string? reason = null)
    {
        var s = _services.Settings;

        // 先把要写的值算出来，和当前设置比对：一模一样就不写盘、不刷状态栏。
        // （设置页刚打开时，控件的绑定回填会触发一堆 change 事件，值其实没变 —— 这一步把那些噪音挡掉。）
        string media = MediaRoot?.Trim() ?? "";
        string localized = LocalizedRoot?.Trim() ?? "";
        string model = ModelPath?.Trim() ?? "";
        string export = ExportDir?.Trim() ?? "";
        string language = SelectedLanguage?.Value ?? "zh";
        int parallelism = double.IsNaN(Parallelism) ? 1 : Math.Clamp((int)Math.Round(Parallelism), 1, 8);
        int timeout = double.IsNaN(TimeoutMinutes) ? 10 : Math.Clamp((int)Math.Round(TimeoutMinutes), 1, 600);
        bool music = TranscribeMusic == true;
        bool autoStart = AutoStartTranscription == true;
        string splash = SplashVideoPath?.Trim() ?? "";
        bool playSplash = PlaySplashOnStartup;
        string theme = ThemeService.ToSettingValue(ThemeService.Current);

        bool unchanged =
            string.Equals(s.MediaRoot ?? "", media, StringComparison.Ordinal) &&
            string.Equals(s.LocalizedRoot ?? "", localized, StringComparison.Ordinal) &&
            string.Equals(s.ModelPath ?? "", model, StringComparison.Ordinal) &&
            string.Equals(s.ExportDir ?? "", export, StringComparison.Ordinal) &&
            string.Equals(s.Language ?? "", language, StringComparison.Ordinal) &&
            s.Parallelism == parallelism &&
            s.TimeoutMinutes == timeout &&
            s.TranscribeMusic == music &&
            s.AutoStartTranscription == autoStart &&
            string.Equals(s.SplashVideoPath ?? "", splash, StringComparison.Ordinal) &&
            s.PlaySplashOnStartup == playSplash &&
            string.Equals(s.Theme ?? "", theme, StringComparison.Ordinal);

        if (unchanged)
        {
            StatusText = "设置没有变化（自动保存已跳过写盘）。";
            return;
        }

        s.MediaRoot = media;
        s.LocalizedRoot = localized;
        s.ModelPath = model;
        s.ExportDir = export;
        s.Language = language;
        s.Parallelism = parallelism;
        s.TimeoutMinutes = timeout;
        s.TranscribeMusic = music;
        s.AutoStartTranscription = autoStart;
        s.SplashVideoPath = splash;
        s.PlaySplashOnStartup = playSplash;
        // 主题有自己的保存路径（选完即存），这里再写一次保证设置文件与界面完全一致。
        s.Theme = theme;

        // 回写（把夹取/默认值显示出来）
        _loading = true;
        try
        {
            Parallelism = s.Parallelism;
            TimeoutMinutes = s.TimeoutMinutes;
        }
        finally
        {
            _loading = false;
        }

        _services.SaveSettings();
        _lastSavedAt = DateTime.Now;
        RefreshModelHint();

        string what = string.IsNullOrWhiteSpace(reason) ? "设置" : reason;
        StatusText = $"已自动保存（{what}）· {_lastSavedAt:HH:mm:ss} · 语言 {s.Language} · 并发 {s.Parallelism} · 超时 {s.TimeoutMinutes} 分钟";

        // 临时诊断：把真正写进文件的路径打出来（排查"路径被自动保存冲掉"）
        _services.LogInfo($"自动保存写入[{(string.IsNullOrWhiteSpace(reason) ? "设置" : reason)}]：" +
                          $"Media='{s.MediaRoot}' Localized='{s.LocalizedRoot}' Model='{s.ModelPath}' " +
                          $"Splash='{s.SplashVideoPath}' 播放片头={s.PlaySplashOnStartup}");
    }

    // ── 自动识别模型 ────────────────────────────────────────────────────────

    /// <summary>
    /// 自动识别「这个文件夹里有没有模型」（进入设置页、下载完成后调用）：
    /// · 模型路径填的是文件夹 → 扫里面（含两层子目录）找 <c>ggml-*.bin</c>；找到一个就自动填上，
    ///   找到多个就列在下拉里让你挑，一个都没找到就明确说明；
    /// · 模型路径填的是文件 → 只校验文件在不在（不会去改你选定的那个文件）；
    /// · 路径为空 → 顺手看一眼默认模型目录（<c>&lt;数据目录&gt;\models</c>）里有什么。
    /// </summary>
    public void AutoDetectModel()
    {
        RefreshModelHint();
        ScanModels(ResolveScanFolder(), autoFill: true);
    }

    /// <summary>
    /// 用户正在手敲 / 粘贴模型路径：**只扫描并报结论，不改写输入框**。
    /// （边打字边把内容换成别的路径会非常难用；要自动填就离开输入框，或点「识别文件夹」。）
    /// </summary>
    public void ReportModelsForTypedPath(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed))
        {
            ModelsInFolder.Clear();
            OnPropertyChanged(nameof(HasMultipleModelsInFolder));
            OnPropertyChanged(nameof(MultipleModelsVisibility));
            FolderScanHint = "还没有可扫描的文件夹：可以点「识别文件夹」选一个，或直接点「选择文件」选 ggml-*.bin。";
            return;
        }

        ScanModels(typed.Trim(), autoFill: false);
    }

    /// <summary>文本框失焦 / 点「识别文件夹」：这次可以自动填了。</summary>
    public void CommitModelPath(string? folder = null)
    {
        ScanModels(string.IsNullOrWhiteSpace(folder) ? ResolveScanFolder() : folder.Trim(), autoFill: true);
    }

    /// <summary>该拿哪个文件夹去扫：路径是文件夹就用它，是文件就用它所在目录，空就用默认模型目录。</summary>
    private string ResolveScanFolder()
    {
        var path = ModelPath?.Trim() ?? "";
        if (path.Length > 0)
        {
            try
            {
                if (Directory.Exists(path)) return path;
                if (File.Exists(path))
                {
                    var dir = System.IO.Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) return dir;
                }
            }
            catch
            {
                // 路径非法就当没填
            }
        }
        return _services.ModelsDir;
    }

    /// <summary>
    /// 扫描一个文件夹里的模型并给出结论。
    /// <paramref name="autoFill"/> = true 时会把识别到的模型写进模型路径（并自动保存）；
    /// false 时只报告，不动用户的输入。
    /// </summary>
    private void ScanModels(string? folder, bool autoFill)
    {
        ModelsInFolder.Clear();
        _loading = true;
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                FolderScanHint = string.IsNullOrWhiteSpace(folder)
                    ? "还没有可扫描的文件夹：可以点「识别文件夹」选一个，或直接点「选择文件」选 ggml-*.bin。"
                    : "找不到这个文件夹：" + folder;
                return;
            }

            foreach (var m in ModelManager.FindModels(folder)) ModelsInFolder.Add(m);
        }
        finally
        {
            _loading = false;
        }

        OnPropertyChanged(nameof(HasMultipleModelsInFolder));
        OnPropertyChanged(nameof(MultipleModelsVisibility));

        if (ModelsInFolder.Count == 0)
        {
            FolderScanHint = $"自动识别：{folder} 里（含子目录）没有找到模型 —— 请确认放的是 ggml-*.bin（whisper.cpp 格式）。";
            return;
        }

        var current = ModelPath?.Trim() ?? "";
        bool currentIsAFile = false;
        try { currentIsAFile = current.Length > 0 && File.Exists(current); } catch { /* 非法路径 */ }
        bool currentIsInList = ModelsInFolder.Any(m => string.Equals(m.Path, current, StringComparison.OrdinalIgnoreCase));

        // 用户已经明确指到一个存在的模型文件上：尊重这个选择，只报"这个文件夹里还有别的什么"
        if (currentIsAFile && !autoFill)
        {
            FolderScanHint = ModelsInFolder.Count == 1
                ? $"自动识别：{System.IO.Path.GetDirectoryName(current)} 里找到 1 个模型，就是当前这个。"
                : $"自动识别：{System.IO.Path.GetDirectoryName(current)} 里一共 {ModelsInFolder.Count} 个模型文件（可以在下面换一个）。";
            return;
        }

        var biggest = ModelsInFolder[0];
        bool fill = autoFill || !currentIsAFile;   // 输入框里是文件夹/空的时候才允许改写

        if (ModelsInFolder.Count == 1)
        {
            FolderScanHint = $"自动识别：在 {System.IO.Path.GetDirectoryName(biggest.Path)} 里找到模型 {biggest.FileName}（{biggest.SizeText}）"
                             + (currentIsInList || !fill ? "，就是当前使用的这个。" : "，已自动填入模型路径。");
            if (fill && !currentIsInList) ModelPath = biggest.Path;
            return;
        }

        if (fill && !currentIsInList)
        {
            FolderScanHint = $"自动识别：找到 {ModelsInFolder.Count} 个模型文件，已先选最大的 {biggest.FileName}（{biggest.SizeText}），下面可以换。";
            ModelPath = biggest.Path;
            SelectedModelInFolder = biggest;
        }
        else
        {
            FolderScanHint = $"自动识别：找到 {ModelsInFolder.Count} 个模型文件（最大的 {biggest.FileName} · {biggest.SizeText}）。";
            SelectedModelInFolder = ModelsInFolder.FirstOrDefault(m => string.Equals(m.Path, current, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static ChoiceOption? Find(ObservableCollection<ChoiceOption> options, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        foreach (var option in options)
            if (string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase)) return option;
        return null;
    }

    private void RefreshModelHint()
    {
        var path = ModelPath?.Trim() ?? "";

        if (path.Length == 0)
        {
            ModelHint = "当前未配置模型：识别不会启动。可以先在下面选一个 ggml 模型点「下载」，或点「识别文件夹」自动找本地模型。";
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                ModelHint = "这是文件夹，不是 .bin 文件：已按文件夹自动识别（结果见上一行）。";
                return;
            }
        }
        catch
        {
            // 路径非法按"找不到文件"处理
        }

        ModelHint = _services.IsModelReady
            ? "当前模型：" + System.IO.Path.GetFileName(path) + "（文件存在）"
            : "当前模型：" + System.IO.Path.GetFileName(path) + "（找不到该文件，请重新选择）";
    }

    // ── 下载模型 ────────────────────────────────────────────────────────────

    public async Task DownloadModelAsync()
    {
        if (IsDownloading) return;

        if (SelectedModel?.Tag is not ModelManager.ModelInfo info)
        {
            StatusText = "请先在「模型下载」里选择一个模型。";
            return;
        }

        IsDownloading = true;
        DownloadPercent = 0;
        DownloadText = "正在连接…";
        _downloadCts = new CancellationTokenSource();
        var token = _downloadCts.Token;
        bool useMirror = UseMirror == true;

        var progress = new Progress<(long got, long total, double mbs)>(p => UiDispatch.Run(_ui, () =>
        {
            if (p.total > 0)
            {
                DownloadPercent = Math.Min(100, p.got * 100.0 / p.total);
                DownloadText = $"{CategoryText.Bytes(p.got)} / {CategoryText.Bytes(p.total)} · {p.mbs:F1} MB/s";
            }
            else
            {
                DownloadText = $"{CategoryText.Bytes(p.got)} · {p.mbs:F1} MB/s";
            }
        }));

        try
        {
            _services.LogInfo($"开始下载模型 {info.Name}（{(useMirror ? "hf-mirror 镜像" : "HuggingFace 官方")}）");
            string path = await ModelManager
                .DownloadAsync(info, _services.ModelsDir, useMirror, progress, token)
                .ConfigureAwait(true);

            ModelPath = path;          // 触发自动保存（防抖）
            DownloadPercent = 100;
            FlushAutoSave();           // 下载是多步流程，这里立刻落盘，别让 500ms 防抖把提示顶掉
            AutoDetectModel();
            StatusText = "模型下载完成：" + path + "（已自动填入模型路径并保存）";
            _services.LogInfo("模型下载完成：" + path);
        }
        catch (OperationCanceledException)
        {
            StatusText = "模型下载已取消（已下载的部分保留为 .part 文件）。";
        }
        catch (Exception ex)
        {
            StatusText = "模型下载失败：" + ex.Message;
            _services.LogWarn("模型下载失败：" + ex.Message);
        }
        finally
        {
            UiDispatch.Run(_ui, () =>
            {
                IsDownloading = false;
                DownloadText = "";
            });
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    public void CancelDownload()
    {
        try { _downloadCts?.Cancel(); } catch { }
    }
}
