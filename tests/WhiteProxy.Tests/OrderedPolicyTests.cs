using System.Net;
using WhiteProxy.Core;

namespace WhiteProxy.Tests;

public class OrderedPolicyTests
{
    private readonly ManualClock clock = new();
    private Configuration Config() => new()
    {
        Devices = [new() { Id = "subnet", Address = "192.168.10.0/24" }, new() { Id = "pc", Address = "192.168.10.21/32" }],
        Services = [new() { Id = "web", Hosts = ["example.com"] }],
        Policies = [new() { DeviceId = "subnet", ServiceId = "web", Deny = true }, new() { DeviceId = "pc", ServiceId = "web", AlwaysAllow = true }]
    };
    private Decision Evaluate(Configuration c, string ip = "192.168.10.21", string host = "example.com")
        => new PolicyEngine(clock).Evaluate(c, IPAddress.Parse(ip), host, 443);

    [Fact]
    public void FirstSubnetDenyWinsAndMovingHostAboveItAllowsOnlyThatHost()
    {
        var c = Config(); Assert.Empty(ConfigurationValidator.Validate(c));
        Assert.False(Evaluate(c).Allowed);
        c.Policies.Reverse();
        Assert.True(Evaluate(c).Allowed);
        Assert.False(Evaluate(c, "192.168.10.22").Allowed);
        Assert.False(Evaluate(c, "192.168.11.21").Allowed);
        Assert.False(Evaluate(c, host: "unknown.example").Allowed);
        c.Proxy.GlobalBlock = true; Assert.False(Evaluate(c).Allowed);
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("expired")]
    [InlineData("reserved")]
    [InlineData("paused")]
    [InlineData("disabled")]
    public void InactiveFirstRuleFallsThroughToAlwaysAllow(string mode)
    {
        var c = Config(); c.Policies.Reverse(); c.Policies[1].Deny = false; c.Policies[1].AlwaysAllow = true;
        c.Policies[0].AlwaysAllow = false;
        if (mode == "outside") c.Policies[0].Schedules.Add(new() { Days = [DayOfWeek.Monday], Start = new(13, 0), End = new(14, 0) });
        if (mode == "expired") c.TemporaryGrants.Add(new() { DeviceId = "pc", ServiceId = "web", StartsAtUtc = clock.UtcNow.AddHours(-1), ExpiresAtUtc = clock.UtcNow });
        if (mode == "reserved") c.TemporaryGrants.Add(new() { DeviceId = "pc", ServiceId = "web", StartsAtUtc = clock.UtcNow.AddMinutes(1), ExpiresAtUtc = clock.UtcNow.AddHours(1) });
        if (mode == "paused") c.Devices[1].Paused = true;
        if (mode == "disabled") c.Devices[1].Enabled = false;
        var result = Evaluate(c);
        Assert.True(result.Allowed);
        Assert.Equal("subnet", result.DeviceId);
    }

    [Fact]
    public void SamePcCanUseMultipleServicesAndExpiredOverlapFallsThrough()
    {
        var c = Config(); c.Policies.Clear();
        c.Services.Add(new() { Id = "office", Hosts = ["office.example", "example.com"] });
        c.Policies = [new() { DeviceId = "pc", ServiceId = "web" }, new() { DeviceId = "pc", ServiceId = "office", AlwaysAllow = true }];
        c.TemporaryGrants.Add(new() { DeviceId = "pc", ServiceId = "web", StartsAtUtc = clock.UtcNow, ExpiresAtUtc = clock.UtcNow.AddHours(1) });
        Assert.Equal("web", Evaluate(c).ServiceId);
        Assert.Equal("office", Evaluate(c, host: "office.example").ServiceId);
        clock.UtcNow = clock.UtcNow.AddHours(1);
        var fallback = Evaluate(c);
        Assert.True(fallback.Allowed); Assert.Equal("office", fallback.ServiceId);
        c.Policies[1].AlwaysAllow = false;
        Assert.False(Evaluate(c).Allowed);
        Assert.False(Evaluate(c, host: "office.example").Allowed);
    }

    [Fact]
    public void LowerTemporaryGrantCannotBypassFirstDeny()
    {
        var c = Config(); c.TemporaryGrants.Add(new() { DeviceId = "pc", ServiceId = "web", StartsAtUtc = clock.UtcNow, ExpiresAtUtc = clock.UtcNow.AddHours(1) });
        Assert.False(Evaluate(c).Allowed);
        c.Policies.Reverse(); Assert.True(Evaluate(c).Allowed);
        c.Policies[0].AlwaysAllow = false; clock.UtcNow = clock.UtcNow.AddHours(1);
        Assert.False(Evaluate(c).Allowed);
    }

    [Fact]
    public void MatchingIncludesDestinationServiceAndSupportsIdenticalSourceNetworks()
    {
        var c = Config(); c.Devices[1].Address = c.Devices[0].Address;
        c.Services.Add(new() { Id = "other", Hosts = ["other.example"] });
        c.Policies[0].ServiceId = "other";
        Assert.Empty(ConfigurationValidator.Validate(c));
        Assert.True(Evaluate(c).Allowed);
        Assert.False(Evaluate(c, host: "other.example").Allowed);
        c.Policies[0].ServiceId = "web"; Assert.False(Evaluate(c).Allowed);
    }

    [Fact]
    public void OverlappingDestinationDefinitionsFollowRuleOrderWhileServiceDisableStillBlocks()
    {
        var c = Config(); c.Policies.Reverse();
        c.Services.Add(new() { Id = "overlap", Hosts = ["example.com"] }); c.Policies[1].ServiceId = "overlap";
        Assert.True(Evaluate(c).Allowed);
        c.Policies.Reverse(); Assert.False(Evaluate(c).Allowed);
        c.Policies.Reverse(); c.Services[1].Enabled = false; Assert.False(Evaluate(c).Allowed);
    }

    [Fact]
    public void LegacyMigrationKeepsDenyAheadOfAllowAndPersistsNewSemanticsVersion()
    {
        var legacy = Config(); legacy.SchemaVersion = 1; legacy.Policies.Reverse();
        var upgraded = RuleOrder.Upgrade(legacy);
        Assert.Equal(1, legacy.SchemaVersion); Assert.Equal(2, upgraded.SchemaVersion);
        Assert.True(upgraded.Policies[0].Deny); Assert.False(Evaluate(upgraded).Allowed);
        Assert.NotEmpty(ConfigurationValidator.Validate(legacy));
        Assert.Empty(ConfigurationValidator.Validate(upgraded));
        var reopened = JsonDefaults.Clone(upgraded);
        Assert.Equal("subnet", reopened.Policies[0].DeviceId);
    }
}
