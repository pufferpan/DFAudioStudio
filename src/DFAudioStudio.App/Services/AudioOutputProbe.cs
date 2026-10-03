using System.Diagnostics;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DFAudioStudio.App.Services;

/// <summary>
/// 音频输出通路自检（不依赖界面）：
///   1. 枚举所有 WASAPI 输出设备（看默认设备是不是虚拟声卡）；
///   2. 正弦波直通 WasapiOut —— 判断「声卡到底有没有在拉数据」；
///   3. 同一个音频文件走 DSP 链（SoundTouch 变速/变调）→ WasapiOut。
/// 用来区分三种可能：设备不时钟 / DSP 链吐不出数据 / 界面层没接上。
/// </summary>
internal static class AudioOutputProbe
{
    public static string Run(string? audioPath, int? deviceIndex, int seconds, double? tempo = null, double? semitones = null,
        double? switchTempo = null, int switchAtSeconds = 0)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== 输出设备枚举 ===");
        sb.Append(DescribeDevices());

        sb.AppendLine();
        sb.AppendLine($"=== 第 1 段：正弦波直通（默认设备，{seconds} 秒）===");
        sb.Append(RunTone(null, seconds));

        if (deviceIndex is int idx)
        {
            sb.AppendLine();
            sb.AppendLine($"=== 第 2 段：正弦波直通（设备 #{idx}，{seconds} 秒）===");
            sb.Append(RunTone(idx, seconds));
        }

        if (!string.IsNullOrWhiteSpace(audioPath) && File.Exists(audioPath))
        {
            sb.AppendLine();
            sb.AppendLine("=== 第 2 段：解码源 / DSP 链裸拉（不经过声卡）===");
            sb.Append(RunPipelineProbe(audioPath!));

            sb.AppendLine();
            sb.AppendLine($"=== 第 3 段：DSP 链播放（{(deviceIndex is int i2 ? "设备 #" + i2 : "默认设备")}，{seconds} 秒，速度 {tempo?.ToString("F2") ?? "1.00"}×，音调 {(semitones is double sem ? sem.ToString("+0;-0;0") : "0")} 半音）===");
            sb.Append(RunDsp(audioPath!, deviceIndex, seconds, tempo, semitones, switchTempo, switchAtSeconds));
        }

        return sb.ToString();
    }

    /// <summary>
    /// 把「解码源直读」与「DSP 链拉取」分开测：
    /// 前者验证 AudioFileReader 能不能持续出数据，后者验证 SoundTouch 包装能不能产出。
    /// </summary>
    private static string RunPipelineProbe(string audioPath)
    {
        var sb = new StringBuilder();

        // ① 解码源直读：不经过 DSP，看音频文件本身能不能被连续读出
        try
        {
            using var reader = new AudioFileReader(audioPath);
            var buf = new float[9600];
            long frames = 0;
            int calls = 0;
            var first = new List<string>();
            int read;
            while (calls < 500_000 && (read = reader.Read(buf, 0, buf.Length)) > 0)
            {
                frames += read / reader.WaveFormat.Channels;
                if (calls < 6) first.Add(read.ToString());
                calls++;
            }
            sb.AppendLine($"源直读：格式={reader.WaveFormat} 共 {calls} 次读出 {frames} 帧 = {(double)frames / reader.WaveFormat.SampleRate:F2}s（文件标称 {reader.TotalTime.TotalSeconds:F2}s）");
            sb.AppendLine($"        前几次返回采样数：{string.Join(", ", first)}");
        }
        catch (Exception ex)
        {
            sb.AppendLine("源直读失败：" + ex);
        }

        // ② DSP 链裸拉：和实时播放同一条链（立体声、同样的小块读），只把声卡换成死循环
        try
        {
            using var reader = new AudioFileReader(audioPath);
            var provider = new SoundTouchSampleProvider(reader.ToSampleProvider());
            var buf = new float[9600];
            long outFrames = 0;
            int calls = 0;
            int zeros = 0;
            var first = new List<string>();
            while (calls < 500_000)
            {
                int read = provider.Read(buf, 0, buf.Length);
                if (calls < 6) first.Add(read.ToString());
                if (read <= 0)
                {
                    zeros++;
                    if (zeros > 3) break;
                }
                else
                {
                    outFrames += read / provider.WaveFormat.Channels;
                }
                calls++;
            }
            sb.AppendLine($"DSP 链：格式={provider.WaveFormat} 共 {calls} 次，产出 {outFrames} 帧 = {(double)outFrames / provider.WaveFormat.SampleRate:F2}s（应接近 {reader.TotalTime.TotalSeconds:F2}s）");
            sb.AppendLine($"        前几次返回采样数：{string.Join(", ", first)}；累计输出={provider.TotalSamplesRead} 累计读入={provider.TotalSamplesPulled}");
        }
        catch (Exception ex)
        {
            sb.AppendLine("DSP 链裸拉失败：" + ex);
        }

        return sb.ToString();
    }

