using System.Diagnostics;
using DFAudioStudio.Core.Indexing;
using DFAudioStudio.Core.Models;

// 分类器实跑验证：不写数据库，只统计分区与标签，确认 11 万文件的分类结果符合预期。
string media = @"I:\新建文件夹 (19)\Exports\DeltaForce\Content\WwiseAudio\Media";
string localized = @"I:\新建文件夹 (19)\Exports\DeltaForce\Content\WwiseAudio\Localized";
if (args.Length >= 1) media = args[0];
if (args.Length >= 2) localized = args[1];
int limit = args.Length >= 3 && int.TryParse(args[2], out var l) ? l : int.MaxValue;

var roots = new (string Root, string Path)[] { ("Media", media), ("Localized", localized) };
var byCat = new Dictionary<AudioCategory, int>();
var bySub = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
var byEvent = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
var byMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
var byOp = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
var voiceBySub = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
var musicBySub = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
var samples = new List<string>();
int total = 0;
var sw = Stopwatch.StartNew();

foreach (var (rootName, rootPath) in roots)
{
    if (!Directory.Exists(rootPath)) { Console.WriteLine($"[跳过] {rootName} 不存在: {rootPath}"); continue; }
    foreach (var f in Directory.EnumerateFiles(rootPath, "*.wav", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
    {
        if (total >= limit) break;
        total++;
        string rel = System.IO.Path.GetRelativePath(rootPath, f);
        string name = System.IO.Path.GetFileName(f);
        var (cat, sub) = Classifier.CategoryOf(name, rel);
        byCat[cat] = byCat.GetValueOrDefault(cat) + 1;
        bySub[cat + "/" + sub] = bySub.GetValueOrDefault(cat + "/" + sub) + 1;
        if (cat == AudioCategory.Voice) voiceBySub[sub] = voiceBySub.GetValueOrDefault(sub) + 1;
        if (cat == AudioCategory.Music) musicBySub[sub] = musicBySub.GetValueOrDefault(sub) + 1;

        string ev = Classifier.EventOf(name);
        if (ev.Length > 0) byEvent[ev] = byEvent.GetValueOrDefault(ev) + 1;
        string map = Classifier.MapOf(name, rel);
        if (map.Length > 0) byMap[map] = byMap.GetValueOrDefault(map) + 1;
        string op = Classifier.OperatorOf(name);
        if (op.Length > 0) byOp[op] = byOp.GetValueOrDefault(op) + 1;

        if (samples.Count < 12 && (ev.Length > 0 || map.Length > 0 || op.Length > 0))
            samples.Add($"{cat,-8} op={op,-4} ev={ev,-8} map={map,-12} sub={sub,-28} {name}");
    }
}

Console.WriteLine($"扫描文件数: {total:N0}   用时 {sw.Elapsed.TotalSeconds:F1}s");
Console.WriteLine("\n=== 分区统计 ===");
foreach (var kv in byCat.OrderByDescending(k => k.Value))
    Console.WriteLine($"  {kv.Key,-10} {kv.Value,8:N0}");

void Top(string title, Dictionary<string, int> d, int n)
{
    Console.WriteLine($"\n=== {title} (Top{n}) ===");
    foreach (var kv in d.OrderByDescending(k => k.Value).Take(n))
        Console.WriteLine($"  {kv.Value,7:N0}  {kv.Key}");
}

Top("事件标签", byEvent, 25);
Top("地图标签", byMap, 25);
Top("干员编号", byOp, 25);
Top("语音子分类", voiceBySub, 20);
Top("音乐子分类", musicBySub, 20);
Top("全部子分类", bySub, 30);

Console.WriteLine("\n=== 标签样本 ===");
foreach (var s in samples) Console.WriteLine("  " + s);

// Other 类专项分析：找出命名规律，用于完善分类器
var otherSub = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
var otherPrefix = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
int digitOnly = 0;
foreach (var (rootName, rootPath) in roots)
{
    if (!Directory.Exists(rootPath)) continue;
    foreach (var f in Directory.EnumerateFiles(rootPath, "*.wav", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
    {
        string name = System.IO.Path.GetFileName(f);
        string rel = System.IO.Path.GetRelativePath(rootPath, f);
        var (cat, sub) = Classifier.CategoryOf(name, rel);
        if (cat != AudioCategory.Other) continue;
        string stem = System.IO.Path.GetFileNameWithoutExtension(name);
        if (stem.All(char.IsDigit)) digitOnly++;
        string pref = stem.Contains('_') ? stem[..stem.IndexOf('_')] : stem;
        otherPrefix[pref] = otherPrefix.GetValueOrDefault(pref) + 1;
        otherSub[sub] = otherSub.GetValueOrDefault(sub) + 1;
    }
}
Console.WriteLine($"\n=== Other 类专项 ===\n  纯数字文件名: {digitOnly:N0}");
Top("Other 前缀", otherPrefix, 40);
Top("Other 子分类", otherSub, 40);
