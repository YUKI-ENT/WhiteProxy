using System.Net;
using WhiteProxy.Core;

namespace WhiteProxy.Tests;

public class PolicyTests
{
    [Theory]
    [InlineData("a.example.com", "*.example.com", true)]
    [InlineData("example.com", "*.example.com", false)]
    [InlineData("evil-example.com", "*.example.com", false)]
    [InlineData("example.com.evil.test", "example.com", false)]
    [InlineData("EXAMPLE.COM.", "example.com", true)]
    [InlineData("例え.テスト", "xn--r8jz45g.xn--zckzah", true)]
    [InlineData("::ffff:127.0.0.1", "127.0.0.1", true)]
    [InlineData("127.0.0.1", "*.0.0.1", false)]
    public void DomainBoundaryAndNormalization(string host, string pattern, bool expected)
        => Assert.Equal(expected, HostRules.Matches(host, pattern));

    [Theory]
    [InlineData("example.com/secret")]
    [InlineData("example.com?secret")]
    [InlineData("user@example.com")]
    [InlineData("example.com:443")]
    [InlineData("*.example.com")]
    [InlineData(" example.com")]
    [InlineData("fe80::1%5")]
    public void MalformedHostRejected(string host) => Assert.Throws<ArgumentException>(() => HostRules.Normalize(host));

    [Theory]
    [InlineData("192.168.10.5", true)]
    [InlineData("192.168.11.5", false)]
    [InlineData("::ffff:192.168.10.5", true)]
    [InlineData("::1", false)]
    public void PeerNetworkNormalization(string peer, bool expected)
        => Assert.Equal(expected, Ipv4Network.Parse("192.168.10.0/24").Contains(IPAddress.Parse(peer)));

    private static Configuration Scheduled(ManualClock clock, DayOfWeek[] days, TimeOnly start, TimeOnly end)
    {
        var c = ProxyTests.Config(3128, 443, clock); c.TemporaryGrants.Clear();
        c.Services[0].Hosts = ["example.com"];
        c.Policies = [new() { DeviceId = "pc", ServiceId = "web", Schedules = [new() { Days = days.ToList(), Start = start, End = end }] }];
        return c;
    }
    private static Decision Evaluate(Configuration c, ManualClock clock, string host = "example.com", int port = 443, string peer = "127.0.0.1")
        => new PolicyEngine(clock).Evaluate(c, IPAddress.Parse(peer), host, port);

    [Theory]
    [InlineData("2026-10-26T02:59:59Z", false)]
    [InlineData("2026-10-26T03:00:00Z", true)]
    [InlineData("2026-10-26T03:59:59Z", true)]
    [InlineData("2026-10-26T04:00:00Z", false)]
    [InlineData("2026-10-25T03:30:00Z", false)]
    public void WeekdayHalfOpenBoundaries(string utc, bool expected)
    {
        var clock = new ManualClock { UtcNow = DateTimeOffset.Parse(utc) };
        var c = Scheduled(clock, [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday], new(12, 0), new(13, 0));
        Assert.Equal(expected, Evaluate(c, clock).Allowed);
    }

    [Theory]
    [InlineData("2026-10-30T13:59:59Z", false)]
    [InlineData("2026-10-30T14:00:00Z", true)]
    [InlineData("2026-10-30T16:59:59Z", true)]
    [InlineData("2026-10-30T17:00:00Z", false)]
    [InlineData("2026-10-31T14:00:00Z", false)]
    public void OvernightUsesStartingWeekday(string utc, bool expected)
    {
        var clock = new ManualClock { UtcNow = DateTimeOffset.Parse(utc) };
        var c = Scheduled(clock, [DayOfWeek.Friday], new(23, 0), new(2, 0));
        Assert.Equal(expected, Evaluate(c, clock).Allowed);
    }

    [Fact]
    public void ExpiredGrantStaysExpiredAfterSerializationAndRestart()
    {
        var clock = new ManualClock(); var c = ProxyTests.Config(3128, 443, clock);
        Assert.True(Evaluate(c, clock, "127.0.0.1").Allowed);
        clock.UtcNow = clock.UtcNow.AddMinutes(89); Assert.True(Evaluate(c, clock, "127.0.0.1").Allowed);
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        Assert.False(Evaluate(JsonDefaults.Clone(c), clock, "127.0.0.1").Allowed);
    }

