using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Windows.Media.Playback;

namespace DFAudioStudio.App.Services;

/// <summary>
/// 媒体播放诊断小工具：音频播放条（<see cref="Microsoft.UI.Xaml.Controls.MediaPlayerElement"/> 之外的
/// 自建 MediaPlayer）与主窗口启动片头两条播放路径**共用同一套**「失败原因 + 路径体检」写法，
/// 目的是一次点击就能在日志里看到真实原因，而不是靠猜。
///
/// ⚠ 关于 <c>new Uri(path)</c>（已被实测推翻的旧结论 + 真正原因）：
/// · 实测 <c>new Uri(@"I:\新建文件夹 (19)\…\x.wav")</c> **不会抛异常**，得到
///   <c>file:///I:/%E6%96%B0%E5%BB%BA…/x.wav</c>（裸盘符与中文都被正常转义）——所以「UriFormatException 导致播放失败」是错的；
/// · 真正的失败在 MF 内部：把这个**转义后**的 file:// URL 交给 <c>MFCreateSourceReaderFromURL</c> 会返回
///   <c>0x80070003</c>（ERROR_PATH_NOT_FOUND）——MF 的 file:// 处理解不了转义的非 ASCII 路径（%20 这种单字节转义没问题）；
/// · 同一个文件换成「不转义的中文原样 URL」或者「打开文件流（= <c>MediaSource.CreateFromStorageFile</c> 的走法）」
///   都能正常解出音频样本。所以两条播放路径一律走 StorageFile，绝不用 Uri。
/// </summary>
internal static class MediaDiagnostics
{
    /// <summary>
    /// 把 <see cref="MediaPlayerFailedEventArgs"/> 拆成一行可读文本：
    /// Error（枚举）/ ErrorCode（若该版本存在）/ ExtendedErrorCode（含数值 HRESULT）/ ErrorMessage 全部带上。
    /// </summary>
    public static string DescribeFailure(MediaPlayerFailedEventArgs? args)
    {
        if (args is null) return "MediaFailed（事件参数为空）";

        var parts = new List<string>
        {
            "Error=" + args.Error,
            "ErrorMessage=" + (string.IsNullOrWhiteSpace(args.ErrorMessage) ? "(空)" : args.ErrorMessage)
        };

        // ErrorCode：官方文档里 MediaPlayerFailedEventArgs 只有 Error / ErrorMessage / ExtendedErrorCode，
        // 不同 SDK 版本成员略有出入，所以这里反射探一下 —— 有就一起打出来，没有就跳过，
        // 不会因为成员不存在而编译或运行失败。
        string? errorCode = TryReadErrorCode(args);
        if (errorCode is not null) parts.Add("ErrorCode=" + errorCode);

        var code = args.ExtendedErrorCode;
        parts.Add(code is null
            ? "ExtendedErrorCode=(空)"
            : $"ExtendedErrorCode=0x{code.HResult:X8}（{code.GetType().Name}：{code.Message}）");

        return string.Join("，", parts);
    }

    private static string? TryReadErrorCode(MediaPlayerFailedEventArgs args)
    {
        try
        {
            PropertyInfo? property = args.GetType().GetProperty("ErrorCode");
            object? value = property?.GetValue(args);
            return value?.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 路径体检：文件是否存在、大小、用旧写法（<c>new Uri(path)</c>）算出来的绝对 URI、
    /// 以及路径里有没有非 ASCII 字符 —— 用于区分「编码/路径问题」与「解码器/文件问题」。
    ///
    /// 实测结论（本机 Windows + Media Foundation，用 MFCreateSourceReaderFromURL / FromByteStream 直接验证）：
    /// · <c>new Uri(@"I:\新建文件夹 (19)\…\x.wav").AbsoluteUri</c> = <c>file:///I:/%E6%96%B0…/x.wav</c>（不抛异常）；
    /// · 把这个**转义后**的 URL 交给 MF → <c>0x80070003</c>（ERROR_PATH_NOT_FOUND，MF 的 file:// 处理解不出多字节转义）；
    /// · 同一路径改成**不转义**的中文原样 URL，或者走「打开文件流 / StorageFile」→ 正常解出音频样本；
    /// · 纯 ASCII 路径即使带 %20 转义也正常 —— 问题只出在「转义的非 ASCII 路径」上。
    /// 所以播放一律走 <c>MediaSource.CreateFromStorageFile</c>（当前两条播放路径都是这么写的）。
    /// </summary>
    public static string DescribePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "path=(空)";

        string exists = File.Exists(path) ? "存在" : "不存在";

        string size;
        try { size = new FileInfo(path).Length.ToString("N0") + " 字节"; }
        catch { size = "未知"; }

        string uri;
        try { uri = new Uri(path).AbsoluteUri; }   // 注意：这一步本身不会抛（裸盘符 + 中文都会被正常转义）
        catch (Exception ex) { uri = "解析失败（" + ex.GetType().Name + "：" + ex.Message + "）"; }

        string hint = IsAscii(path)
            ? ""
            : "；路径含非 ASCII：上面的转义 URL 交给 MF 会返回 0x80070003（路径找不到，MF 解不了多字节 %XX），"
              + "所以必须走 MediaSource.CreateFromStorageFile（本实现已经如此）";

        return $"File.Exists={exists}，大小={size}，new Uri(path).AbsoluteUri={uri}，纯ASCII路径={(IsAscii(path) ? "是" : "否")}{hint}";
    }

    /// <summary>路径 / 文本里是否只含 ASCII 字符（隔离验证要用它判断副本路径是否真的「英文」）。</summary>
    public static bool IsAscii(string? text)
    {
        if (string.IsNullOrEmpty(text)) return true;
        foreach (char c in text)
            if (c > 127) return false;
        return true;
    }
}
