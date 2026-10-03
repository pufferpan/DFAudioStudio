using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DFAudioStudio.App.Services;

/// <summary>
/// 变速 / 变调用的播放引擎：NAudio 解码 + SoundTouch 实时 DSP + WASAPI 输出。
///
/// 为什么不用系统 MediaPlayer：它的倍速（PlaybackRate）必然连带变调，
/// 做不到「变速保持音调」与「独立变调滑块」这两件事。这里用 SoundTouch 的
/// Tempo（变速不变调）与 PitchSemiTones（变调不变速）实现。
///
/// 普通播放默认仍走系统 MediaPlayer（多格式解码更稳）；只有变速不是 1.00×
/// 或变调不是 0 半音时，播放条才切到本引擎，回到中性值再交还回去。
/// </summary>
public sealed class DspAudioPlayer : IDisposable, SpectrumService.IPlaybackClock
{
    /// <summary>变速范围（1.0 = 原速）。</summary>
    public const double MinSpeed = 0.5;
    public const double MaxSpeed = 2.0;

    /// <summary>变调范围（半音，0 = 原调）。</summary>
    public const int MinSemitones = -12;
    public const int MaxSemitones = 12;

    private AudioFileReader? _reader;
    private SoundTouchSampleProvider? _dsp;
    private WasapiOut? _output;
    private string? _path;
    private bool _disposed;

    private double _speed = 1.0;
    private double _semitones;
    private float _volume = 1f;

    /// <summary>播放自然结束（不是被 Pause/Stop）。</summary>
    public event Action? PlaybackEnded;

    public bool IsLoaded => _reader is not null;
    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;
    public string? Path => _path;

    /// <summary>供背景频谱读取的当前进度（秒）。</summary>
    public double PositionSeconds => Position.TotalSeconds;

    public TimeSpan Duration => _reader?.TotalTime ?? TimeSpan.Zero;

    /// <summary>当前位置（源文件时间轴，和普通模式一致）。</summary>
    public TimeSpan Position
    {
        get => _reader?.CurrentTime ?? TimeSpan.Zero;
        set
        {
            if (_reader is null) return;
            try
            {
                _reader.CurrentTime = value;
                _dsp?.Clear();      // 丢掉跳转前的残留，避免放出旧声音
            }
            catch (Exception ex)
            {
                AppServices.Current.LogWarn("DSP 播放器跳转失败：" + ex.Message);
            }
        }
    }

