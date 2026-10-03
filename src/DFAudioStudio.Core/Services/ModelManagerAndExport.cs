using System.Net.Http;
using System.Text;
using System.Text.Json;
using DFAudioStudio.Core.Models;

namespace DFAudioStudio.Core.Services;

/// <summary>Whisper ggml 模型下载（支持镜像），带进度回调。</summary>
public static class ModelManager
{
    public sealed record ModelInfo(string Name, string SizeText, string Url, string MirrorUrl, string Note);

    public static readonly ModelInfo[] Catalog =
    {
        new("ggml-tiny.bin",  "约 75 MB",  "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin",   "https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin",   "最快最省，中文勉强"),
        new("ggml-base.bin",  "约 142 MB", "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin",   "https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/ggml-base.bin",   "轻量，中文一般"),
        new("ggml-small.bin", "约 466 MB", "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin",  "https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/ggml-small.bin",  "推荐起步：中文可用、速度可接受"),
        new("ggml-medium.bin","约 1.5 GB", "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.bin", "https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/ggml-medium.bin", "中文准确率明显更好，CPU 较慢"),
        new("ggml-large-v3-turbo.bin", "约 1.6 GB", "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo.bin", "https://hf-mirror.com/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo.bin", "精度最高（turbo 版速度尚可）"),
    };

    public static async Task<string> DownloadAsync(ModelInfo model, string modelsDir, bool useMirror,
                                                   IProgress<(long got, long total, double mbs)>? progress,
                                                   CancellationToken ct)
    {
        Directory.CreateDirectory(modelsDir);
        string target = System.IO.Path.Combine(modelsDir, model.Name);
        string url = useMirror ? model.MirrorUrl : model.Url;

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DFAudioStudio/1.0");
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        long total = resp.Content.Headers.ContentLength ?? -1;
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(target + ".part", FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);

        var buffer = new byte[1 << 20];
        long got = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int read;
        while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            got += read;
            double mbs = got / 1024.0 / 1024.0 / Math.Max(0.001, sw.Elapsed.TotalSeconds);
            progress?.Report((got, total, mbs));
        }
        await dst.FlushAsync(ct).ConfigureAwait(false);
        dst.Close();

        if (File.Exists(target)) File.Delete(target);
        File.Move(target + ".part", target);
        return target;
    }

    // ── 自动识别「这个文件夹里有没有模型」 ────────────────────────────────────

    /// <summary>一个候选模型文件。</summary>
    public sealed record ModelFile(string Path, long Bytes, bool IsWhisperName)
    {
        public string FileName => System.IO.Path.GetFileName(Path);

        /// <summary>「466.1 MB」这种给人看的大小。</summary>
        public string SizeText => Bytes >= 1024L * 1024 * 1024
            ? $"{Bytes / 1024.0 / 1024 / 1024:F2} GB"
            : $"{Bytes / 1024.0 / 1024:F1} MB";

        public string Label => $"{FileName} · {SizeText}" + (IsWhisperName ? "" : "（文件名不像 whisper 模型）");
    }

    /// <summary>
    /// 在文件夹里找 Whisper ggml 模型：
    /// · 优先 <c>ggml-*.bin</c>（whisper.cpp 的官方命名），其余 <c>*.bin</c> 作为兜底；
    /// · 同名只保留一份，按「先 ggml- 后其它、再按文件大小从大到小」排序；
    /// · 默认最多往下找 <paramref name="maxDepth"/> 层子目录（很多人会把模型放在 models\whisper\ 里）；
    /// · 打不开的目录 / 文件直接跳过，不抛异常（设置页要能随便乱输路径）。
    /// </summary>
    public static List<ModelFile> FindModels(string? folder, int maxDepth = 2)
    {
        var found = new List<ModelFile>();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return found;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string dir, int depth)>();
        queue.Enqueue((folder, 0));

        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.bin"); }
            catch { files = Array.Empty<string>(); }

            foreach (var file in files)
            {
                try
                {
                    var name = System.IO.Path.GetFileName(file);
                    if (name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) continue;   // 没下完的半成品
                    if (!seen.Add(file)) continue;

                    var info = new FileInfo(file);
                    if (info.Length <= 0) continue;                                            // 0 字节的占位文件不算
                    found.Add(new ModelFile(file, info.Length, name.StartsWith("ggml-", StringComparison.OrdinalIgnoreCase)));
                }
                catch
                {
                    // 单个文件读不到就跳过
                }
            }

            if (depth >= maxDepth) continue;

            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(dir); }
            catch { subs = Array.Empty<string>(); }

            foreach (var sub in subs)
            {
                // 明显是数据/缓存目录的跳过，省得在几十万文件里翻
                var leaf = System.IO.Path.GetFileName(sub);
                if (leaf.Equals("logs", StringComparison.OrdinalIgnoreCase) ||
                    leaf.Equals("thumbs", StringComparison.OrdinalIgnoreCase) ||
                    leaf.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
                queue.Enqueue((sub, depth + 1));
            }
        }

        return found
            .OrderByDescending(m => m.IsWhisperName)
            .ThenByDescending(m => m.Bytes)
            .ToList();
    }
}

