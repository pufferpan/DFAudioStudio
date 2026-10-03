using DFAudioStudio.Core.Data;
using DFAudioStudio.Core.Models;
using DFAudioStudio.Core.Services;

// 命令行工具（与 WinUI 应用共用 Core）：用于验证分区入库与离线识别整条链路。
// 用法:
//   dfaudio index                      全量扫描入库
//   dfaudio stats                      打印分区/标签/状态统计
//   dfaudio query <关键词> [数量]       搜索文件名或识别文本
//   dfaudio transcribe [数量] [--music] 识别待处理条目（默认只语音）
//   dfaudio export                     导出 CSV 到设置里的导出目录

string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
var settings = AppSettings.Load();
if (args.Contains("--media")) settings.MediaRoot = args[Array.IndexOf(args, "--media") + 1];
if (args.Contains("--localized")) settings.LocalizedRoot = args[Array.IndexOf(args, "--localized") + 1];

Console.WriteLine($"[配置] Media={settings.MediaRoot}");
Console.WriteLine($"[配置] Localized={settings.LocalizedRoot}");
Console.WriteLine($"[配置] 数据库={settings.DbPath}");
Console.WriteLine($"[配置] 模型={(string.IsNullOrEmpty(settings.ModelPath) ? "(未找到)" : settings.ModelPath)}");

using var db = new IndexDb(settings.DbPath);