    public float Volume
    {
        get => _reader?.Volume ?? _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            if (_reader is not null) _reader.Volume = _volume;
        }
    }

    /// <summary>速度倍率：0.5~2.0，**保持音调**。</summary>
    public double Speed
    {
        get => _speed;
        set
        {
            _speed = Math.Clamp(value, MinSpeed, MaxSpeed);
            if (_dsp is not null) _dsp.Tempo = _speed;
        }
    }

    /// <summary>音调偏移：-12~+12 半音，**保持速度**。</summary>
    public double Semitones
    {
        get => _semitones;
        set
        {
            _semitones = Math.Clamp(value, MinSemitones, MaxSemitones);
            if (_dsp is not null) _dsp.PitchSemitones = _semitones;
        }
    }

    /// <summary>当前实际使用的输出设备名（诊断用）。</summary>
    public string DeviceDescription { get; private set; } = "(默认设备)";

    /// <summary>当前解码后的输出格式（诊断用）。</summary>
    public string WaveFormatText => _dsp?.WaveFormat.ToString() ?? "(无)";

    /// <summary>加载音频（支持 MediaFoundation 能解的 WAV/MP3 等；解不了会抛异常由调用方兜）。</summary>
    /// <param name="path">音频文件路径。</param>
    /// <param name="deviceIndex">指定输出设备序号（null = 系统默认设备）。</param>
    public void Load(string path, int? deviceIndex = null)
    {
        Stop();
        DisposeOutput();

        _reader = new AudioFileReader(path)
        {
            Volume = _volume,
        };
        _dsp = new SoundTouchSampleProvider(_reader.ToSampleProvider())
        {
            Tempo = _speed,
            PitchSemitones = _semitones,
        };
        _path = path;

        MMDevice? device = null;
        if (deviceIndex is int idx)
        {
            try
            {
                using var en = new MMDeviceEnumerator();
                var list = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                if (idx >= 0 && idx < list.Count) device = list[idx];
            }
            catch (Exception ex)
            {
                AppServices.Current.LogWarn("选择输出设备失败，改用默认设备：" + ex.Message);
            }
        }

        DeviceDescription = device?.FriendlyName ?? "(系统默认设备)";
        _output = device is null
            ? new WasapiOut(AudioClientShareMode.Shared, 120)
            : new WasapiOut(device, AudioClientShareMode.Shared, true, 120);

        _output.PlaybackStopped += (_, e) =>
        {
            // 输出线程挂了要留下证据（否则表现就是"进度不走、没有声音"）
            if (e.Exception is not null)
            {
                LastStopInfo = $"{e.Exception.GetType().Name}: {e.Exception.Message}";
                AppServices.Current.LogWarn($"DSP 输出已停止（异常）：{e.Exception.GetType().Name} {e.Exception.Message}");
            }
            // 自然播完才通知（用户暂停 / 停掉不算）
            else if (_reader is not null && _reader.CurrentTime >= _reader.TotalTime - TimeSpan.FromMilliseconds(120))
                PlaybackEnded?.Invoke();
        };
        _output.Init(_dsp);
    }

    public void Play()
    {
        try { _output?.Play(); } catch (Exception ex) { AppServices.Current.LogWarn("DSP 播放失败：" + ex.Message); }
    }

    public void Pause()
    {
        try { _output?.Pause(); } catch { /* 忽略 */ }
    }

    public void Stop()
    {
        try { _output?.Stop(); } catch { /* 忽略 */ }
    }

    /// <summary>诊断用：声卡输出状态 + 已拉走 / 已读入的采样数。</summary>
    public string DescribeState() =>
        $"state={_output?.PlaybackState.ToString() ?? "(空)"} out={_dsp?.TotalSamplesRead ?? 0} src={_dsp?.TotalSamplesPulled ?? 0} " +
        $"pos={Position.TotalSeconds:F2}s/{Duration.TotalSeconds:F2}s calls={_dsp?.ReadCalls ?? 0} " +
        $"[{_dsp?.LastReadInfo ?? ""}]" +
        ((_dsp?.ClampHits ?? 0) > 0 ? $" 收敛次数={_dsp!.ClampHits}" : "") +
        (string.IsNullOrEmpty(_dsp?.LastError) ? "" : $" err={_dsp!.LastError}") +
        (string.IsNullOrEmpty(LastStopInfo) ? "" : $" stop={LastStopInfo}");

    /// <summary>输出线程停止的原因（异常时非空，诊断用）。</summary>
    public string LastStopInfo { get; private set; } = "";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        DisposeOutput();
        _reader?.Dispose();
        _reader = null;
        _dsp = null;
        _path = null;
    }

    private void DisposeOutput()
    {
        try { _output?.Dispose(); } catch { /* 忽略 */ }
        _output = null;
    }

    // ── 离线自检：把音频跑一遍 DSP 链（不经过声卡），用于验证「变速不变调 / 变调不变速」──

    /// <summary>
    /// 离线自检：对指定音频按给定速度/音调处理，输出处理后的采样（单声道混合），供 CLI 验证。
    /// 不创建 WASAPI 输出，因此不需要声卡，也不会出声。
    /// </summary>
    public static (float[] Samples, int SampleRate, int Channels) ProcessOffline(
        string path, double tempo, double semitones)
    {
        using var reader = new AudioFileReader(path);
        ISampleProvider source = reader.ToSampleProvider();
        if (source.WaveFormat.Channels == 2)
            source = new StereoToMonoSampleProvider(source);
        var provider = new SoundTouchSampleProvider(source) { Tempo = tempo, PitchSemitones = semitones };
        ISampleProvider dsp = provider;

        var output = new List<float>(reader.WaveFormat.SampleRate * 4);
        var buffer = new float[8192];
        int guard = 0;
        while (guard++ < 200_000)
        {
            int read = dsp.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            output.AddRange(buffer.Take(read));
        }
        return (output.ToArray(), reader.WaveFormat.SampleRate, 1);
    }
}
