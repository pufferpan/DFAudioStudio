#if ALPHA_EXPIRY
using System.Net;
using System.Net.Sockets;

namespace DFAudioStudio.App.Services;

/// <summary>时间校验结果状态。</summary>
public enum TimeCheckStatus
{
    /// <summary>校验通过：系统时间与 NTP 一致，且未过期。</summary>
    Ok,
    /// <summary>已超过 2026-09-24 00:00（北京时间）过期时刻。</summary>
    Expired,
    /// <summary>系统时间与 NTP 服务器不一致（疑似修改过系统时间）。</summary>
    ClockMismatch,
    /// <summary>所有 NTP 服务器均不可达，无法完成校验。</summary>
    Unreachable,
}

/// <summary>一次时间校验的完整结果，供界面提示与日志排查使用。</summary>
public sealed class TimeCheckResult
{
    public required TimeCheckStatus Status { get; init; }
    public required DateTimeOffset LocalUtc { get; init; }
    public DateTimeOffset? NtpUtc { get; init; }
    /// <summary>NTP 时间 − 本机时间（正值表示本机时间偏慢）。</summary>
    public TimeSpan? Offset { get; init; }
    public string? Server { get; init; }
    public string Detail { get; init; } = "";
    public int RespondedServers { get; init; }
    public int QueriedServers { get; init; }

    public bool IsOk => Status == TimeCheckStatus.Ok;
}

/// <summary>
/// Alpha 定时过期版的授权时间校验：直接向多个公网 NTP 服务器取时（SNTP，RFC 4330），
/// 而不是信任本机时钟，从而同时完成两件事：
///   1. 判断当前网络时间是否已过 2026-09-24 00:00（北京时间）的过期时刻；
///   2. 判断本机系统时间与网络时间是否一致（防止改系统时间绕过过期）。
/// 任一环节失败都不允许使用，界面提示统一为 <see cref="ExpiryMessage"/>。
/// </summary>
public static class TimeGuard
{
    /// <summary>正式过期时刻：北京时间 2026-09-24 00:00（= 2026-09-23 16:00 UTC）。</summary>
    public static readonly DateTimeOffset ExpiryUtc =
        new(2026, 9, 24, 0, 0, 0, TimeSpan.FromHours(8));

    /// <summary>统一的过期提示文案（不改写、不拼接）。</summary>
    public const string ExpiryMessage =
        "此版本为Alpha测试版本。于2026年9月24日正式过期，请等待正式版本发布";

    /// <summary>允许的系统时间偏差：超过该值即判定「系统时间与 NTP 不一致」。</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>单个 NTP 服务器的等待上限（全部服务器并发查询，总耗时约等于该值）。</summary>
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(3);

    /// <summary>公网 NTP 服务器池：国内优先（可用性最好），国际服务器兜底与交叉验证。</summary>
    private static readonly string[] Servers =
    {
        "ntp.aliyun.com",       // 阿里云
        "ntp1.aliyun.com",
        "ntp.tencent.com",      // 腾讯云
        "cn.pool.ntp.org",      // 中国 NTP 池
        "ntp.ntsc.ac.cn",       // 国家授时中心
        "time.windows.com",     // 微软
        "time.apple.com",       // 苹果
        "pool.ntp.org",         // 国际 NTP 池
    };

    private const int NtpPort = 123;

