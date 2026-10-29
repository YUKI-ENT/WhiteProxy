using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using WhiteProxy.Core;

namespace WhiteProxy.Shared;

public static class Protocol
{
    public const string OrderedRulesCapability = "active-ordered-rules-v3";
    public const string ReadPipe = "WhiteProxy.Read.v1";
    public const string AdminPipe = "WhiteProxy.Admin.v1";
    public const string ServiceName = "WhiteProxy";
    public const int MaxMessageBytes = 4 * 1024 * 1024;
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonDefaults.Options);
        if (bytes.Length > MaxMessageBytes) throw new InvalidDataException("IPCメッセージが大きすぎます。");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, ct); await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, ct);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > MaxMessageBytes) throw new InvalidDataException("IPCサイズが不正です。");
        var body = new byte[length]; await stream.ReadExactlyAsync(body, ct);
        return JsonSerializer.Deserialize<T>(body, JsonDefaults.Options) ?? throw new InvalidDataException("IPCが空です。");
    }

    public static async Task WriteResponseAsync(Stream stream, IpcResponse response, CancellationToken ct)
    {
        await WriteAsync(stream, response, ct);
        // DisconnectNamedPipeは未読の応答を破棄する。FlushAsyncだけでは消費を待たない。
        // クライアントは応答全体を読み終えてDisposeするので、そのEOFまで待つ。
        if (await stream.ReadAsync(new byte[1], ct) != 0)
            throw new InvalidDataException("1接続につき1要求だけ受け付けます。");
    }
}

public sealed class IpcRequest
{
    public string Operation { get; set; } = "status";
    public Configuration? Configuration { get; set; }
    public long ExpectedRevision { get; set; }
    public bool Block { get; set; }
    public List<string> DeviceIds { get; set; } = [];
    public string ServiceId { get; set; } = "";
    public DateTimeOffset? StartsAtUtc { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public string Reason { get; set; } = "";
    public string GrantId { get; set; } = "";
    public WebControlChange? WebControl { get; set; }
}

public sealed class IpcResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public Configuration? Configuration { get; set; }
    public ServiceStatus? Status { get; set; }
    public List<LogEntry> Logs { get; set; } = [];
    public WebControlStatus? WebControl { get; set; }
}

public sealed class WebControlChange
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 3129;
    public string Password { get; set; } = "";
    public long ExpectedRevision { get; set; }
}

public sealed record WebControlStatus(bool Enabled, int Port, bool PasswordConfigured, long Revision,
    bool Running = false, string Error = "", string CertificateFingerprint = "", IReadOnlyList<string>? Urls = null);

public sealed record ServiceStatus(bool Running, string Listen, int Connections, int Devices,
    long Denied, bool Healthy, string Error, DateTimeOffset TimeUtc, long DroppedLogs);

public sealed record LogEntry(DateTimeOffset TimeUtc, string Kind, string Peer, string Host, int Port,
    bool Allowed, string Reason, string? ServiceId = null, long Bytes = 0, double Seconds = 0, string Actor = "",
    string RequestPath = "", bool IsTunnel = false);

public sealed class PipeClient
{
    public async Task<IpcResponse> SendAsync(IpcRequest request, bool admin = false, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(admin ? 50 : 12));
        using var pipe = new NamedPipeClientStream(".", admin ? Protocol.AdminPipe : Protocol.ReadPipe,
            PipeDirection.InOut, PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Impersonation);
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
        {
            connectTimeout.CancelAfter(TimeSpan.FromMilliseconds(1500));
            await pipe.ConnectAsync(connectTimeout.Token);
        }
        await Protocol.WriteAsync(pipe, request, timeout.Token);
        return await Protocol.ReadAsync<IpcResponse>(pipe, timeout.Token);
    }
}
