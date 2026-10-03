using System.Text.Json;
using System.Text.Json.Serialization;

namespace DFAudioStudio.Core.Services;

/// <summary>应用配置，存在 exe 同级 data\settings.json。</summary>
public sealed class AppSettings
{
    public string MediaRoot { get; set; } = @"I:\新建文件夹 (19)\Exports\DeltaForce\Content\WwiseAudio\Media";
    public string LocalizedRoot { get; set; } = @"I:\新建文件夹 (19)\Exports\DeltaForce\Content\WwiseAudio\Localized";

    /// <summary>Whisper ggml 模型文件路径（如 ggml-medium.bin）。</summary>
    public string ModelPath { get; set; } = "";

    /// <summary>识别语言，"zh" 为中文。</summary>
    public string Language { get; set; } = "zh";

    /// <summary>
    /// 打开软件自动开始识别。**默认关闭**：启动时不加载模型（465MB）也不抢 CPU，需要时手动点「开始识别」。
    /// </summary>
    public bool AutoStartTranscription { get; set; }

    /// <summary>
    /// 启动片头视频（mp4）完整路径。默认指向本机的 mojang.mp4；
    /// 文件不存在（或路径为空）时启动会直接跳过片头，界面照常显示。
    /// </summary>
    public string SplashVideoPath { get; set; } = @"G:\工程\mojang.mp4";

    /// <summary>启动时是否播放片头视频（默认开）。关掉后启动不再显示片头覆盖层。</summary>
    public bool PlaySplashOnStartup { get; set; } = true;

    /// <summary>
    /// 新手引导是否已经走过（默认 false = 第一次打开软件时整屏铺开引导，教用户设置目录 / 模型）。
    /// 走完或点「跳过」都会置 true，之后不再自动弹；想重看可以从「新手教程」页点「重看首次引导」。
    /// </summary>
    public bool OnboardingCompleted { get; set; }

    /// <summary>配置版本号，用于一次性迁移旧配置。</summary>
    public int SettingsVersion { get; set; }

    /// <summary>
    /// 界面主题：System（跟随系统，默认）/ Light / Dark。
    /// 只影响界面配色，不影响识别；由界面层 ThemeService 读取并应用到主窗口根元素。
    /// </summary>
    public string Theme { get; set; } = "System";

    /// <summary>识别并发数（CPU 建议 1，显卡可用 2-3）。</summary>
    public int Parallelism { get; set; } = 1;

    /// <summary>是否连音乐一起识别（默认只识别语音）。</summary>
    public bool TranscribeMusic { get; set; }

    /// <summary>短于该时长(秒)的碎片直接跳过（呼吸/动作音效等，语音里这类很多）。</summary>
    public double MinDurationSec { get; set; } = 0.35;

    /// <summary>
    /// 命中这些关键词的条目不做识别（基本都是非语音素材，白白耗时）：
    /// 呼吸声、脚步、拟音、环境氛围、拍手/表情动作等。
    /// </summary>
    public string SkipKeywords { get; set; } = "Breath,Footstep,Foley,_Amb_,Clapping,Emotes_Body,Effort,Grunt";

    /// <summary>单文件识别超时（分钟）。</summary>
    public int TimeoutMinutes { get; set; } = 10;

    /// <summary>导出目录（默认 桌面\DFAudioStudio导出）。</summary>
    public string ExportDir { get; set; } = "";

    /// <summary>
    /// 统一数据目录（GUI 与命令行工具共用）：可用环境变量 DFAUDIO_DATA 覆盖，
    /// 默认 %LOCALAPPDATA%\DFAudioStudio，库里存索引与识别结果。
    /// </summary>
    public static string ResolveDataDir()
    {
        string? custom = Environment.GetEnvironmentVariable("DFAUDIO_DATA");
        if (!string.IsNullOrWhiteSpace(custom)) return custom!;
        return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DFAudioStudio");
    }

    /// <summary>
    /// 数据根目录（索引库 index.db 放这里）。留空 = 默认 %LOCALAPPDATA%\DFAudioStudio。
    /// C 盘紧张时可以指到别的盘，例如 E:\DFAudioStudio\data。
    /// </summary>
    public string DataRoot { get; set; } = "";