    /// <summary>NTP 时间戳纪元：1900-01-01 UTC（era 0，有效至 2036 年）。</summary>
    private static readonly DateTimeOffset NtpEpoch = new(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>最近一次校验结果（供界面角标显示剩余时间）。</summary>
    public static TimeCheckResult? LastResult { get; private set; }

    /// <summary>日志出口：由 App 接到应用自身日志；未设置时写到 %LOCALAPPDATA%\DFAudioStudio\logs\alpha-guard.log。</summary>
    public static Action<string>? LogSink { get; set; }

    /// <summary>并发查询所有 NTP 服务器并给出校验结论（不抛出异常），同时记录到 <see cref="LastResult"/>。</summary>
    public static async Task<TimeCheckResult> CheckAsync(CancellationToken ct = default)
    {
        var result = ApplyForceBlockOverride(await CheckCoreAsync(ct));
        LastResult = result;
        return result;
    }

    /// <summary>
    /// 界面自测开关：环境变量 <c>DMV_ALPHA_FORCE_BLOCK=Expired|ClockMismatch|Unreachable</c>
    /// 可强制走拦截界面（用于提前确认提示样式）。
    /// 该开关只会让校验更严格——只会拦截、不会放行，因此不构成绕过时效的手段。
    /// </summary>
    private static TimeCheckResult ApplyForceBlockOverride(TimeCheckResult result)
    {
        var forced = Environment.GetEnvironmentVariable("DMV_ALPHA_FORCE_BLOCK");
        if (string.IsNullOrWhiteSpace(forced)) return result;

        TimeCheckStatus? status = forced.Trim().ToLowerInvariant() switch
        {
            "expired" or "1" or "true" => TimeCheckStatus.Expired,
            "clockmismatch" or "mismatch" => TimeCheckStatus.ClockMismatch,
            "unreachable" or "offline" => TimeCheckStatus.Unreachable,
            _ => null,
        };
        if (status is null) return result;

        LogLine($"[自测开关] DMV_ALPHA_FORCE_BLOCK={forced}：强制按 {status} 处理");
        return new TimeCheckResult
        {
            Status = status.Value,
            LocalUtc = result.LocalUtc,
            NtpUtc = result.NtpUtc,
            Offset = result.Offset,
            Server = result.Server,
            Detail = $"【自测开关 DMV_ALPHA_FORCE_BLOCK】模拟 {status}；真实情况：{result.Detail}",
            RespondedServers = result.RespondedServers,
            QueriedServers = result.QueriedServers,
        };
    }

    /// <summary>
    /// 判定结论（纯函数，便于按任意时间点复算与自测）：
    /// 先判「系统时间与 NTP 是否一致」，再判「网络时间是否已过期」。
    /// </summary>
    public static TimeCheckStatus Evaluate(DateTimeOffset ntpNowUtc, TimeSpan drift)
    {
        if (drift.Duration() > MaxClockSkew) return TimeCheckStatus.ClockMismatch;
        if (ntpNowUtc >= ExpiryUtc) return TimeCheckStatus.Expired;
        return TimeCheckStatus.Ok;
    }

    /// <summary>
    /// 命令行诊断：DFAudioStudio.App.exe --timecheck [报告文件] [--at ISO时间] [--drift 秒数]，
    /// 把真实校时结果写入文件与日志；--at/--drift 仅用于按指定时间点复算判定结论（边界自测）。
    /// </summary>
    public static async Task<int> RunDiagnosticAsync(string? reportPath,
        DateTimeOffset? simulateAt = null, TimeSpan? simulateDrift = null)
    {
        var result = await CheckAsync();
        var path = string.IsNullOrWhiteSpace(reportPath)
            ? Path.Combine(Path.GetTempPath(), "dfaudio_timecheck.txt")
            : reportPath;

        var lines = new List<string>
        {
            $"app         = DFAudioStudio Alpha",
            $"status      = {result.Status}",
            $"localUtc    = {result.LocalUtc:u}",
            $"ntpUtc      = {(result.NtpUtc?.ToString("u") ?? "(无)")}",
            $"offsetSec   = {(result.Offset?.TotalSeconds.ToString("F3") ?? "(无)")}",
            $"server      = {result.Server ?? "(无)"}",
            $"responded   = {result.RespondedServers}/{result.QueriedServers}",
            $"detail      = {result.Detail}",
            $"message     = {ExpiryMessage}",
            $"expiryUtc   = {ExpiryUtc:u} (北京时间 2026-09-24 00:00)",
        };

        if (simulateAt is not null || simulateDrift is not null)
        {
            var at = (simulateAt ?? result.NtpUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
            var drift = simulateDrift ?? result.Offset ?? TimeSpan.Zero;
            lines.Add($"simulateAt  = {at:u}");
            lines.Add($"simulateDriftSec = {drift.TotalSeconds:F3}");
            lines.Add($"simulateVerdict  = {Evaluate(at, drift)}");
        }

        try
        {
            File.WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
        }
        catch (Exception ex)
        {
            LogLine("写 --timecheck 报告失败：" + ex.Message);
        }
        foreach (var line in lines) LogLine(line);
        return result.IsOk ? 0 : 3;
    }

    // ---------- SNTP 查询 ----------

    private sealed record NtpSample(string Server, IPAddress Address, int Stratum, TimeSpan Offset, TimeSpan RoundTrip);

    private static async Task<TimeCheckResult> CheckCoreAsync(CancellationToken ct)
    {
        var localAtStart = DateTimeOffset.UtcNow;

        NtpSample?[] samples;
        try
        {
            samples = await Task.WhenAll(Servers.Select(s => QueryAsync(s, ct)));
        }
        catch (Exception ex)
        {
            LogLine("校时异常：" + ex.Message);
            samples = Array.Empty<NtpSample?>();
        }

        var valid = samples.Where(s => s is not null).Select(s => s!).ToList();
        foreach (var s in samples)
        {
            if (s is null) continue;
            LogLine($"校时[{s.Server}] stratum={s.Stratum} 往返={s.RoundTrip.TotalMilliseconds:F0}ms " +
                    $"偏差={s.Offset.TotalSeconds:+0.000;-0.000;0.000}s");
        }

        if (valid.Count == 0)
        {
            LogLine($"校时失败：{Servers.Length} 个 NTP 服务器均无有效响应");
            return new TimeCheckResult
            {
                Status = TimeCheckStatus.Unreachable,
                LocalUtc = localAtStart,
                Detail = $"无法连接 NTP 服务器完成时间校验（已尝试 {Servers.Length} 个，均无响应）",
                QueriedServers = Servers.Length,
            };
        }

        // 采用往返延迟最小的样本（受网络抖动影响最小），其余样本仅作交叉验证记录。
        var best = valid.OrderBy(s => s.RoundTrip).First();
        var ntpNow = DateTimeOffset.UtcNow + best.Offset;
        var drift = best.Offset;

        if (Evaluate(ntpNow, drift) == TimeCheckStatus.ClockMismatch)
        {
            LogLine($"校时失败：系统时间与 {best.Server} 相差 {drift.TotalSeconds:F0}s");
            return new TimeCheckResult
            {
                Status = TimeCheckStatus.ClockMismatch,
                LocalUtc = localAtStart,
                NtpUtc = ntpNow,
                Offset = drift,
                Server = best.Server,
                Detail = $"系统时间与 NTP 服务器（{best.Server}）相差 {FormatSpan(drift.Duration())}，时间校验未通过",
                RespondedServers = valid.Count,
                QueriedServers = Servers.Length,
            };
        }

        if (Evaluate(ntpNow, drift) == TimeCheckStatus.Expired)
        {
            LogLine($"已过期（网络时间 {ntpNow:u} ≥ {ExpiryUtc:u}）");
            return new TimeCheckResult
            {
                Status = TimeCheckStatus.Expired,
                LocalUtc = localAtStart,
                NtpUtc = ntpNow,
                Offset = drift,
                Server = best.Server,
                Detail = $"网络时间 {ntpNow.ToOffset(TimeSpan.FromHours(8)):yyyy-MM-dd HH:mm} 已超过过期时刻 2026-09-24 00:00",
                RespondedServers = valid.Count,
                QueriedServers = Servers.Length,
            };
        }

        LogLine($"校时通过：server={best.Server} offset={drift.TotalSeconds:F3}s 剩余={FormatSpan(ExpiryUtc - ntpNow)}");
        return new TimeCheckResult
        {
            Status = TimeCheckStatus.Ok,
            LocalUtc = localAtStart,
            NtpUtc = ntpNow,
            Offset = drift,
            Server = best.Server,
            Detail = $"网络时间校验通过（{best.Server}），距过期还有 {FormatSpan(ExpiryUtc - ntpNow)}",
            RespondedServers = valid.Count,
            QueriedServers = Servers.Length,
        };
    }

    /// <summary>查询单个服务器：返回 null 表示超时/无响应/响应不可信。</summary>
    private static async Task<NtpSample?> QueryAsync(string server, CancellationToken ct)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(server, ct);
            var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                          ?? addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6);
            if (address is null) return null;

            var request = new byte[48];
            request[0] = 0x1B;                      // LI=0, VN=3, Mode=3（客户端）
            var t1 = DateTimeOffset.UtcNow;
            WriteTimestamp(request, 40, t1);        // 发送时刻

            using var udp = new UdpClient(address.AddressFamily);
            udp.Connect(new IPEndPoint(address, NtpPort));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(QueryTimeout);

            await udp.SendAsync(request, request.Length);
            // 直接把 token 交给 ReceiveAsync：超时会正常取消该次接收，不会留下挂起的废弃任务
            var response = await udp.ReceiveAsync(timeout.Token);
            var t4 = DateTimeOffset.UtcNow;         // 收到时刻

            var data = response.Buffer;
            if (data.Length < 48) return null;

            int leap = (data[0] >> 6) & 0x3;
            int mode = data[0] & 0x7;
            int stratum = data[1];
            if (mode != 4 || leap == 3 || stratum is 0 or > 15) return null;   // 非服务端响应 / 未同步 / Kiss-of-Death

            var origin = ReadTimestamp(data, 24);   // 回显的客户端发送时刻（防伪造校验）
            var t2 = ReadTimestamp(data, 32);       // 服务器接收时刻
            var t3 = ReadTimestamp(data, 40);       // 服务器发送时刻
            if (origin is null || t2 is null || t3 is null) return null;
            if ((origin.Value - t1).Duration() > TimeSpan.FromSeconds(1)) return null;

            var offset = new TimeSpan(((t2.Value - t1) + (t3.Value - t4)).Ticks / 2);
            var roundTrip = (t4 - t1) - (t3.Value - t2.Value);
            return new NtpSample(server, address, stratum, offset, roundTrip);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            LogLine($"校时[{server}] 查询失败：{ex.Message}");
            return null;
        }
    }

    private static void WriteTimestamp(byte[] buffer, int offset, DateTimeOffset time)
    {
        double seconds = (time - NtpEpoch).TotalSeconds;
        uint whole = (uint)Math.Floor(seconds);
        uint fraction = (uint)((seconds - whole) * 4294967296.0);
        WriteUInt32BigEndian(buffer, offset, whole);
        WriteUInt32BigEndian(buffer, offset + 4, fraction);
    }

    private static DateTimeOffset? ReadTimestamp(byte[] buffer, int offset)
    {
        uint whole = ReadUInt32BigEndian(buffer, offset);
        uint fraction = ReadUInt32BigEndian(buffer, offset + 4);
        if (whole == 0 && fraction == 0) return null;
        return NtpEpoch.AddSeconds(whole + fraction / 4294967296.0);
    }

    private static void WriteUInt32BigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint ReadUInt32BigEndian(byte[] buffer, int offset) =>
        ((uint)buffer[offset] << 24) | ((uint)buffer[offset + 1] << 16) |
        ((uint)buffer[offset + 2] << 8) | buffer[offset + 3];

    /// <summary>把时长格式化为「x天x小时x分」，用于提示文案。</summary>
    private static string FormatSpan(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays} 天 {span.Hours} 小时";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours} 小时 {span.Minutes} 分";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes} 分 {span.Seconds} 秒";
        return $"{span.TotalSeconds:F0} 秒";
    }

    /// <summary>日志：优先走应用自身日志（LogSink），否则落到 %LOCALAPPDATA%\DFAudioStudio\logs\alpha-guard.log。</summary>
    private static void LogLine(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [ALPHA] {message}";
        try
        {
            if (LogSink is not null)
            {
                LogSink(line);
                return;
            }
            var dir = Path.Combine(Core.Services.AppSettings.ResolveDataDir(), "logs");
            Directory.CreateDirectory(dir);
            lock (LogGate)
                File.AppendAllText(Path.Combine(dir, "alpha-guard.log"), line + Environment.NewLine);
        }
        catch
        {
            // 日志失败不影响校验
        }
    }

    private static readonly object LogGate = new();
}
#endif