/// <summary>导出：CSV 清单 / 单条 SRT 字幕 / 按分区合并的 TXT 文本。</summary>
public static class ExportService
{
    public static string ExportCsv(IEnumerable<AudioItem> items, string dir, string fileName = "音频索引.csv")
    {
        Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, fileName);
        var sb = new StringBuilder();
        sb.AppendLine("分区,子分类,文件名,时长秒,时长,干员,事件,地图,阵营,皮肤,状态,识别文本,路径");
        foreach (var i in items)
        {
            sb.AppendLine(string.Join(',',
                Q(i.Category.ToString()), Q(i.SubCategory), Q(i.FileName), i.DurationSec.ToString("F2"), Q(i.DurationText),
                Q(i.Operator), Q(i.EventTag), Q(i.MapTag), Q(i.SideTag), Q(i.SkinTag),
                Q(i.Status.ToString()), Q(i.Transcript), Q(i.Path)));
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        return path;
    }

    public static string ExportSrt(AudioItem item, string dir)
    {
        Directory.CreateDirectory(dir);
        string safe = string.Join("_", item.FileName.Split(System.IO.Path.GetInvalidFileNameChars()));
        string path = System.IO.Path.Combine(dir, System.IO.Path.GetFileNameWithoutExtension(safe) + ".srt");
        var segs = ParseSegments(item.SegmentsJson);
        var sb = new StringBuilder();
        if (segs.Count == 0)
        {
            sb.AppendLine("1").AppendLine("00:00:00,000 --> 00:00:10,000").AppendLine(item.Transcript);
        }
        else
        {
            int n = 1;
            foreach (var s in segs)
            {
                sb.AppendLine(n++.ToString());
                sb.AppendLine($"{Ts(s.Start)} --> {Ts(s.End)}");
                sb.AppendLine(s.Text);
                sb.AppendLine();
            }
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        return path;
    }

    public static string ExportTxtBundle(IEnumerable<AudioItem> items, string dir, string fileName)
    {
        Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, fileName);
        var sb = new StringBuilder();
        int n = 0;
        foreach (var i in items.OrderBy(x => x.Path))
        {
            if (string.IsNullOrWhiteSpace(i.Transcript)) continue;
            n++;
            sb.AppendLine($"### {n}. {i.FileName}");
            sb.AppendLine($"分区: {i.Category} | 子分类: {i.SubCategory} | 干员: {i.Operator} | 事件: {i.EventTag} | 地图: {i.MapTag} | 时长: {i.DurationText}");
            sb.AppendLine($"路径: {i.Path}");
            sb.AppendLine(i.Transcript.Trim());
            sb.AppendLine();
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        return path;
    }

    private static List<TranscriptSegment> ParseSegments(string json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<TranscriptSegment>();
            return JsonSerializer.Deserialize<List<TranscriptSegment>>(json) ?? new List<TranscriptSegment>();
        }
        catch { return new List<TranscriptSegment>(); }
    }

    private static string Ts(double sec)
    {
        var t = TimeSpan.FromSeconds(sec);
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}";
    }

    private static string Q(string s)
    {
        s ??= "";
        return s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? "\"" + s.Replace("\"", "\"\"").Replace("\n", " ").Replace("\r", "") + "\""
            : s;
    }
}
