using System.Buffers.Binary;

namespace DFAudioStudio.Core.Indexing;

/// <summary>
/// 极轻量的 WAV 头解析：只读文件前若干 KB，拿到采样率/声道/时长。
/// 该导出的音频都是标准 RIFF/WAVE（PCM），所以不需要解码器即可试听与识别。
/// </summary>
public static class WavProbe
{
    public sealed record Info(double DurationSec, int SampleRate, int Channels, int BitsPerSample, string Format);

    public static Info Probe(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192);
            Span<byte> head = stackalloc byte[12];
            if (fs.Read(head) < 12) return new Info(0, 0, 0, 0, "?");
            if (!(head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F'))
                return new Info(0, 0, 0, 0, "非RIFF");

            int sampleRate = 0, channels = 0, bits = 0;
            long dataSize = 0;
            Span<byte> chunkHeader = stackalloc byte[8];
            long limit = Math.Min(fs.Length, 8 * 1024 * 1024);

            while (fs.Position + 8 <= limit)
            {
                if (fs.Read(chunkHeader) < 8) break;
                string id = System.Text.Encoding.ASCII.GetString(chunkHeader[..4]);
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..8]);

                if (id == "fmt ")
                {
                    var fmt = new byte[Math.Min(size, 40)];
                    int read = fs.Read(fmt, 0, fmt.Length);
                    if (read >= 16)
                    {
                        channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2, 2));
                        sampleRate = BinaryPrimitives.ReadInt32LittleEndian(fmt.AsSpan(4, 4));
                        bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14, 2));
                    }
                    long skip = size - read;
                    if (skip > 0) fs.Seek(skip, SeekOrigin.Current);
                }
                else if (id == "data")
                {
                    dataSize = size;
                    break;
                }
                else
                {
                    fs.Seek(size + (size % 2), SeekOrigin.Current);
                }
            }

            double dur = 0;
            if (sampleRate > 0 && channels > 0 && bits > 0 && dataSize > 0)
                dur = dataSize / (double)(sampleRate * channels * (bits / 8.0));

            string fmtName = bits switch { 16 => "PCM16", 24 => "PCM24", 32 => "PCM32/Float", 8 => "PCM8", _ => bits > 0 ? $"PCM{bits}" : "?" };
            return new Info(Math.Round(dur, 3), sampleRate, channels, bits, fmtName);
        }
        catch
        {
            return new Info(0, 0, 0, 0, "err");
        }
    }
}
