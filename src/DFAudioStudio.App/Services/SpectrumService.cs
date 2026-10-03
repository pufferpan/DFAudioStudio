using DFAudioStudio.Core.Indexing;
using Microsoft.UI.Dispatching;
using Windows.Media.Playback;

namespace DFAudioStudio.App.Services;

/// <summary>
/// 播放时的频谱分析：复用 Core 的 WAV 解码（16kHz 单声道 float）→ 按播放进度取窗做 FFT
/// → 输出 <see cref="BandCount"/> 个频段的 0..1 能量，供背景做「随音乐变化」的可视化。
///
/// · 播放中：每帧（约 30fps）按 <c>PlaybackSession.Position</c> 取 1024 点做 FFT（汉宁窗），
///   频段按对数分布（40Hz~7kHz），快起慢落（attack/decay）平滑，避免柱子抖。
/// · 暂停 / 停止 / 播完：能量自然衰减到 0 并触发 <see cref="ActiveChanged"/>(false)，
///   背景恢复原来的三色渐变。
/// · 解码不了的音频（非 PCM 的 WAV）不会报错，只是没有可视化。
/// </summary>
public sealed class SpectrumService
{
    /// <summary>频段数量（背景柱状图的数量）。</summary>
    public const int BandCount = 40;

    private const int FftSize = 1024;                       // 16kHz 下 = 64ms 窗，频率分辨率 15.6Hz
    private const int SampleRate = AudioDecoder.TargetSampleRate;
    private const double MinHz = 40;                        // 最低频段
    private const double MaxHz = 7000;                      // 最高频段（16kHz 采样下有效范围）
    private const double MaxDecodeSeconds = 300;            // 只分析前 5 分钟，够看频谱又省内存
    private const double SilenceDb = -58;                   // 该分贝以下算静音
    private const float Attack = 0.55f;                     // 起（快）
    private const float Release = 0.12f;                    // 落（慢）
    private const int TickMs = 33;                          // 约 30fps

    public static SpectrumService Current { get; } = new();

    private readonly float[] _bands = new float[BandCount];
    private readonly float[] _targets = new float[BandCount];
    private readonly float[] _re = new float[FftSize];
    private readonly float[] _im = new float[FftSize];
    private readonly float[] _cos = new float[FftSize / 2];
    private readonly float[] _sin = new float[FftSize / 2];
    private readonly int[] _rev = new int[FftSize];
    private readonly int[] _bandLo = new int[BandCount + 1];

    /// <summary>播放进度的来源：系统 MediaPlayer 与变速/变调用的 DSP 引擎都实现它。</summary>
    public interface IPlaybackClock
    {
        bool IsPlaying { get; }
        double PositionSeconds { get; }
    }

    /// <summary>把 MediaPlayer 的会话包成 <see cref="IPlaybackClock"/>。</summary>
    public sealed class MediaPlayerClock : IPlaybackClock
    {
        private readonly MediaPlayer _player;
        public MediaPlayerClock(MediaPlayer player) => _player = player;
        public bool IsPlaying => _player.PlaybackSession?.PlaybackState == MediaPlaybackState.Playing;
        public double PositionSeconds => _player.PlaybackSession?.Position.TotalSeconds ?? 0;
    }

    private readonly object _gate = new();

    private IPlaybackClock? _clock;
    private float[] _samples = Array.Empty<float>();
    private int _decodeToken;
    private DispatcherQueueTimer? _timer;

    /// <summary>是否处于「正在播放」状态（背景据此淡入频谱层）。</summary>
    public bool IsActive { get; private set; }

    /// <summary>整体能量 0..1（低频权重更高，用于背景脉冲）。</summary>
    public float Level { get; private set; }

    /// <summary>播放状态变化：true=开始播放（频谱层淡入），false=停了（淡出回渐变）。</summary>
    public event Action<bool>? ActiveChanged;

    /// <summary>每帧更新（UI 线程），背景在这里刷新柱子高度。</summary>
    public event Action? FrameUpdated;

