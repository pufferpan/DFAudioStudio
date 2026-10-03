using DFAudioStudio.Core.Data;
using DFAudioStudio.Core.Indexing;
using DFAudioStudio.Core.Models;

namespace DFAudioStudio.Core.Services;

public sealed record ScanProgress(int Scanned, int Changed, int TotalHint, string CurrentFile, AudioCategory CurrentCategory);

/// <summary>全量扫描：把 Media / Localized 两个根目录下所有音频分类入库（增量，已存在且未改动则跳过）。</summary>
public sealed class Scanner
{
    private readonly IndexDb _db;

    public Scanner(IndexDb db) => _db = db;

    public async Task<ScanStats> ScanAsync(IEnumerable<(string Root, string Path)> roots,
                                           IProgress<ScanProgress>? progress,
                                           CancellationToken ct,
                                           Action<string>? log = null)
    {
        var stats = new ScanStats();
        var batch = new List<AudioItem>(8000);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // 一次载入已有索引状态，之后全部在内存比较（11 万文件若逐条查库会慢到不可接受）
        var known = _db.LoadFileStates();
        log?.Invoke($"[增量] 库内已有 {known.Count:N0} 条记录");

        foreach (var (rootName, rootPath) in roots)
        {
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
            {
                log?.Invoke($"[跳过] 目录不存在: {rootName} -> {rootPath}");
                continue;
            }

            log?.Invoke($"[扫描] {rootName}: {rootPath}");
            var enumOpts = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            foreach (var file in Directory.EnumerateFiles(rootPath, "*.wav", enumOpts))
            {
                ct.ThrowIfCancellationRequested();
                stats.TotalFiles++;

                FileInfo fi;
                try { fi = new FileInfo(file); }
                catch { stats.Failed++; continue; }

                long mtime = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();
                if (known.TryGetValue(file, out var prev) && prev.Size == fi.Length && prev.Mtime == mtime && prev.SceneTag.Length > 0)
                {
                    stats.Skipped++;
                    if (stats.TotalFiles % 2000 == 0)
                        progress?.Report(new ScanProgress(stats.TotalFiles, stats.NewOrUpdated, 0, file, AudioCategory.Other));
                    continue;
                }

                var item = new AudioItem
                {
                    Path = file,
                    Root = rootName,
                    RelPath = System.IO.Path.GetRelativePath(rootPath, file),
                    FileName = fi.Name,
                    Size = fi.Length,
                    Mtime = mtime
                };

                // 统一走分类器，避免新增标签维度时漏字段（曾经漏过 SceneTag）
                Classifier.Apply(item);
                var cat = item.Category;

                if (cat is AudioCategory.Voice or AudioCategory.Music)
                {
                    var info = WavProbe.Probe(file);
                    item.DurationSec = info.DurationSec;
                    item.SampleRate = info.SampleRate;
                    item.Channels = info.Channels;
                }

                batch.Add(item);
                stats.NewOrUpdated++;
                stats.ByCategory[cat] = stats.ByCategory.GetValueOrDefault(cat) + 1;

                if (batch.Count >= 4000)
                {
                    _db.UpsertBatch(batch, now);
                    batch.Clear();
                }

                if (stats.TotalFiles % 200 == 0)
                    progress?.Report(new ScanProgress(stats.TotalFiles, stats.NewOrUpdated, 0, file, cat));

                await Task.Yield();
            }
        }

        if (batch.Count > 0) _db.UpsertBatch(batch, now);
        _db.SetKv("LastScanUtc", DateTimeOffset.UtcNow.ToString("O"));
        log?.Invoke($"[完成] 扫描 {stats.TotalFiles}，更新 {stats.NewOrUpdated}，跳过(未变化) {stats.Skipped}，失败 {stats.Failed}");
        progress?.Report(new ScanProgress(stats.TotalFiles, stats.NewOrUpdated, 0, "", AudioCategory.Other));
        return stats;
    }
}
