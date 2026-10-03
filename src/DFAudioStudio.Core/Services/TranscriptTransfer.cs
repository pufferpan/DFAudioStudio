using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DFAudioStudio.Core.Data;
using DFAudioStudio.Core.Models;

namespace DFAudioStudio.Core.Services;

/// <summary>一个条目在交换文件里的样子（导出 / 导入共用）。</summary>
public sealed class TranscriptEntry
{
    /// <summary>文件名（含扩展名）——匹配的第一依据。</summary>
    public string FileName { get; set; } = "";

    /// <summary>字节数。和文件名一起构成"同一条音频"的判据（Events / Media 下同一文件各存一份）。</summary>
    public long Size { get; set; }

    /// <summary>完整路径（可选）。有它就优先按路径精确匹配，跨机器/换目录时允许对不上。</summary>
    public string? Path { get; set; }

    /// <summary>识别文本（正文）。</summary>
    public string Text { get; set; } = "";

    /// <summary>分段（可选）。没有就只写正文。</summary>
    public List<TranscriptSegment>? Segments { get; set; }

    // ── 下面都是导出时附带的信息，导入时不参与匹配，只用来核对 ──

    public double DurationSec { get; set; }
    public string? Category { get; set; }
    public string? SubCategory { get; set; }
    public string? UpdatedAt { get; set; }
}

/// <summary>
/// 识别结果交换文件（导入 / 导出同一套格式）。
///
/// 格式就是下面这样的 JSON（UTF-8 无 BOM，中文不转义，直接可读可手改；
/// 键名用 PascalCase，和库里 `SegmentsJson` 的写法保持一致）：
/// <code>
/// {
///   "Format": "dfaudio-transcripts",
///   "Version": 1,
///   "App": "三角洲音频工坊 · DFAudioStudio",
///   "ExportedAt": "2026-10-03T14:22:01.3269671+08:00",
///   "Count": 2,
///   "Entries": [
///     {
///       "FileName": "Voice_302_SOL_GameStart_1_Low.wav",
///       "Size": 80396,
///       "Path": "I:\\...\\Voice_302_SOL_GameStart_1_Low.wav",
///       "Text": "我已就位",
///       "Segments": [ { "Start": 0, "End": 1.5, "Text": "我已就位" } ],
///       "DurationSec": 1.52,
///       "Category": "Voice",
///       "SubCategory": "Voice_302",
///       "UpdatedAt": "2026-09-22T07:50:00+08:00"
///     }
///   ]
/// }
/// </code>
///
/// 读的时候键名大小写不敏感，多几个字段 / 少几个字段都不影响；缺 <c>Segments</c> 就只写正文。
///
/// 匹配规则（导入时）：
/// ① <c>FileName</c> + <c>Size</c> 同时一致 → 命中所有同名同大小的条目
///    （同一音频在 Events 与 Media 各一份，两份都会写上）；
/// ② 只给 <c>FileName</c>（<c>Size</c> 为 0）→ 命中所有同名条目；
/// ③ 都没有时退回 <c>Path</c> 精确匹配（大小写敏感，走 Path 唯一索引）。
/// 命中多条且给了 <c>Path</c> 时，路径完全一致的那条排在前面。
/// </summary>
public sealed class TranscriptFile
{
    public const string FormatId = "dfaudio-transcripts";
    public const int CurrentVersion = 1;

    public string Format { get; set; } = FormatId;
    public int Version { get; set; } = CurrentVersion;
    public string? App { get; set; }
    public string? ExportedAt { get; set; }
    public int Count { get; set; }
    public List<TranscriptEntry> Entries { get; set; } = new();

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>把一批条目写成交换文件，返回文件路径。</summary>
    public static string Write(IEnumerable<AudioItem> items, string dir, string fileName)
    {
        Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, fileName);

        var file = new TranscriptFile
        {
            ExportedAt = DateTimeOffset.Now.ToString("o"),
            App = "三角洲音频工坊 · DFAudioStudio",
        };

        foreach (var item in items)
        {
            var text = item.Transcript?.Trim() ?? "";
            if (text.Length == 0) continue;   // 没识别文本的不导出

            file.Entries.Add(new TranscriptEntry
            {
                FileName = item.FileName,
                Size = item.Size,
                Path = item.Path,
                Text = text,
                Segments = ParseSegments(item.SegmentsJson),
                DurationSec = Math.Round(item.DurationSec, 3),
                Category = item.Category.ToString(),
                SubCategory = string.IsNullOrWhiteSpace(item.SubCategory) ? null : item.SubCategory,
                UpdatedAt = item.UpdatedAt > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(item.UpdatedAt).ToLocalTime().ToString("o")
                    : null,
            });
        }

