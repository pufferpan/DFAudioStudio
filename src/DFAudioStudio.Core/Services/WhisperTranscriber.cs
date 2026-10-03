using System.Runtime.InteropServices;
using System.Text;
using DFAudioStudio.Core.Indexing;
using DFAudioStudio.Core.Models;
using Whisper.net;

namespace DFAudioStudio.Core.Services;

public interface ITranscriber : IDisposable
{
    string Name { get; }
    string ModelName { get; }
    Task<TranscriptResult> TranscribeAsync(string wavPath, string language, CancellationToken ct);
}

/// <summary>
/// 离线语音识别（Whisper.net + 本地 ggml 模型，不联网、不上传）。
/// 注意：Whisper 只接受 16kHz 单声道，因此先用 <see cref="AudioDecoder"/> 解码重采样再喂 float[]。
/// </summary>
public sealed class WhisperTranscriber : ITranscriber
{
    private readonly WhisperFactory _factory;
    private readonly SemaphoreSlim _gate;

    public string Name => "Whisper.net (离线)";
    public string ModelName { get; }
    public string ModelPath { get; }

    /// <summary>推理线程数（0 = 自动）。</summary>
    public int Threads { get; set; }

    /// <summary>
    /// 提示词：中文场景下强烈建议带上，可显著减少繁体输出与胡言乱语
    /// （Whisper 在没有提示时会随机输出繁体）。
    /// </summary>
    public string Prompt { get; set; } = "以下是普通话的句子，请用简体中文转写。";

    public WhisperTranscriber(string modelPath, int concurrency = 1, int threads = 0)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("找不到 Whisper 模型文件（ggml-*.bin）", modelPath);
        ModelPath = modelPath;
        ModelName = System.IO.Path.GetFileNameWithoutExtension(modelPath);
        _factory = CreateFactory(modelPath);
        _gate = new SemaphoreSlim(Math.Max(1, concurrency));
        Threads = threads > 0 ? threads : Math.Clamp(Environment.ProcessorCount - 2, 2, 6);
    }

    /// <summary>
    /// 加载模型。Whisper 的原生库用窄字符路径打开文件，**遇到中文/非 ASCII 路径会直接抛 SEH 异常**
    /// （"External component has thrown an exception"）。所以先试 FromPath，失败就退回把文件读进内存再加载。
    /// </summary>
    private static WhisperFactory CreateFactory(string modelPath)
    {
        try
        {
            return WhisperFactory.FromPath(modelPath);
        }
        catch (Exception ex) when (ex is SEHException or DllNotFoundException or BadImageFormatException
                                   || ex.Message.Contains("External component", StringComparison.OrdinalIgnoreCase))
        {
            // 非 ASCII 路径兜底：读进内存（约等于模型大小）后用 buffer 加载
            byte[] bytes = File.ReadAllBytes(modelPath);
            return WhisperFactory.FromBuffer(bytes);
        }
    }

    public async Task<TranscriptResult> TranscribeAsync(string wavPath, string language, CancellationToken ct)
    {
        var samples = AudioDecoder.DecodeMono16k(wavPath, out var info);
        if (samples.Length < AudioDecoder.TargetSampleRate / 10) // 少于 0.1 秒视为无内容
            return new TranscriptResult { Text = "", Language = language, Model = ModelName };

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var segments = new List<TranscriptSegment>();
            var sb = new StringBuilder();

            var builder = _factory.CreateBuilder()
                .WithLanguage(string.IsNullOrWhiteSpace(language) ? "zh" : language)
                .WithThreads(Threads)
                .WithNoContext();
            if (!string.IsNullOrWhiteSpace(Prompt) && (language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(language)))
                builder = builder.WithPrompt(Prompt);
            using var processor = builder.Build();

            await foreach (var seg in processor.ProcessAsync(samples, ct).ConfigureAwait(false))
            {
                string text = (seg.Text ?? string.Empty).Trim();
                if (text.Length == 0) continue;
                // Whisper 中文常夹繁体，统一转简体（ToolGood.Words）
                text = ToSimplified(text);
                segments.Add(new TranscriptSegment(seg.Start.TotalSeconds, seg.End.TotalSeconds, text));
                sb.Append(text);
            }

            return new TranscriptResult
            {
                Text = ToSimplified(sb.ToString().Trim()),
                Segments = segments,
                Language = language,
                Model = ModelName
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _factory.Dispose();
        _gate.Dispose();
    }

    /// <summary>繁体 → 简体（失败时原样返回，不影响识别）。</summary>
    private static string ToSimplified(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        try { return ToolGood.Words.WordsHelper.ToSimplifiedChinese(text); }
        catch { return text; }
    }
}
