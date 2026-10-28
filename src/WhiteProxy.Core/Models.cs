using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhiteProxy.Core;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
}

public sealed class Configuration
{
    public int SchemaVersion { get; set; } = 2;
    public long Revision { get; set; }
    public ProxySettings Proxy { get; set; } = new();
    public List<Device> Devices { get; set; } = [];
    public List<ServiceDefinition> Services { get; set; } = [];
    public List<AccessPolicy> Policies { get; set; } = [];
    public List<TemporaryGrant> TemporaryGrants { get; set; } = [];
}

public sealed class ProxySettings
{
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 3128;
    public bool GlobalBlock { get; set; }
    public string TimeZoneId { get; set; } = "Tokyo Standard Time";
    public int MaximumGrantHours { get; set; } = 4;
    public int LogRetentionDays { get; set; } = 30;
    public int MaxConnections { get; set; } = 256;
}

public sealed class Device
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新しい端末";
    public string Address { get; set; } = "192.168.10.21/32";
    public string Group { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool Paused { get; set; }
}

public sealed class ServiceDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新しいサービス";
    public bool Enabled { get; set; } = true;
    public bool BuiltIn { get; set; }
    public List<string> Hosts { get; set; } = [];
    public List<int> AllowedPorts { get; set; } = [80, 443];
    public string Source { get; set; } = "";
    public string DefinitionVersion { get; set; } = "2026-10-26";
    public string Notes { get; set; } = "";
}

public sealed class AccessPolicy
{
    public string DeviceId { get; set; } = "";
    public string ServiceId { get; set; } = "";
    public bool Deny { get; set; }
    public bool AlwaysAllow { get; set; }
    public List<RecurringSchedule> Schedules { get; set; } = [];
}

public sealed class RecurringSchedule
{
    public bool Enabled { get; set; } = true;
    public List<DayOfWeek> Days { get; set; } = [];
    public TimeOnly Start { get; set; } = new(12, 0);
    public TimeOnly End { get; set; } = new(13, 0);
}

public sealed class TemporaryGrant
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DeviceId { get; set; } = "";
    public string ServiceId { get; set; } = "";
    public DateTimeOffset StartsAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = "";
    public string Reason { get; set; } = "";
    public bool Revoked { get; set; }
}

public sealed record Decision(bool Allowed, string Reason, string? DeviceId = null,
    string? ServiceId = null, DateTimeOffset? ExpiresAtUtc = null);