    /// <summary>列出所有激活的输出设备，并标出系统默认设备。</summary>
    public static string DescribeDevices()
    {
        var sb = new StringBuilder();
        try
        {
            using var en = new MMDeviceEnumerator();
            string defaultId = "";
            try { defaultId = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; }
            catch (Exception ex) { sb.AppendLine("取默认输出设备失败：" + ex.Message); }

            int i = 0;
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                string mark = string.Equals(d.ID, defaultId, StringComparison.OrdinalIgnoreCase) ? "  ← 系统默认" : "";
                sb.AppendLine($"[{i}] {d.FriendlyName}{mark}");
                i++;
            }
            if (i == 0) sb.AppendLine("(没有找到任何激活的输出设备)");
        }
        catch (Exception ex)
        {
            sb.AppendLine("枚举输出设备失败：" + ex.Message);
        }
        return sb.ToString();
    }

    private static MMDevice? PickDevice(int? index)
    {
        if (index is not int idx) return null;
        using var en = new MMDeviceEnumerator();
        var list = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        if (idx < 0 || idx >= list.Count) return null;
        return list[idx];
    }

    private static WasapiOut CreateOutput(int? deviceIndex, int latencyMs)
    {
        var device = PickDevice(deviceIndex);
        return device is null
            ? new WasapiOut(AudioClientShareMode.Shared, latencyMs)
            : new WasapiOut(device, AudioClientShareMode.Shared, true, latencyMs);
    }

    /// <summary>正弦波直通测试：只看声卡拉走多少采样，完全不经过 DSP。</summary>
    private static string RunTone(int? deviceIndex, int seconds)
    {
        var sb = new StringBuilder();
        var counter = new CountingSampleProvider(new SignalGenerator(44100, 2) { Gain = 0.15, Frequency = 440 });
        WasapiOut? output = null;
        try
        {
            output = CreateOutput(deviceIndex, 120);
            output.PlaybackStopped += (_, e) =>
            {
                if (e.Exception is not null) sb.AppendLine("PlaybackStopped 异常：" + e.Exception.Message);
            };
            output.Init(counter);
            sb.AppendLine("输出格式：" + counter.WaveFormat);
            output.Play();

            var sw = Stopwatch.StartNew();
            long last = 0;
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                Thread.Sleep(500);
                long now = counter.TotalPulled;
                double pulled = (double)now / counter.WaveFormat.Channels / counter.WaveFormat.SampleRate;
                sb.AppendLine($"  {sw.Elapsed.TotalSeconds:F1}s state={output.PlaybackState} 已拉走={pulled:F2}s音频 本段增量={(double)(now - last) / counter.WaveFormat.Channels / counter.WaveFormat.SampleRate:F2}s");
                last = now;
            }
            sb.AppendLine($"结论：{seconds} 秒墙钟时间里声卡拉走了 {(double)counter.TotalPulled / counter.WaveFormat.Channels / counter.WaveFormat.SampleRate:F2} 秒音频" +
                          (counter.TotalPulled > 0 ? "（设备正常在时钟）" : "（设备没有拉数据！）"));
        }
        catch (Exception ex)
        {
            sb.AppendLine("正弦波测试失败：" + ex);
        }
        finally
        {
            try { output?.Stop(); } catch { }
            try { output?.Dispose(); } catch { }
        }
        return sb.ToString();
    }

    /// <summary>走完整 DSP 链（SoundTouch）播放本地音频文件。</summary>
    private static string RunDsp(string audioPath, int? deviceIndex, int seconds, double? tempo, double? semitones,
        double? switchTempo = null, int switchAtSeconds = 0)
    {
        var sb = new StringBuilder();
        DspAudioPlayer? player = null;
        try
        {
            player = new DspAudioPlayer();
            player.Load(audioPath, deviceIndex);
            if (tempo is double t) player.Speed = t;
            if (semitones is double s) player.Semitones = s;
            sb.AppendLine("设备：" + player.DeviceDescription);
            sb.AppendLine("格式：" + player.WaveFormatText);
            sb.AppendLine("时长：" + player.Duration.TotalSeconds.ToString("F2") + "s");
            player.Play();

            var sw = Stopwatch.StartNew();
            double lastPos = -1;
            double lastAt = 0;
            double lastPosAtMark = 0;
            bool switched = false;
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                Thread.Sleep(500);
                if (switchTempo is double st && !switched && sw.Elapsed.TotalSeconds >= switchAtSeconds)
                {
                    switched = true;
                    player.Speed = st;                       // 播放中改速度（和界面上拖滑块走同一条路）
                    lastPosAtMark = player.Position.TotalSeconds;
                    lastAt = sw.Elapsed.TotalSeconds;
                    sb.AppendLine($"  >>> {sw.Elapsed.TotalSeconds:F1}s 播放中把速度切成 {st:F2}×（当前进度 {lastPosAtMark:F2}s）");
                }
                sb.AppendLine($"  {sw.Elapsed.TotalSeconds:F1}s {player.DescribeState()}");
                lastPos = player.Position.TotalSeconds;
            }
            sb.AppendLine($"结论：{seconds} 秒墙钟时间里进度从 0 走到 {lastPos:F2}s（1.0× 时应接近 {seconds:F2}s）");
            if (switched)
            {
                double wall = sw.Elapsed.TotalSeconds - lastAt;
                double posDelta = lastPos - lastPosAtMark;
                sb.AppendLine($"切换后：{wall:F2} 秒墙钟进度走了 {posDelta:F2}s，实测倍率 {(wall > 0 ? posDelta / wall : 0):F2}×（期望约 {switchTempo:F2}×）");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("DSP 链测试失败：" + ex);
        }
        finally
        {
            try { player?.Dispose(); } catch { }
        }
        return sb.ToString();
    }

    /// <summary>只负责数「下游拉走了多少采样」的透传包装。</summary>
    private sealed class CountingSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _inner;
        public CountingSampleProvider(ISampleProvider inner) { _inner = inner; WaveFormat = inner.WaveFormat; }
        public WaveFormat WaveFormat { get; }
        public long TotalPulled { get; private set; }
        public int Read(float[] buffer, int offset, int count)
        {
            int read = _inner.Read(buffer, offset, count);
            if (read > 0) TotalPulled += read;
            return read;
        }
    }
}