    [Fact]
    public void RecurringGrantContinuesAfterTemporaryGrantAndLongestExpiryWins()
    {
        var clock = new ManualClock(); var c = Scheduled(clock, [DayOfWeek.Monday], new(12, 0), new(15, 0));
        c.TemporaryGrants.Add(new() { DeviceId = "pc", ServiceId = "web", StartsAtUtc = clock.UtcNow, ExpiresAtUtc = clock.UtcNow.AddMinutes(90) });
        var result = Evaluate(c, clock);
        Assert.True(result.Allowed); Assert.Equal(clock.UtcNow.AddHours(3), result.ExpiresAtUtc);
        clock.UtcNow = clock.UtcNow.AddMinutes(90); Assert.True(Evaluate(c, clock).Allowed);
        c.Policies[0].AlwaysAllow = true; Assert.Null(Evaluate(c, clock).ExpiresAtUtc);
    }

    [Theory]
    [InlineData("global")]
    [InlineData("device")]
    [InlineData("pause")]
    [InlineData("service")]
    [InlineData("deny")]
    [InlineData("unknown-device")]
    [InlineData("unknown-host")]
    [InlineData("port")]
    [InlineData("empty-hosts")]
    [InlineData("revoked")]
    public void DenyOverridesGrant(string mode)
    {
        var clock = new ManualClock(); var c = ProxyTests.Config(3128, 443, clock);
        switch (mode)
        {
            case "global": c.Proxy.GlobalBlock = true; break;
            case "device": c.Devices[0].Enabled = false; break;
            case "pause": c.Devices[0].Paused = true; break;
            case "service": c.Services[0].Enabled = false; break;
            case "deny": c.Policies.Add(new() { DeviceId = "pc", ServiceId = "web", Deny = true, AlwaysAllow = true }); break;
            case "unknown-device": c.Devices.Clear(); break;
            case "unknown-host": c.Services[0].Hosts = ["other.example"]; break;
            case "port": c.Services[0].AllowedPorts = [80]; break;
            case "empty-hosts": c.Services[0].Hosts.Clear(); break;
            case "revoked": c.TemporaryGrants[0].Revoked = true; break;
        }
        Assert.False(Evaluate(c, clock, "127.0.0.1").Allowed);
    }

    [Fact]
    public void ConfigurationAllowsOverlapButRejectsUnknownReferencesAndEqualScheduleTimes()
    {
        var clock = new ManualClock(); var c = Scheduled(clock, [DayOfWeek.Monday], new(12, 0), new(12, 0));
        c.Devices.Add(new() { Id = "pc2", Address = "127.0.0.0/8" });
        c.Policies.Add(new() { DeviceId = "not-there", ServiceId = "web" });
        Assert.True(ConfigurationValidator.Validate(c).Count >= 2);
    }

    [Fact]
    public void DstAmbiguousAndMissingBoundarySlotsFailClosed()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var ambiguous = new RecurringSchedule { Days = [DayOfWeek.Sunday], Start = new(1, 30), End = new(2, 30) };
        Assert.DoesNotContain(PolicyEngine.Intervals(ambiguous, zone, DateTimeOffset.Parse("2026-11-01T05:30:00Z")),
            i => i.Start.UtcDateTime.Date == new DateTime(2026, 11, 1));
        var missing = new RecurringSchedule { Days = [DayOfWeek.Sunday], Start = new(2, 30), End = new(3, 30) };
        Assert.DoesNotContain(PolicyEngine.Intervals(missing, zone, DateTimeOffset.Parse("2026-03-08T07:30:00Z")),
            i => i.Start.UtcDateTime.Date == new DateTime(2026, 3, 8));
    }

    [Fact]
    public void ClockGuardDetectsReversalJumpAndPersistedFuture()
    {
        var clock = new ManualClock(); var guard = new ClockGuard(clock);
        Assert.True(guard.Check()); clock.UtcNow = clock.UtcNow.AddMinutes(-1); Assert.False(guard.Check());
        clock.UtcNow = clock.UtcNow.AddMinutes(1); Assert.False(guard.Check());
        Assert.False(new ClockGuard(clock, clock.UtcNow.AddMinutes(1)).Healthy);
        guard = new ClockGuard(clock); clock.UtcNow = clock.UtcNow.AddHours(1); Assert.False(guard.Check());
    }
}