    private SpectrumService()
    {
        // 位反转表 + 旋转因子（只算一次）
        int bits = (int)Math.Log2(FftSize);
        for (int i = 0; i < FftSize; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++)
                if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
            _rev[i] = r;
        }
        for (int i = 0; i < FftSize / 2; i++)
        {
            double angle = -2 * Math.PI * i / FftSize;
            _cos[i] = (float)Math.Cos(angle);
            _sin[i] = (float)Math.Sin(angle);
        }

        // 频段边界（对数分布），存成 bin 下标
        for (int b = 0; b <= BandCount; b++)
        {
            double hz = MinHz * Math.Pow(MaxHz / MinHz, b / (double)BandCount);
            _bandLo[b] = Math.Clamp((int)Math.Round(hz * FftSize / SampleRate), 1, FftSize / 2 - 1);
        }
    }

    /// <summary>绑定播放进度来源与当前条目（换条目前先 Detach，或直接再 Attach 覆盖）。</summary>
    public void Attach(IPlaybackClock clock, string path)
    {
        _clock = clock;

        int token = ++_decodeToken;
        _samples = Array.Empty<float>();

        _ = Task.Run(() =>
        {
            try
            {
                var decoded = AudioDecoder.DecodeMono16k(path, out var info);
                if (decoded.Length == 0)
                {
                    AppServices.Current.LogWarn($"频谱：无法解码（{info.Format} / {info.SourceSampleRate}Hz），本条不做背景可视化：{Path.GetFileName(path)}");
                    return;
                }

                int max = (int)(MaxDecodeSeconds * SampleRate);
                if (decoded.Length > max) Array.Resize(ref decoded, max);
                if (token != _decodeToken) return;   // 已经换歌了，丢掉
                lock (_gate) _samples = decoded;
                AppServices.Current.LogInfo($"频谱：已就绪（{info.Format} {info.SourceSampleRate}Hz→16kHz，{decoded.Length / (double)SampleRate:F1}s）");
            }
            catch (Exception ex)
            {
                AppServices.Current.LogWarn($"频谱：解码异常，本条不做背景可视化：{ex.Message}");
            }
        });

        StartTimer();
    }

    /// <summary>停止播放 / 换源 / 控件卸载时调用：停掉数据源，能量自然衰减后背景自动淡出。</summary>
    public void Detach()
    {
        _decodeToken++;
        _clock = null;
        lock (_gate) _samples = Array.Empty<float>();

        // 刻意不立刻通知「已停止」：换歌时 StartPlaybackAsync 会先 Detach 再 Attach，
        // 立刻淡出会让背景闪一下。交给下面 OnTick 的能量衰减来决定何时真的淡出
        // （暂停 / 停止 / 播完都会在半秒内收敛到 0 并触发）。
    }

    private void StartTimer()
    {
        if (_timer is not null)
        {
            if (!_timer.IsRunning) _timer.Start();
            return;
        }

        var queue = DispatcherQueue.GetForCurrentThread();
        if (queue is null) return;      // 非 UI 线程调用：等下一次在 UI 线程 Attach
        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(TickMs);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
    }

    private void OnTick()
    {
        try
        {
            var clock = _clock;
            bool playing = clock?.IsPlaying == true;

            float[] samples;
            lock (_gate) samples = _samples;

            if (playing && samples.Length > FftSize)
            {
                int start = (int)(clock!.PositionSeconds * SampleRate);
                start = Math.Clamp(start, 0, Math.Max(0, samples.Length - FftSize - 1));
                ComputeSpectrum(samples, start);
                Smooth();
                if (!IsActive)
                {
                    IsActive = true;
                    ActiveChanged?.Invoke(true);
                }
                Heartbeat();
                FrameUpdated?.Invoke();
                return;
            }

            // 没在播：能量回落，全部归零后通知背景切回渐变，并把定时器停掉省电
            bool wentSilent = Release0();
            FrameUpdated?.Invoke();
            if (wentSilent && IsActive)
            {
                IsActive = false;
                ActiveChanged?.Invoke(false);

                // 只有"真的没有数据源了"才停表。
                // 换歌是 Detach → 立刻 Attach，中间能量会短暂归零；如果这里无条件停表，
                // 新曲子随后开始播时已经没人再叫醒它了 —— 表现就是"切歌之后背景频谱再也不亮"
                // （暂停后恢复播放也是同一个坑）。_clock 为 null 才是真停（停止 / 卸载 / 交还 DSP）。
                if (_clock is null) _timer?.Stop();
            }
        }
        catch (Exception ex)
        {
            AppServices.Current.LogWarn("频谱：刷新异常（已忽略）：" + ex.Message);
        }
    }

    /// <summary>取 start 处的 1024 点做 FFT，算出各频段目标值（0..1）。</summary>
    private void ComputeSpectrum(float[] samples, int start)
    {
        for (int i = 0; i < FftSize; i++)
        {
            double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1));   // 汉宁窗
            _re[i] = (float)(samples[start + i] * w);
            _im[i] = 0;
        }

        Fft();

        // 满幅正弦的谱线幅度约 FftSize/4，用它归一化 → 0dB = 满幅
        double norm = FftSize / 4.0;
        for (int b = 0; b < BandCount; b++)
        {
            int lo = _bandLo[b], hi = Math.Max(lo + 1, _bandLo[b + 1]);
            double sum = 0;
            for (int k = lo; k < hi && k < FftSize / 2; k++)
                sum += Math.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]);
            double mag = sum / (hi - lo) / norm;

            double hz = (lo + hi) * 0.5 * SampleRate / FftSize;
            mag *= 1.0 + 1.5 * Math.Min(1.0, hz / 4000.0);                      // 高频补偿，柱子更均匀

            double db = 20 * Math.Log10(mag + 1e-6);
            _targets[b] = (float)Math.Clamp((db - SilenceDb) / -SilenceDb, 0, 1);
        }

        // 整体能量：低频权重更高（更贴近"鼓点跳动"的观感）
        Level = (float)Math.Clamp(
            0.55 * Avg(0, BandCount / 3) + 0.30 * Avg(BandCount / 3, 2 * BandCount / 3) + 0.15 * Avg(2 * BandCount / 3, BandCount),
            0, 1);

        // 自动增益：安静的素材（语音、环境音）也能看清柱子，最多放大 3 倍，响的素材不放大
        float peak = 0;
        for (int b = 0; b < BandCount; b++)
            if (_targets[b] > peak) peak = _targets[b];
        _peakHold = Math.Max(peak, _peakHold * 0.99f);
        float gain = _peakHold > 0.03f ? Math.Clamp(0.45f / _peakHold, 1f, 3f) : 1f;
        for (int b = 0; b < BandCount; b++)
            _targets[b] = Math.Clamp(_targets[b] * gain, 0, 1);
    }

    private float _peakHold;

    private float Avg(int from, int to)
    {
        float sum = 0;
        for (int i = from; i < to; i++) sum += _targets[i];
        return to > from ? sum / (to - from) : 0;
    }

    private long _lastHeartbeatMs;

    /// <summary>每 5 秒记一条当前能量，便于确认「频谱确实在随音乐变化」。</summary>
    private void Heartbeat()
    {
        long now = Environment.TickCount64;
        if (now - _lastHeartbeatMs < 5000) return;
        _lastHeartbeatMs = now;

        int peak = 0;
        for (int b = 1; b < BandCount; b++)
            if (_bands[b] > _bands[peak]) peak = b;
        double lo = _bandLo[peak] * SampleRate / (double)FftSize;
        double hi = _bandLo[Math.Min(BandCount, peak + 1)] * SampleRate / (double)FftSize;
        AppServices.Current.LogInfo($"频谱：level={Level:F2} 峰值={lo:F0}-{hi:F0}Hz 频段能量前 8 段=[{string.Join(", ", _bands.Take(8).Select(v => v.ToString("F2")))}]");
    }

    private void Smooth()
    {
        for (int b = 0; b < BandCount; b++)
        {
            float cur = _bands[b], tgt = _targets[b];
            _bands[b] = tgt > cur ? cur + (tgt - cur) * Attack : cur + (tgt - cur) * Release;
        }
    }

    /// <summary>没在播时让所有频段往 0 收；返回 true 表示已经基本全静音。</summary>
    private bool Release0()
    {
        float max = 0;
        for (int b = 0; b < BandCount; b++)
        {
            _bands[b] *= 1f - Release;
            if (_bands[b] < 0.004f) _bands[b] = 0;
            if (_bands[b] > max) max = _bands[b];
        }
        Level *= 1f - Release;
        if (Level < 0.004f) Level = 0;
        _ = max;
        return Level <= 0.004f && _bands.All(v => v <= 0.004f);
    }

    /// <summary>当前各频段能量（0..1）的副本，供背景刷新柱子。</summary>
    public float[] Snapshot()
    {
        lock (_gate) return (float[])_bands.Clone();
    }

    /// <summary>
    /// 离线自检（命令行 --spectrumtest）：解码指定音频并按时间轴取样算频谱，
    /// 返回可读报告（含每段的整体能量与 40 段能量的字符柱状图），不依赖播放器与界面。
    /// </summary>
    public string SelfTest(string wavPath, int steps = 8)
    {
        var sb = new System.Text.StringBuilder();
        var samples = AudioDecoder.DecodeMono16k(wavPath, out var info);
        sb.AppendLine($"文件        ：{System.IO.Path.GetFileName(wavPath)}");
        sb.AppendLine($"源格式      ：{info.Format} / {info.SourceSampleRate}Hz / {info.SourceChannels}ch / {info.SourceSeconds:F2}s");
        sb.AppendLine($"分析输入    ：16kHz 单声道 {samples.Length} 点（{samples.Length / (double)SampleRate:F2}s）");
        if (samples.Length <= FftSize)
        {
            sb.AppendLine("样本太短，无法分析。");
            return sb.ToString();
        }

        sb.AppendLine();
        sb.AppendLine("时间点   整体能量  峰值频段      40 段频谱（左低右高）");
        const string blocks = "▁▂▃▄▅▆▇█";
        for (int i = 0; i < steps; i++)
        {
            int start = (int)((samples.Length - FftSize) * (i / (double)Math.Max(1, steps - 1)));
            start = Math.Clamp(start, 0, samples.Length - FftSize - 1);

            ComputeSpectrum(samples, start);
            Array.Copy(_targets, _bands, BandCount);

            int peak = 0;
            for (int b = 1; b < BandCount; b++)
                if (_bands[b] > _bands[peak]) peak = b;

            double peakHzLo = _bandLo[peak] * SampleRate / (double)FftSize;
            double peakHzHi = _bandLo[Math.Min(BandCount, peak + 1)] * SampleRate / (double)FftSize;

            var chart = new char[BandCount];
            for (int b = 0; b < BandCount; b++)
            {
                int idx = (int)Math.Round(_bands[b] * (blocks.Length - 1));
                chart[b] = blocks[Math.Clamp(idx, 0, blocks.Length - 1)];
            }

            sb.AppendLine($"{start / (double)SampleRate,7:F2}s {Level,8:F2}  {peakHzLo,6:F0}-{peakHzHi,-6:F0}Hz  {new string(chart)}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// DSP 自检（命令行 --dsptest）：把音频按不同速度 / 音调跑一遍 SoundTouch 链（不出声），
    /// 用自相关测出输出主频、并报告时长，用来验证：
    ///   变速（Tempo）→ 时长变、**主频不变**；变调（PitchSemiTones）→ 主频变、**时长不变**。
    /// </summary>
    public static string DspSelfTest(string path)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"文件：{System.IO.Path.GetFileName(path)}");
        sb.AppendLine();
        sb.AppendLine("设置                  输出时长   主频      说明");

        double baseFreq = 0, baseSeconds = 0;
        var cases = new (string Label, double Tempo, double Semi)[]
        {
            ("原始（1.00×，0 半音）", 1.0, 0),
            ("变速 0.50×（音调应不变）", 0.5, 0),
            ("变速 2.00×（音调应不变）", 2.0, 0),
            ("变调 +12 半音（速度应不变）", 1.0, 12),
            ("变调 -12 半音（速度应不变）", 1.0, -12),
            ("变速 1.50× + 变调 -5 半音", 1.5, -5),
        };

        foreach (var (label, tempo, semi) in cases)
        {
            var (samples, rate, _) = DspAudioPlayer.ProcessOffline(path, tempo, semi);
            double seconds = samples.Length / (double)rate;
            double freq = DominantFrequency(samples, rate);
            if (label.StartsWith("原始")) { baseFreq = freq; baseSeconds = seconds; }

            string note = "";
            if (Math.Abs(tempo - 1.0) > 0.001 && Math.Abs(semi) < 0.001)
                note = $"时长 {(seconds / Math.Max(0.001, baseSeconds)):F2}×（期望 {1 / tempo:F2}×），音调 {(baseFreq > 0 ? freq / baseFreq : 0):F3}×（期望 1.000）";
            else if (Math.Abs(semi) > 0.001 && Math.Abs(tempo - 1.0) < 0.001)
                note = $"时长 {(seconds / Math.Max(0.001, baseSeconds)):F2}×（期望 1.00），音调 {(baseFreq > 0 ? freq / baseFreq : 0):F3}×（期望 {Math.Pow(2, semi / 12.0):F3}）";

            sb.AppendLine($"{label,-26} {seconds,7:F2}s {freq,8:F1}Hz  {note}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 自相关测基频（对单音 / 持续音足够准，用于自检）：
    /// 取归一化自相关里**第一个**达到峰值的局部极大（避免落到次谐波上），再做抛物线插值细化。
    /// </summary>
    private static double DominantFrequency(float[] samples, int rate)
    {
        if (samples.Length < rate / 4) return 0;

        // 取中段 0.5s，避免首尾静音
        int start = Math.Max(0, samples.Length / 2 - rate / 4);
        int len = Math.Min(rate / 2, samples.Length - start);
        if (len < 2048) return 0;

        var window = new double[len];
        double mean = 0;
        for (int i = 0; i < len; i++) mean += samples[start + i];
        mean /= len;
        for (int i = 0; i < len; i++) window[i] = samples[start + i] - mean;

        int minLag = Math.Max(2, rate / 2000);          // 最高测到 2kHz
        int maxLag = Math.Min(len / 2, rate / 40);      // 最低测到 40Hz
        if (maxLag <= minLag + 2) return 0;

        var corr = new double[maxLag + 2];
        for (int lag = minLag; lag <= maxLag + 1; lag++)
        {
            double sum = 0;
            for (int i = 0; i < len - lag; i++) sum += window[i] * window[i + lag];
            corr[lag] = sum / (len - lag);
        }

        double max = 0;
        for (int lag = minLag; lag <= maxLag; lag++)
            if (corr[lag] > max) max = corr[lag];
        if (max <= 0) return 0;

        // 第一个达到峰值 90% 的局部极大 = 基音周期
        for (int lag = minLag + 1; lag < maxLag; lag++)
        {
            if (corr[lag] < 0.9 * max) continue;
            if (corr[lag] < corr[lag - 1] || corr[lag] < corr[lag + 1]) continue;

            // 抛物线插值：峰值位置 = lag + 0.5*(y[-1]-y[+1]) / (y[-1]-2y[0]+y[+1])
            double y0 = corr[lag - 1], y1 = corr[lag], y2 = corr[lag + 1];
            double denom = y0 - 2 * y1 + y2;
            double delta = Math.Abs(denom) < 1e-12 ? 0 : 0.5 * (y0 - y2) / denom;
            double period = lag + Math.Clamp(delta, -0.5, 0.5);
            return period > 0 ? rate / period : 0;
        }
        return 0;
    }

    // ---------- 迭代式基 2 FFT（原地，输入 _re/_im，输出频域）----------

    private void Fft()
    {
        for (int i = 0; i < FftSize; i++)
        {
            int j = _rev[i];
            if (j > i)
            {
                (_re[i], _re[j]) = (_re[j], _re[i]);
                (_im[i], _im[j]) = (_im[j], _im[i]);
            }
        }

        for (int len = 2; len <= FftSize; len <<= 1)
        {
            int half = len >> 1;
            int step = FftSize / len;
            for (int i = 0; i < FftSize; i += len)
            {
                for (int k = 0; k < half; k++)
                {
                    int tw = k * step;
                    float wr = _cos[tw], wi = _sin[tw];
                    int a = i + k, b = i + k + half;
                    float tr = _re[b] * wr - _im[b] * wi;
                    float ti = _re[b] * wi + _im[b] * wr;
                    _re[b] = _re[a] - tr;
                    _im[b] = _im[a] - ti;
                    _re[a] += tr;
                    _im[a] += ti;
                }
            }
        }
    }
}
