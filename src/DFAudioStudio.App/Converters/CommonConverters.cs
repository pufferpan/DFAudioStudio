using System;
using System.Globalization;
using DFAudioStudio.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace DFAudioStudio.App.Converters;

/// <summary>分区 / 状态 / 体积的统一中文文案（界面与导出保持一致）。</summary>
public static class CategoryText
{
    public static string Of(AudioCategory c) => c switch
    {
        AudioCategory.Voice => "语音",
        AudioCategory.Music => "音乐",
        AudioCategory.Sfx => "音效",
        AudioCategory.Ambience => "环境",
        AudioCategory.Ui => "界面",
        _ => "其它"
    };

    public static string OfStatus(TranscriptStatus s) => s switch
    {
        TranscriptStatus.None => "未识别",
        TranscriptStatus.Queued => "排队中",
        TranscriptStatus.Running => "识别中",
        TranscriptStatus.Done => "已识别",
        TranscriptStatus.Failed => "失败",
        TranscriptStatus.Skipped => "已跳过",
        _ => "-"
    };

    public static string Bytes(long bytes)
    {
        if (bytes <= 0) return "-";
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024 / 1024:F2} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / 1024.0 / 1024:F1} MB";
        return $"{bytes / 1024.0:F0} KB";
    }

    public static string Count(long n) => n.ToString("N0", CultureInfo.CurrentCulture);
}

/// <summary>bool / bool? → Visibility。ConverterParameter 传 "invert" 可反转。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>固定反转（也可以在 XAML 用 ConverterParameter=invert）。</summary>
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool b = value switch
        {
            bool v => v,
            string s => !string.IsNullOrWhiteSpace(s),
            int i => i != 0,
            long l => l != 0,
            _ => value is not null
        };

        bool invert = Invert || IsInvert(parameter);
        if (invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        bool b = value is Visibility v && v == Visibility.Visible;
        if (Invert || IsInvert(parameter)) b = !b;
        return b;
    }

    private static bool IsInvert(object parameter)
        => parameter is string s && (s.Equals("invert", StringComparison.OrdinalIgnoreCase) || s == "!" || s == "反");
}

/// <summary>bool → !bool（用于 IsEnabled 之类需要反向布尔的场合）。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => !ToBool(value);

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => !ToBool(value);

    internal static bool ToBool(object value) => value switch
    {
        bool b => b,
        int i => i != 0,
        long l => l != 0,
        string s => !string.IsNullOrWhiteSpace(s),
        _ => false
    };
}

/// <summary>转录状态 → 是否「可以重试」。用于队列页失败项行内那个「重试」按钮的 IsEnabled。</summary>
public sealed class RetryableStatusConverter : IValueConverter
{
    /// <summary>
    /// 与 <c>IndexDb.NextPending</c> 的口径一致：只有「未处理(0) / 失败(4)」才会被队列取走，
    /// 所以只有这两种状态值得给「重试」按钮放行。
    /// 取到的值不是一条真实条目的状态（null / 别的类型）时返回 false —— 也就是「无该项时禁用」。
    /// </summary>
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is TranscriptStatus status
           && (status == TranscriptStatus.Failed || status == TranscriptStatus.None);

    /// <summary>反向转换：直接把布尔还原成「失败」或「已识别」，界面不会再调用。</summary>
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is bool b && b ? TranscriptStatus.Failed : TranscriptStatus.Done;
}

/// <summary>Category → 中文分区名。</summary>
public sealed class CategoryToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value switch
        {
            AudioCategory c => CategoryText.Of(c),
            string s => CategoryText.Of(ParseCategory(s)),
            _ => ""
        };

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is string s ? ParseCategory(s) : AudioCategory.Other;

    public static AudioCategory ParseCategory(string text)
        => Enum.TryParse<AudioCategory>(text, true, out var c) ? c : AudioCategory.Other;
}

/// <summary>TranscriptStatus → 中文状态名。</summary>
public sealed class StatusToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value switch
        {
            TranscriptStatus s => CategoryText.OfStatus(s),
            _ => "-"
        };

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is string s && Enum.TryParse<TranscriptStatus>(s, true, out var st) ? st : TranscriptStatus.None;
}

/// <summary>字节数 → "1.5 MB"。</summary>
public sealed class BytesToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value switch
        {
            long l => CategoryText.Bytes(l),
            int i => CategoryText.Bytes(i),
            double d => CategoryText.Bytes((long)d),
            _ => "-"
        };

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => 0L;
}

/// <summary>数字 → 带千分位的文本（卡片统计用，避免 int 直接绑 TextBlock）。</summary>
public sealed class CountToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value switch
        {
            long l => CategoryText.Count(l),
            int i => CategoryText.Count(i),
            double d => CategoryText.Count((long)d),
            _ => "0"
        };

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => 0L;
}

/// <summary>识别全文 → 列表里预览的前 80 个字。</summary>
public sealed class TranscriptPreviewConverter : IValueConverter
{
    /// <summary>预览长度，默认 80 字。</summary>
    public int MaxLength { get; set; } = 80;

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string s || string.IsNullOrWhiteSpace(s)) return "";
        string flat = s.Replace("\r", " ").Replace("\n", " ").Trim();
        int max = MaxLength;
        if (parameter is string ps && int.TryParse(ps, out int p) && p > 0) max = p;
        return flat.Length > max ? flat[..max] + "…" : flat;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => "";
}

/// <summary>标签 → "干员 302"，空标签返回空串（ConverterParameter 作为前缀）。</summary>
public sealed class TagTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string s || string.IsNullOrWhiteSpace(s)) return "";
        string prefix = parameter as string ?? "";
        return prefix + s;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => "";
}
