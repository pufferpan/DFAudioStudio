using System.Buffers.Binary;
using System.Text;

namespace DFAudioStudio.Core.Indexing;

/// <summary>
/// 把任意 WAV（PCM 8/16/24/32 位、32/64 位浮点、多声道、任意采样率）解成
/// **16kHz 单声道 float 采样**，供 Whisper.net 使用（该库只接受 16kHz 输入）。
/// 游戏导出的是 44.1/48kHz 立体声，必须先重采样，否则会报 "Only 16KHz sample rate is supported"。
/// </summary>
public static class AudioDecoder
{
    public const int TargetSampleRate = 16000;

    /// <summary>为控制内存，超过该长度的数据只取前面一部分（默认 20 分钟）。</summary>
    public const double MaxSeconds = 20 * 60;

    public sealed record DecodeInfo(int SourceSampleRate, int SourceChannels, int BitsPerSample, string Format, double SourceSeconds, bool Truncated);

    public static float[] DecodeMono16k(string path) => DecodeMono16k(path, out _);

    public static float[] DecodeMono16k(string path, out DecodeInfo info)
    {
        int fmtTag = 1, channels = 0, sampleRate = 0, bits = 0;
        long dataOffset = -1, dataSize = 0;

        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16))
        {
            Span<byte> head = stackalloc byte[12];
            if (fs.Read(head) < 12 || !(head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F'))
            {
                info = new DecodeInfo(0, 0, 0, "非RIFF", 0, false);
                return Array.Empty<float>();
            }

            Span<byte> ch = stackalloc byte[8];
            while (fs.Position + 8 <= fs.Length)
            {
                if (fs.Read(ch) < 8) break;
                string id = Encoding.ASCII.GetString(ch[..4]);
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(ch[4..8]);
                long bodyStart = fs.Position;

                if (id == "fmt ")
                {
                    var fmt = new byte[Math.Min(size, 64)];
                    int read = fs.Read(fmt, 0, fmt.Length);
                    if (read >= 16)
                    {
                        fmtTag = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(0, 2));
                        channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2, 2));
                        sampleRate = BinaryPrimitives.ReadInt32LittleEndian(fmt.AsSpan(4, 4));
                        bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14, 2));
                        // WAVE_FORMAT_EXTENSIBLE：真实格式在前 2 字节的 SubFormat GUID 里
                        if (fmtTag == 0xFFFE && read >= 26)
                            fmtTag = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(24, 2));
                    }
                }
                else if (id == "data")
                {
                    dataOffset = bodyStart;
                    dataSize = size == 0xFFFFFFFF ? fs.Length - bodyStart : size;
                    if (channels > 0) break;
                }

                long next = bodyStart + size + (size % 2);
                if (next <= bodyStart) break;
                fs.Position = Math.Min(next, fs.Length);
            }

            if (dataOffset < 0 || channels <= 0 || sampleRate <= 0 || bits <= 0)
            {
                info = new DecodeInfo(sampleRate, channels, bits, "残缺", 0, false);
                return Array.Empty<float>();
            }

            int bytesPerSample = bits / 8;
            int frameBytes = bytesPerSample * channels;
            long frames = dataSize / frameBytes;
            int maxFrames = (int)Math.Min(frames, (long)(MaxSeconds * sampleRate));
            bool truncated = maxFrames < frames;

            double srcSeconds = frames / (double)sampleRate;
            info = new DecodeInfo(sampleRate, channels, bits,
                fmtTag switch { 1 => $"PCM{bits}", 3 => $"Float{bits}", _ => $"Fmt{fmtTag}" },
                srcSeconds, truncated);

            // ── 解码 + 下混为单声道 float ───────────────────────────────
            var mono = new float[maxFrames];
            fs.Position = dataOffset;
            int bufFrames = 8192;
            var buffer = new byte[bufFrames * frameBytes];
            int frameIndex = 0;

            while (frameIndex < maxFrames)
            {
                int want = Math.Min(bufFrames, maxFrames - frameIndex) * frameBytes;
                int got = fs.Read(buffer, 0, want);
                if (got < frameBytes) break;
                int gotFrames = got / frameBytes;

                for (int f = 0; f < gotFrames && frameIndex < maxFrames; f++, frameIndex++)
                {
                    float sum = 0;
                    int baseIdx = f * frameBytes;
                    for (int c = 0; c < channels; c++)
                    {
                        int off = baseIdx + c * bytesPerSample;
                        sum += fmtTag switch
                        {
                            3 when bits == 32 => BitConverter.ToSingle(buffer, off),
                            3 when bits == 64 => (float)BitConverter.ToDouble(buffer, off),
                            1 when bits == 8 => (buffer[off] - 128) / 128f,
                            1 when bits == 16 => BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(off, 2)) / 32768f,
                            1 when bits == 24 => ReadInt24(buffer, off) / 8388608f,
                            1 when bits == 32 => BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(off, 4)) / 2147483648f,
                            _ => 0f
                        };
                    }
                    mono[frameIndex] = sum / channels;
                }
            }

            if (frameIndex < mono.Length)
                Array.Resize(ref mono, frameIndex);

            return Resample(mono, sampleRate, TargetSampleRate);
        }
    }

    private static int ReadInt24(byte[] b, int off)
    {
        int v = b[off] | (b[off + 1] << 8) | (b[off + 2] << 16);
        if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
        return v;
    }

    /// <summary>线性插值重采样（对 ASR 足够，且无第三方依赖）。</summary>
    public static float[] Resample(float[] input, int sourceRate, int targetRate)
    {
        if (input.Length == 0) return input;
        if (sourceRate == targetRate) return input;

        double ratio = sourceRate / (double)targetRate;
        int outLen = (int)Math.Floor(input.Length / ratio);
        if (outLen <= 0) return Array.Empty<float>();

        var output = new float[outLen];
        for (int i = 0; i < outLen; i++)
        {
            double pos = i * ratio;
            int i0 = (int)pos;
            int i1 = Math.Min(i0 + 1, input.Length - 1);
            float frac = (float)(pos - i0);
            output[i] = input[i0] * (1 - frac) + input[i1] * frac;
        }
        return output;
    }
}
