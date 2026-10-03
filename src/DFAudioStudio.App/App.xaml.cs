using System;
using DFAudioStudio.App.Services;
using DFAudioStudio.Core.Data;
using DFAudioStudio.Core.Models;
using DFAudioStudio.Core.Services;
using Microsoft.UI.Xaml;

namespace DFAudioStudio.App;

/// <summary>
/// 应用入口：创建并激活主窗口。
/// 启动路径刻意保持轻量——只打开索引库、读几个毫秒级的统计数字，**不加载 Whisper 模型、不启动识别队列、不做全量扫描**；
/// 分区导航树由主窗口在窗口显示之后异步生成。
/// </summary>
public partial class App : Application
{
    /// <summary>当前主窗口（文件 / 文件夹选择器需要用它取 HWND 做 InitializeWithWindow）。</summary>
    public static Window? MainWindowInstance { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var cmdArgs = Environment.GetCommandLineArgs();

        // 识别结果导入 / 导出自检（不进入界面，走的是和界面按钮完全同一条路径）：
        //   DFAudioStudio.App.exe --exporttranscripts <输出.json|目录> [报告文件]
        //   DFAudioStudio.App.exe --importtranscripts <输入.json> [报告文件] [--dryrun]
        int exportIdx = Array.IndexOf(cmdArgs, "--exporttranscripts");
        if (exportIdx >= 0)
        {
            string outArg = exportIdx + 1 < cmdArgs.Length && !cmdArgs[exportIdx + 1].StartsWith("--")
                ? cmdArgs[exportIdx + 1]
                : Path.Combine(Path.GetTempPath(), "dfaudio_transcripts.json");
            string report = exportIdx + 2 < cmdArgs.Length && !cmdArgs[exportIdx + 2].StartsWith("--")
                ? cmdArgs[exportIdx + 2]
                : Path.Combine(Path.GetTempPath(), "dfaudio_export_report.txt");

            string text;
            try { text = RunExportTranscripts(outArg); }
            catch (Exception ex) { text = "导出失败：" + ex; }
            try { File.WriteAllText(report, text); } catch { /* 报告写不出来也不影响退出 */ }
            Environment.Exit(text.StartsWith("导出失败") ? 2 : 0);
            return;
        }

        int importIdx = Array.IndexOf(cmdArgs, "--importtranscripts");
        if (importIdx >= 0 && importIdx + 1 < cmdArgs.Length)
        {
            string inPath = cmdArgs[importIdx + 1];
            string report = importIdx + 2 < cmdArgs.Length && !cmdArgs[importIdx + 2].StartsWith("--")
                ? cmdArgs[importIdx + 2]
                : Path.Combine(Path.GetTempPath(), "dfaudio_import_report.txt");
            bool dryRun = Array.IndexOf(cmdArgs, "--dryrun") >= 0;

            string text;
            try { text = RunImportTranscripts(inPath, dryRun); }
            catch (Exception ex) { text = "导入失败：" + ex; }
            try { File.WriteAllText(report, text); } catch { /* 报告写不出来也不影响退出 */ }
            Environment.Exit(text.StartsWith("导入失败") ? 2 : 0);
            return;
        }

        // 音频输出通路自检（不进入界面，用来定位"进度不走 / 没声音"）：
        //   DFAudioStudio.App.exe --audiotest [报告文件] [--file 音频] [--device 序号] [--seconds 秒数]
        int audioTest = Array.IndexOf(cmdArgs, "--audiotest");
        if (audioTest >= 0)
        {
            string audioReport = audioTest + 1 < cmdArgs.Length && !cmdArgs[audioTest + 1].StartsWith("--")
                ? cmdArgs[audioTest + 1]
                : Path.Combine(Path.GetTempPath(), "dfaudio_audiotest.txt");

            string? probeFile = null;
            int fileIndex = Array.IndexOf(cmdArgs, "--file");
            if (fileIndex >= 0 && fileIndex + 1 < cmdArgs.Length) probeFile = cmdArgs[fileIndex + 1];

            int? probeDevice = null;
            int devIndex = Array.IndexOf(cmdArgs, "--device");
            if (devIndex >= 0 && devIndex + 1 < cmdArgs.Length && int.TryParse(cmdArgs[devIndex + 1], out var dv))
                probeDevice = dv;

            int probeSeconds = 4;
            int secIndex = Array.IndexOf(cmdArgs, "--seconds");
            if (secIndex >= 0 && secIndex + 1 < cmdArgs.Length && int.TryParse(cmdArgs[secIndex + 1], out var sc))
                probeSeconds = Math.Clamp(sc, 1, 60);

            double? probeTempo = null;
            int tempoIndex = Array.IndexOf(cmdArgs, "--tempo");
            if (tempoIndex >= 0 && tempoIndex + 1 < cmdArgs.Length &&
                double.TryParse(cmdArgs[tempoIndex + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tp))
                probeTempo = tp;

            double? probePitch = null;
            int pitchIndex = Array.IndexOf(cmdArgs, "--pitch");
            if (pitchIndex >= 0 && pitchIndex + 1 < cmdArgs.Length &&
                double.TryParse(cmdArgs[pitchIndex + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pt))
                probePitch = pt;

            // 播放中途改速度（验证"拖滑块时实时生效"这条路径）
            double? probeSwitchTempo = null;
            int swTempoIndex = Array.IndexOf(cmdArgs, "--switch-tempo");
            if (swTempoIndex >= 0 && swTempoIndex + 1 < cmdArgs.Length &&
                double.TryParse(cmdArgs[swTempoIndex + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var swt))
                probeSwitchTempo = swt;

            int probeSwitchAt = 2;
            int swAtIndex = Array.IndexOf(cmdArgs, "--switch-at");
            if (swAtIndex >= 0 && swAtIndex + 1 < cmdArgs.Length && int.TryParse(cmdArgs[swAtIndex + 1], out var swa))
                probeSwitchAt = Math.Clamp(swa, 0, 60);

            string probeText;
            try
            {
                probeText = AudioOutputProbe.Run(probeFile, probeDevice, probeSeconds, probeTempo, probePitch, probeSwitchTempo, probeSwitchAt);
            }
            catch (Exception ex)
            {
                probeText = "音频输出自检失败：" + ex;
            }
            try { File.WriteAllText(audioReport, probeText); } catch { /* 报告写不出来也不影响退出 */ }
            Environment.Exit(0);
            return;
        }

        // 频谱自检（正式版与 Alpha 版都可以用，不进入界面）：
        //   DFAudioStudio.App.exe --spectrumtest <音频路径> [报告文件]
        int spectrumTest = Array.IndexOf(cmdArgs, "--spectrumtest");
        if (spectrumTest >= 0 && spectrumTest + 1 < cmdArgs.Length)
        {
            string wavPath = cmdArgs[spectrumTest + 1];
            string reportPath = spectrumTest + 2 < cmdArgs.Length && !cmdArgs[spectrumTest + 2].StartsWith("--")
                ? cmdArgs[spectrumTest + 2]
                : Path.Combine(Path.GetTempPath(), "dfaudio_spectrum.txt");
            string text;
            try
            {
                text = SpectrumService.Current.SelfTest(wavPath);
            }
            catch (Exception ex)
            {
                text = "频谱自检失败：" + ex;
            }
            try { File.WriteAllText(reportPath, text); } catch { /* 报告写不出来也不影响退出 */ }
            Environment.Exit(0);
            return;
        }

        // DSP 自检（验证「变速保持音调 / 变调保持速度」）：
        //   DFAudioStudio.App.exe --dsptest <音频路径> [报告文件]
        int dspTest = Array.IndexOf(cmdArgs, "--dsptest");
        if (dspTest >= 0 && dspTest + 1 < cmdArgs.Length)
        {
            string audioPath = cmdArgs[dspTest + 1];
            string dspReport = dspTest + 2 < cmdArgs.Length && !cmdArgs[dspTest + 2].StartsWith("--")
                ? cmdArgs[dspTest + 2]
                : Path.Combine(Path.GetTempPath(), "dfaudio_dsp.txt");
            string dspText;
            try
            {
                dspText = SpectrumService.DspSelfTest(audioPath);
            }
            catch (Exception ex)
            {
                dspText = "DSP 自检失败：" + ex;
            }
            try { File.WriteAllText(dspReport, dspText); } catch { /* 报告写不出来也不影响退出 */ }
            Environment.Exit(0);
            return;
        }

#if ALPHA_EXPIRY
        // ── Alpha 定时过期版 · 命令行诊断 ──────────────────────────────────────
        // DFAudioStudio.App.exe --timecheck [报告文件] [--at ISO时间] [--drift 秒数]
        // 输出 NTP 校时结果后退出，退出码 0=通过 3=未通过（--at/--drift 只复算结论，不改变实际校验行为）。
        int timecheck = Array.IndexOf(cmdArgs, "--timecheck");
        if (timecheck >= 0)
        {
            string? reportPath = timecheck + 1 < cmdArgs.Length && !cmdArgs[timecheck + 1].StartsWith("--")
                ? cmdArgs[timecheck + 1]
                : null;

            DateTimeOffset? simulateAt = null;
            int atIndex = Array.IndexOf(cmdArgs, "--at");
            if (atIndex >= 0 && atIndex + 1 < cmdArgs.Length &&
                DateTimeOffset.TryParse(cmdArgs[atIndex + 1], out var parsedAt))
                simulateAt = parsedAt;

            TimeSpan? simulateDrift = null;
            int driftIndex = Array.IndexOf(cmdArgs, "--drift");
            if (driftIndex >= 0 && driftIndex + 1 < cmdArgs.Length &&
                double.TryParse(cmdArgs[driftIndex + 1], out var driftSeconds))
                simulateDrift = TimeSpan.FromSeconds(driftSeconds);

            // 丢到线程池再同步等待：UI 线程上直接等异步校时会死锁
            int code = Task.Run(() => TimeGuard.RunDiagnosticAsync(reportPath, simulateAt, simulateDrift))
                           .GetAwaiter().GetResult();
            Environment.Exit(code);
            return;
        }
#endif

        var services = AppServices.Current;
#if ALPHA_EXPIRY
        // 校时日志接进应用自身日志（<数据根>\logs\app-yyyyMMdd.log），方便排查
        TimeGuard.LogSink ??= message =>
        {
            try { AppServices.Current.LogInfo(message); } catch { /* 日志失败不影响校验 */ }
        };
#endif
        services.LogInfo("三角洲音频工坊 · DFAudioStudio 启动");
        services.LogInfo($"索引库：{services.Db.DbPath}");

        var window = new MainWindow();
        MainWindowInstance = window;
        window.Activate();

        // 识别改成手动：这个方法默认什么都不做（只写一条日志），只有设置里显式勾选了自动识别才会启动队列，
        // 而且启动路径上不会创建 WhisperTranscriber（模型加载只发生在队列后台线程里）。
        services.TryAutoStartTranscription();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        AppServices.Current.LogWarn("未处理异常：" + e.Message);
        e.Handled = true;
    }

    // ── 识别结果导入 / 导出自检（和界面按钮同一条路径，只是不进界面） ────────

    /// <summary>导出：列出现有的识别文本条数、写成交换文件、再把文件读回来核对一遍。</summary>
    private static string RunExportTranscripts(string outArg)
    {
        var services = AppServices.Current;
        var lines = new List<string>();

        var items = services.Db.Query(new ItemQuery
        {
            OnlyWithTranscript = true,
            Limit = 50000,
            OrderBy = "Path",
        });

        string dir, name;
        if (Directory.Exists(outArg) || string.IsNullOrEmpty(Path.GetExtension(outArg)))
        {
            dir = outArg;
            name = $"识别结果_{DateTime.Now:yyyyMMdd_HHmmss}.json";
        }
        else
        {
            dir = Path.GetDirectoryName(outArg) ?? ".";
            name = Path.GetFileName(outArg);
        }

        lines.Add($"库里有识别文本的条目：{items.Count:N0}");
        string path = TranscriptTransfer.ExportAll(services.Db, dir, name);
        lines.Add("已导出：" + path);
        lines.Add("文件大小：" + new FileInfo(path).Length.ToString("N0") + " 字节");

        var back = TranscriptFile.Read(path, out string error);
        lines.Add("回读校验：" + (back is null ? "失败 — " + error : $"OK，format={back.Format} version={back.Version} count={back.Count}"));
        if (back is not null && back.Entries.Count > 0)
        {
            var first = back.Entries[0];
            lines.Add($"第一条：fileName='{first.FileName}' size={first.Size} text='{Truncate(first.Text, 60)}'");
        }

        try
        {
            string samplePath = Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + ".预览.txt");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("识别结果交换文件预览（前 10 条）");
            foreach (var e in (back?.Entries ?? new List<TranscriptEntry>()).Take(10))
                sb.AppendLine($"  {e.FileName} · {e.Size:N0} 字节 · {Truncate(e.Text, 40)}");
            File.WriteAllText(samplePath, sb.ToString());
            lines.Add("预览：" + samplePath);
        }
        catch { /* 预览写不出来不算失败 */ }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    /// <summary>导入：预演 → （非 dryrun 时）写库 → 输出计数。</summary>
    private static string RunImportTranscripts(string inPath, bool dryRun)
    {
        var services = AppServices.Current;
        var lines = new List<string>();

        lines.Add("输入文件：" + inPath);
        var file = TranscriptFile.Read(inPath, out string error);
        if (file is null)
        {
            lines.Add("读取失败：" + error);
            return "导入失败：" + error + Environment.NewLine + string.Join(Environment.NewLine, lines);
        }

        lines.Add($"文件格式：{file.Format} v{file.Version}，条目 {file.Entries.Count:N0}（文件自称 {file.Count:N0}）");

        var plan = TranscriptTransfer.Plan(services.Db, file);
        lines.Add("预演：" + plan.Describe());
        var samples = plan.DescribeSamples();
        if (samples.Length > 0) lines.Add(samples);

        if (dryRun)
        {
            lines.Add("（--dryrun：只看预演，没有写库）");
            return string.Join(Environment.NewLine, lines) + Environment.NewLine;
        }

        var outcome = TranscriptTransfer.Apply(services.Db, file, (done, total) =>
            lines.Add($"  进度 {done:N0}/{total:N0}"));
        lines.Add("结果：" + outcome.Describe());

        var done = services.Db.CountsByStatus().GetValueOrDefault(TranscriptStatus.Done);
        lines.Add($"导入后「已完成」总数：{done:N0}");

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static string Truncate(string? text, int max)
    {
        text ??= "";
        return text.Length <= max ? text : text[..max] + "…";
    }
}
