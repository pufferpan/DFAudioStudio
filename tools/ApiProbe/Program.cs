using System.Reflection;

// 列出 Whisper.net 1.8.1 的关键 API，用于决定识别输入方式（float[] 还是 16k WAV 流）
var asm = typeof(Whisper.net.WhisperFactory).Assembly;
Console.WriteLine("Assembly: " + asm.FullName);

foreach (var typeName in new[]
         {
             "Whisper.net.WhisperProcessor",
             "Whisper.net.WhisperProcessorBuilder",
             "Whisper.net.WhisperFactory",
             "Whisper.net.SegmentData",
             "Whisper.net.WhisperFactoryOptions"
         })
{
    var t = asm.GetType(typeName);
    Console.WriteLine($"\n=== {typeName} : {(t is null ? "未找到" : "OK")} ===");
    if (t is null) continue;

    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
    {
        if (m.IsSpecialName) continue;
        var ps = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
        Console.WriteLine($"  {m.ReturnType.Name} {m.Name}({ps})");
    }
    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        Console.WriteLine($"  prop {p.PropertyType.Name} {p.Name}");
}

Console.WriteLine("\n=== 全部类型(含 Sample/Rate 关键字) ===");
foreach (var t in asm.GetExportedTypes().Where(x => x.Name.Contains("Sample") || x.Name.Contains("Rate") || x.Name.Contains("Processor")))
    Console.WriteLine("  " + t.FullName);