switch (cmd)
{
    case "index":
    {
        var scanner = new Scanner(db);
        var lastPrint = DateTime.UtcNow;
        var progress = new Progress<ScanProgress>(p =>
        {
            if ((DateTime.UtcNow - lastPrint).TotalSeconds >= 2)
            {
                lastPrint = DateTime.UtcNow;
                Console.WriteLine($"  …已扫描 {p.Scanned:N0}，更新 {p.Changed:N0}  {System.IO.Path.GetFileName(p.CurrentFile)}");
            }
        });
        var roots = new[] { ("Media", settings.MediaRoot), ("Localized", settings.LocalizedRoot) };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var stats = await scanner.ScanAsync(roots, progress, CancellationToken.None, Console.WriteLine);
        sw.Stop();
        Console.WriteLine($"[完成] 用时 {sw.Elapsed.TotalSeconds:F1}s  库内总数 {db.TotalCount():N0}");
        break;
    }

    case "stats":
    {
        Console.WriteLine($"\n库内总数: {db.TotalCount():N0}");
        Console.WriteLine("=== 分区 ===");
        foreach (var kv in db.CountsByCategory().OrderByDescending(k => k.Value))
            Console.WriteLine($"  {kv.Key,-10} {kv.Value,8:N0}");
        Console.WriteLine("=== 识别状态(语音+音乐) ===");
        foreach (var kv in db.CountsByStatus().OrderBy(k => k.Key))
            Console.WriteLine($"  {kv.Key,-10} {kv.Value,8:N0}");
        Console.WriteLine("=== 干员 Top15 ===");
        foreach (var s in db.Distinct("Operator").Take(15)) Console.WriteLine("  " + s);
        Console.WriteLine("=== 事件标签 ===");
        foreach (var s in db.Distinct("EventTag")) Console.WriteLine("  " + s);
        Console.WriteLine("=== 地图标签 ===");
        foreach (var s in db.Distinct("MapTag")) Console.WriteLine("  " + s);
        Console.WriteLine("=== 场景/用途分区（新）===");
        foreach (var (v, n) in db.DistinctWithCounts("SceneTag", 30)) Console.WriteLine($"  {n,7:N0}  {v}");
        Console.WriteLine("=== 子分类 Top15 ===");
        foreach (var (v, n) in db.DistinctWithCounts("SubCategory", 15)) Console.WriteLine($"  {n,7:N0}  {v}");
        break;
    }

    case "query":
    {
        string kw = args.Length > 1 ? args[1] : "";
        int take = args.Length > 2 && int.TryParse(args[2], out var t) ? t : 20;
        var items = db.Query(new ItemQuery { Search = kw, Limit = take });
        Console.WriteLine($"命中 {db.Count(new ItemQuery { Search = kw }):N0} 条，显示 {items.Count} 条：");
        foreach (var i in items)
            Console.WriteLine($"  [{i.Category}] {i.FileName}  ({i.DurationText})  {i.Transcript}");
        break;
    }

    case "transcribe":
    {
        int count = args.Length > 1 && int.TryParse(args[1], out var n) ? n : 5;
        bool music = args.Contains("--music");
        if (string.IsNullOrEmpty(settings.ModelPath) || !File.Exists(settings.ModelPath))
        {
            Console.WriteLine("未找到模型文件，请把 ggml-*.bin 放到 models\\ 目录或设置 ModelPath。");
            break;
        }
        using var transcriber = new WhisperTranscriber(settings.ModelPath, 1);
        Console.WriteLine($"模型: {transcriber.ModelName}  并发: 1  语言: {settings.Language}  线程: {transcriber.Threads}");
        int skipped = db.SkipTooShort(settings.MinDurationSec);
        if (skipped > 0) Console.WriteLine($"已跳过过短碎片: {skipped:N0} 条 (< {settings.MinDurationSec}s)");
        int skippedKeys = db.SkipByKeywords((settings.SkipKeywords ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries));
        if (skippedKeys > 0) Console.WriteLine($"已跳过非语音素材: {skippedKeys:N0} 条");
        var pending = db.NextPending(music, count, settings.MinDurationSec);
        Console.WriteLine($"待识别: {db.PendingCount(music, settings.MinDurationSec):N0} 条，本次处理 {pending.Count} 条\n");
        foreach (var (id, path) in pending)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var r = await transcriber.TranscribeAsync(path, settings.Language, CancellationToken.None);
                sw.Stop();
                db.SaveTranscript(id, r);
                Console.WriteLine($"[{sw.Elapsed.TotalSeconds:F1}s] {System.IO.Path.GetFileName(path)}");
                Console.WriteLine($"      {r.Text}");
            }
            catch (Exception ex)
            {
                db.UpdateStatus(id, TranscriptStatus.Failed, ex.Message);
                Console.WriteLine($"[失败] {System.IO.Path.GetFileName(path)} -> {ex.Message}");
            }
        }
        Console.WriteLine($"\n剩余待识别: {db.PendingCount(music, settings.MinDurationSec):N0}");
        break;
    }

    case "one":
    {
        // 单文件诊断：打印解码信息（采样率/声道/时长）与识别文本
        string path = args.Length > 1 ? args[1] : "";
        if (!File.Exists(path)) { Console.WriteLine("文件不存在: " + path); break; }
        if (string.IsNullOrEmpty(settings.ModelPath) || !File.Exists(settings.ModelPath))
        {
            Console.WriteLine("未找到模型文件。"); break;
        }
        var samples = DFAudioStudio.Core.Indexing.AudioDecoder.DecodeMono16k(path, out var di);
        Console.WriteLine($"[解码] {System.IO.Path.GetFileName(path)}");
        Console.WriteLine($"       源: {di.SourceSampleRate}Hz {di.SourceChannels}ch {di.BitsPerSample}bit {di.Format} 时长 {di.SourceSeconds:F2}s 截断={di.Truncated}");
        Console.WriteLine($"       解码后: {samples.Length} 采样 = {samples.Length / 16000.0:F2}s @16kHz 单声道");
        if (samples.Length > 0)
        {
            double rms = Math.Sqrt(samples.Select(x => (double)x * x).Average());
            Console.WriteLine($"       RMS 音量: {rms:F4}  峰值: {samples.Max(Math.Abs):F4}");
        }
        if (samples.Length < 1600) { Console.WriteLine("       音频过短(<0.1s)，跳过识别"); break; }
        using var tr = new WhisperTranscriber(settings.ModelPath, 1);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await tr.TranscribeAsync(path, settings.Language, CancellationToken.None);
        sw.Stop();
        Console.WriteLine($"[识别] 用时 {sw.Elapsed.TotalSeconds:F1}s  片段 {r.Segments.Count} 个");
        Console.WriteLine("       " + r.Text);
        break;
    }

    case "reset-skipped":
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db.DbPath}");
        conn.Open();
        using var resetCmd = conn.CreateCommand();
        resetCmd.CommandText = "UPDATE items SET Status=0, LastError='' WHERE Status=5;";
        int n = resetCmd.ExecuteNonQuery();
        Console.WriteLine($"已把 {n:N0} 条「跳过」的条目重新加入待识别队列（换更好的模型后可用）。");
        break;
    }

    case "export":
    {
        var items = db.Query(new ItemQuery { Limit = 1000000, OnlyWithTranscript = false });
        var csv = ExportService.ExportCsv(items, settings.ExportDir, "音频索引_全部.csv");
        var voice = db.Query(new ItemQuery { Category = AudioCategory.Voice, OnlyWithTranscript = true, Limit = 1000000 });
        var txt = ExportService.ExportTxtBundle(voice, settings.ExportDir, "语音识别结果_语音分区.txt");
        Console.WriteLine("已导出:\n  " + csv + "\n  " + txt);
        break;
    }

    default:
        Console.WriteLine("""
            DFAudioStudio 命令行工具
              dfaudio index                       全量扫描 Media+Localized 并按分区入库
              dfaudio stats                        查看分区/标签/识别状态统计
              dfaudio query <关键词> [数量]         搜索（文件名/识别文本/干员/事件/地图/皮肤标签）
              dfaudio transcribe [数量] [--music]   离线 Whisper 识别（自动跳过非语音素材）
              dfaudio one <音频路径>               单文件诊断：解码信息 + 识别文本
              dfaudio reset-skipped                把「已跳过」的条目重新排队
              dfaudio export                       导出 CSV / 文本合集
            可选参数: --media <路径> --localized <路径>
            """);
        break;
}
