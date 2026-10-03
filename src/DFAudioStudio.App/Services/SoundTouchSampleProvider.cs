using NAudio.Wave;
using SoundTouch;

namespace DFAudioStudio.App.Services;

/// <summary>
/// 把 NAudio 的采样流经 SoundTouch 处理：变速（Tempo，**保持音调**）与变调（PitchSemiTones，**保持速度**）。
/// 这是"无损变调/变速"的关键 —— 系统 MediaPlayer 的倍速会连带变调，做不到这两件事。
/// </summary>
internal sealed class SoundTouchSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly SoundTouchProcessor _st = new();
    private readonly float[] _inBuffer;
    private readonly float[] _outBuffer;

    private int _outCount;   // _outBuffer 中待输出的采样数
    private int _outPos;
    private bool _flushed;
    private bool _drained;

    /// <summary>累计被下游拉走的采样数（诊断用：能看出声卡到底有没有在拉数据）。</summary>
    public long TotalSamplesRead { get; private set; }

    /// <summary>累计从源读入的采样数。</summary>
    public long TotalSamplesPulled { get; private set; }

    /// <summary>下游调用 Read 的次数（诊断用）。</summary>
    public int ReadCalls { get; private set; }

    /// <summary>最近一次 Read 的请求长度 / 返回长度（诊断用）。</summary>
    public string LastReadInfo { get; private set; } = "(还没被调用)";

    /// <summary>Read 里抛出的异常（诊断用）。</summary>
    public string LastError { get; private set; } = "";

    /// <summary>下游给的缓冲区比请求小时收敛的次数（诊断用）。</summary>
    public int ClampHits { get; private set; }

    /// <summary>最近一次收敛的细节（诊断用）。</summary>
    public string LastClampInfo { get; private set; } = "";

    private double _tempo = 1.0;
    private double _pitchSemitones;

    public SoundTouchSampleProvider(ISampleProvider source)
    {
        _source = source;
        WaveFormat = source.WaveFormat;

        int channels = WaveFormat.Channels;
        _st.SampleRate = WaveFormat.SampleRate;
        _st.Channels = channels;
        _st.Tempo = 1.0;
        _st.PitchSemiTones = 0;

        _inBuffer = new float[4096 * channels];
        _outBuffer = new float[4096 * channels];
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>播放速度倍率（0.5~2.0）：变速但**音调不变**。</summary>
    public double Tempo
    {
        get => _tempo;
        set
        {
            _tempo = Math.Clamp(value, 0.1, 4.0);
            _st.Tempo = _tempo;
        }
    }

    /// <summary>音调偏移（半音，-12~+12）：变调但**速度不变**。</summary>
    public double PitchSemitones
    {
        get => _pitchSemitones;
        set
        {
            _pitchSemitones = Math.Clamp(value, -24.0, 24.0);
            _st.PitchSemiTones = _pitchSemitones;
        }
    }

    /// <summary>跳转后清掉内部残留，避免放出跳转前的旧声音。</summary>
    public void Clear()
    {
        _st.Clear();
        _outCount = 0;
        _outPos = 0;
        _flushed = false;
        _drained = false;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        ReadCalls++;
        try
        {
            int result = ReadCore(buffer, offset, count);
            LastReadInfo = $"请求{count}→返回{result}（第{ReadCalls}次）";
            return result;
        }
        catch (Exception ex)
        {
            if (string.IsNullOrEmpty(LastError))
                LastError = $"第{ReadCalls}次 Read（请求{count}）抛异常：{ex.GetType().Name} {ex.Message} | 栈：{Trim(ex.ToString())}";
            throw;
        }
    }

    private int ReadCore(float[] buffer, int offset, int count)
    {
        int channels = WaveFormat.Channels;

        // 下游（NAudio 的 SampleToWaveProvider）会按"120ms 的采样数"来请求，
        // 但它自己的中转数组可能比这个数小 —— 所以这里必须按目标数组的真实容量收敛，
        // 否则 Array.Copy 会写到数组外面（表现为 ArrayTypeMismatchException / 卡住不出声）。
        int limit = Math.Min(count, Math.Max(0, buffer.Length - offset));
        if (limit < count)
        {
            ClampHits++;
            LastClampInfo = $"第{ReadCalls}次：请求{count}，目标数组只剩{buffer.Length - offset}（本次只处理{limit}）";
        }

        int written = 0;

        while (written < limit)
        {
            if (_outPos < _outCount)
            {
                int copy = Math.Min(limit - written, _outCount - _outPos);
                try
                {
                    // ⚠ 必须用 Span 拷贝，不能用 Array.Copy：
                    // NAudio 的 SampleToWaveProvider 为了省一次拷贝，会把声卡的 byte[] 直接
                    // "当成" float[] 传进来（运行时类型仍是 Byte[]），而 Array.Copy 会做运行时
                    // 数组类型检查并抛 ArrayTypeMismatchException —— 表现就是"进度不走、没有声音"。
                    // Span 拷贝只按元素大小搬运，不做类型检查，是这里唯一正确的做法。
                    _outBuffer.AsSpan(_outPos, copy).CopyTo(buffer.AsSpan(offset + written, copy));
                }
                catch (Exception ex)
                {
                    LastError = $"拷贝失败（第{ReadCalls}次）：src={_outBuffer.GetType()} len={_outBuffer.Length} idx={_outPos} " +
                                $"dst={buffer.GetType()} len={buffer.Length} idx={offset + written} n={copy} " +
                                $"count={count} limit={limit} written={written} outPos={_outPos} outCount={_outCount} " +
                                $"_outCount={_outCount} → {ex.GetType().Name} {ex.Message}";
                    throw;
                }
                _outPos += copy;
                written += copy;
                continue;
            }

            if (_drained) break;

            int framesWanted = Math.Max(1, (limit - written) / channels);
            int framesToRead = Math.Min(framesWanted, _inBuffer.Length / channels);   // 不能超过输入缓冲
            int framesIn = _source.Read(_inBuffer, 0, framesToRead * channels) / channels;
            TotalSamplesPulled += framesIn * channels;

            if (framesIn <= 0)
            {
                // 源到头了：把 SoundTouch 内部剩余的处理完，再收尾
                if (!_flushed)
                {
                    _st.Flush();
                    _flushed = true;
                }
                int tail = _st.ReceiveSamples(_outBuffer, _outBuffer.Length / channels) * channels;
                if (tail <= 0)
                {
                    _drained = true;
                    break;
                }
                _outCount = tail;
                _outPos = 0;
                continue;
            }

            _st.PutSamples(_inBuffer.AsSpan(0, framesIn * channels), framesIn);
            int produced = _st.ReceiveSamples(_outBuffer, _outBuffer.Length / channels) * channels;
            _outCount = produced;
            _outPos = 0;

            if (produced <= 0 && _outCount <= 0)
            {
                // SoundTouch 需要更多输入才会产出（起播时的预填充），继续拉源
                continue;
            }
        }

        TotalSamplesRead += written;
        return written;
    }

    /// <summary>诊断用：当前累计读出的采样数。</summary>
    public long SnapshotRead() => TotalSamplesRead;

    private static string Trim(string text) => text.Length <= 700 ? text : text[..700];
}