        file.Count = file.Entries.Count;
        File.WriteAllText(path, JsonSerializer.Serialize(file, WriteOptions), new UTF8Encoding(false));
        return path;
    }

    /// <summary>读交换文件。<paramref name="error"/> 非空表示读失败（文件格式不对 / 不是本格式）。</summary>
    public static TranscriptFile? Read(string path, out string error)
    {
        error = "";
        try
        {
            if (!File.Exists(path))
            {
                error = "文件不存在：" + path;
                return null;
            }

            var file = JsonSerializer.Deserialize<TranscriptFile>(File.ReadAllText(path), ReadOptions);
            if (file is null)
            {
                error = "文件内容解析为空，可能不是识别结果文件。";
                return null;
            }

            if (!string.IsNullOrWhiteSpace(file.Format) &&
                !string.Equals(file.Format, FormatId, StringComparison.OrdinalIgnoreCase))
            {
                error = $"这不是本软件的识别结果文件（format=\"{file.Format}\"）。";
                return null;
            }

            if (file.Version > CurrentVersion)
            {
                error = $"文件版本（{file.Version}）比当前程序支持的版本（{CurrentVersion}）新，请更新软件后再导入。";
                return null;
            }

            // 容错：字段名写不一样的也认（text/transcript、fileName/name/file）
            file.Entries ??= new List<TranscriptEntry>();
            file.Entries.RemoveAll(e => e is null);
            if (file.Count <= 0) file.Count = file.Entries.Count;
            return file;
        }
        catch (Exception ex)
        {
            error = "读取失败：" + ex.Message;
            return null;
        }
    }

    private static List<TranscriptSegment>? ParseSegments(string? json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            var segs = JsonSerializer.Deserialize<List<TranscriptSegment>>(json);
            return segs is { Count: > 0 } ? segs : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>导入前的"预演"结果：先算清楚会改什么，用户确认了再写。</summary>
public sealed class TranscriptImportPlan
{
    public int InFile { get; init; }             // 文件里的条目数（有文本的）
    public int Matched { get; init; }            // 能在库里找到对应音频的条目数
    public int RowsAffected { get; init; }       // 命中的库记录数（同名同大小可能命中多条）
    public int WillChange { get; init; }         // 其中内容真的会变（与库里现有文本不同）的记录数
    public int SameText { get; init; }           // 文本已经一样、跳过不写的记录数
    public int Unmatched { get; init; }          // 文件里有、库里找不到的条目数
    public int EmptyText { get; init; }          // 文件里文本为空的条目数（跳过）
    public List<string> UnmatchedSamples { get; init; } = new();
    public List<string> EmptySamples { get; init; } = new();

    public string Describe()
        => $"文件里 {InFile:N0} 条有效识别文本：可匹配 {Matched:N0} 条（影响库里 {RowsAffected:N0} 条记录，其中 {WillChange:N0} 条内容会更新、"
           + $"{SameText:N0} 条与现有内容相同会跳过）；匹配不上 {Unmatched:N0} 条"
           + (EmptyText > 0 ? $"；空文本跳过 {EmptyText:N0} 条" : "") + "。";

    public string DescribeSamples()
    {
        var parts = new List<string>();
        if (UnmatchedSamples.Count > 0)
            parts.Add("匹配不上的例子：" + string.Join("、", UnmatchedSamples));
        if (EmptySamples.Count > 0)
            parts.Add("空文本的例子：" + string.Join("、", EmptySamples));
        return parts.Count == 0 ? "" : string.Join("\n", parts);
    }
}

/// <summary>真正写库的结果。</summary>
public sealed class TranscriptImportOutcome
{
    public int Updated { get; set; }
    public int SkippedSame { get; set; }
    public int Unmatched { get; set; }
    public int EmptyText { get; set; }
    public double Seconds { get; set; }

    public string Describe()
        => $"导入完成：更新 {Updated:N0} 条、内容相同跳过 {SkippedSame:N0} 条、匹配不上 {Unmatched:N0} 条"
           + (EmptyText > 0 ? $"，空文本忽略 {EmptyText:N0} 条" : "")
           + $"（{Seconds:F1}s）。";
}

/// <summary>识别结果的导入 / 导出：解析、预演、写回。</summary>
public static class TranscriptTransfer
{
    private const int SampleLimit = 5;

    /// <summary>导出库里所有带识别文本的条目（默认最多 5 万条，与 TXT 导出一致）。</summary>
    public static string ExportAll(IndexDb db, string dir, string? fileName = null, int limit = 50000)
    {
        var items = db.Query(new ItemQuery
        {
            OnlyWithTranscript = true,
            Limit = limit,
            OrderBy = "Path",
        });

        string name = string.IsNullOrWhiteSpace(fileName)
            ? $"识别结果_{DateTime.Now:yyyyMMdd_HHmmss}.json"
            : fileName!;

        return TranscriptFile.Write(items, dir, name);
    }

    /// <summary>只读预演：算清楚"会命中多少、会改多少"，不动数据库。整批共用一个数据库连接。</summary>
    public static TranscriptImportPlan Plan(IndexDb db, TranscriptFile file)
    {
        int inFile = 0, matched = 0, rows = 0, willChange = 0, same = 0, unmatched = 0, empty = 0;
        var unmatchedSamples = new List<string>();
        var emptySamples = new List<string>();

        using var session = db.OpenImportSession();

        foreach (var entry in file.Entries)
        {
            var text = entry.Text?.Trim() ?? "";
            if (text.Length == 0)
            {
                empty++;
                if (emptySamples.Count < SampleLimit && !string.IsNullOrWhiteSpace(entry.FileName))
                    emptySamples.Add(entry.FileName);
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.FileName) && string.IsNullOrWhiteSpace(entry.Path))
            {
                unmatched++;
                if (unmatchedSamples.Count < SampleLimit) unmatchedSamples.Add("(没有文件名)");
                continue;
            }

            inFile++;

            var hits = session.Find(entry.Path, entry.FileName ?? "", entry.Size);
            if (hits.Count == 0)
            {
                unmatched++;
                if (unmatchedSamples.Count < SampleLimit)
                    unmatchedSamples.Add((string.IsNullOrWhiteSpace(entry.FileName) ? entry.Path : entry.FileName) ?? "");
                continue;
            }

            matched++;
            rows += hits.Count;
            foreach (var (_, existing) in hits)
            {
                if (string.Equals(existing?.Trim() ?? "", text, StringComparison.Ordinal)) same++;
                else willChange++;
            }
        }

        return new TranscriptImportPlan
        {
            InFile = inFile,
            Matched = matched,
            RowsAffected = rows,
            WillChange = willChange,
            SameText = same,
            Unmatched = unmatched,
            EmptyText = empty,
            UnmatchedSamples = unmatchedSamples,
            EmptySamples = emptySamples,
        };
    }

    /// <summary>真正写库。<paramref name="progress"/> 收到 (已处理条目, 总条目)。整批共用一个连接。</summary>
    public static TranscriptImportOutcome Apply(IndexDb db, TranscriptFile file, Action<int, int>? progress = null)
    {
        var outcome = new TranscriptImportOutcome();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int total = file.Entries.Count;
        int done = 0;

        using var session = db.OpenImportSession();

        foreach (var entry in file.Entries)
        {
            done++;
            if (done % 500 == 0 || done == total) progress?.Invoke(done, total);

            var text = entry.Text?.Trim() ?? "";
            if (text.Length == 0)
            {
                outcome.EmptyText++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.FileName) && string.IsNullOrWhiteSpace(entry.Path))
            {
                outcome.Unmatched++;
                continue;
            }

            var hits = session.Find(entry.Path, entry.FileName ?? "", entry.Size);
            if (hits.Count == 0)
            {
                outcome.Unmatched++;
                continue;
            }

            string segmentsJson = entry.Segments is { Count: > 0 }
                ? JsonSerializer.Serialize(entry.Segments)
                : "";

            foreach (var (id, existing) in hits)
            {
                if (string.Equals(existing?.Trim() ?? "", text, StringComparison.Ordinal))
                {
                    outcome.SkippedSame++;
                    continue;
                }

                if (session.Update(id, text, segmentsJson))
                    outcome.Updated++;
                else
                    outcome.SkippedSame++;
            }
        }

        clock.Stop();
        outcome.Seconds = clock.Elapsed.TotalSeconds;
        return outcome;
    }
}