    /// <summary>
    /// 模型目录（ggml-*.bin 放这里）。留空 = &lt;数据根&gt;\models。
    /// C 盘紧张时建议指到别的盘，例如 E:\DFAudioStudio\models。
    /// </summary>
    public string ModelsRoot { get; set; } = "";

    public string DataDir => string.IsNullOrWhiteSpace(DataRoot) ? ResolveDataDir() : DataRoot;
    public string ModelsDir => string.IsNullOrWhiteSpace(ModelsRoot) ? System.IO.Path.Combine(DataDir, "models") : ModelsRoot;
    public string DbPath => System.IO.Path.Combine(DataDir, "index.db");
    private static string SettingsPath => System.IO.Path.Combine(ResolveDataDir(), "settings.json");

    /// <summary>配置文件真实路径（%LOCALAPPDATA%\DFAudioStudio\settings.json，或 DFAUDIO_DATA 指向的目录）。</summary>
    public static string SettingsFilePath => SettingsPath;

    private static readonly JsonSerializerOptions Opt = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Opt);
                if (s is not null)
                {
                    s.Normalize();
                    return s;
                }
            }
        }
        catch { /* 配置损坏则回落默认值 */ }
        var def = new AppSettings();
        def.Normalize();
        return def;
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, Opt));
    }

    private const int CurrentSettingsVersion = 3;

    private void Normalize()
    {
        // 迁移：v1 默认是「打开软件就自动识别」，会让启动变慢；v2 起改为手动触发。
        if (SettingsVersion < CurrentSettingsVersion)
        {
            AutoStartTranscription = false;
            SettingsVersion = CurrentSettingsVersion;
            try { Save(); } catch { }
        }

        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(ModelsDir);
        if (string.IsNullOrWhiteSpace(ExportDir))
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            ExportDir = System.IO.Path.Combine(desktop, "DFAudioStudio导出");
        }
        if (Parallelism < 1) Parallelism = 1;
        if (Parallelism > 8) Parallelism = 8;

        // 自动发现模型：设置的模型目录 → 环境变量 DFAUDIO_MODELS → 程序所在目录各级 models\ → 旧默认目录
        if (string.IsNullOrWhiteSpace(ModelPath) || !File.Exists(ModelPath))
        {
            var candidates = new List<string>();
            var dirs = new List<string> { ModelsDir };
            string? envModels = Environment.GetEnvironmentVariable("DFAUDIO_MODELS");
            if (!string.IsNullOrWhiteSpace(envModels)) dirs.Add(envModels);
            foreach (var baseDir in new[]
                     {
                         AppContext.BaseDirectory,
                         System.IO.Path.Combine(AppContext.BaseDirectory, ".."),
                         System.IO.Path.Combine(AppContext.BaseDirectory, "..", ".."),
                         System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..")
                     })
            {
                try { dirs.Add(System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, "models"))); } catch { }
            }
            dirs.Add(System.IO.Path.Combine(ResolveDataDir(), "models")); // 旧位置兼容
            foreach (var dir in dirs.Distinct())
            {
                try { if (Directory.Exists(dir)) candidates.AddRange(Directory.EnumerateFiles(dir, "ggml-*.bin")); } catch { }
            }
            // 优选精度更高的模型：large-v3-turbo → medium → small → turbo → 任意
            string? best = candidates.FirstOrDefault(f => f.Contains("large-v3-turbo", StringComparison.OrdinalIgnoreCase))
                           ?? candidates.FirstOrDefault(f => f.Contains("medium", StringComparison.OrdinalIgnoreCase))
                           ?? candidates.FirstOrDefault(f => f.Contains("small", StringComparison.OrdinalIgnoreCase))
                           ?? candidates.FirstOrDefault(f => f.Contains("turbo", StringComparison.OrdinalIgnoreCase))
                           ?? candidates.FirstOrDefault();
            if (best is not null) ModelPath = best;
        }
    }
}
