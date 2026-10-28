using System.Runtime.InteropServices;
using WhiteProxy.Admin;
using WhiteProxy.Core;

namespace WhiteProxy.Windows.Tests;

public class FirewallTests
{
    [Fact]
    public void PlanRestrictsProgramPortLocalIpAndRegisteredSources()
    {
        var config = new Configuration { Proxy = new() { ListenAddress = "192.168.10.10", Port = 8128 }, Devices = [
            new() { Address = "192.168.10.123/24" }, new() { Address = "192.168.20.21" }, new() { Address = "192.168.10.0/24" },
            new() { Address = "10.0.0.1", Enabled = false }, new() { Address = "127.0.0.1" }] };
        var plan = FirewallManager.Plan(config);
        Assert.True(plan.AllowInbound); Assert.Equal("192.168.10.10", plan.LocalAddress); Assert.Equal(8128, plan.Port);
        Assert.Equal(new[] { "192.168.10.0/24", "192.168.20.21/32" }, plan.Sources);
        Assert.Equal(3, FirewallPlan.Profiles); Assert.EndsWith(@"WhiteProxy\WhiteProxy.exe", plan.Executable);
    }

    [Theory]
    [InlineData("127.0.0.1", "192.168.10.0/24")]
    [InlineData("192.168.10.10", "0.0.0.0/0")]
    [InlineData("192.168.10.10", "127.0.0.1")]
    public void DoesNotAutomaticallyOpenUnboundedOrLoopbackScope(string listen, string source)
    {
        var plan = FirewallManager.Plan(new() { Proxy = new() { ListenAddress = listen }, Devices = [new() { Address = source }] });
        Assert.False(plan.AllowInbound); Assert.Empty(plan.Sources);
    }

    [Fact]
    public void AnyLocalBindStillRequiresLimitedRemoteSourcesAndEmptyDevicesDoNotOpen()
    {
        var config = new Configuration { Proxy = new() { ListenAddress = "0.0.0.0" } };
        Assert.False(FirewallManager.Plan(config).AllowInbound);
        config.Devices.Add(new() { Address = "192.168.10.21" });
        var plan = FirewallManager.Plan(config); Assert.Equal("*", plan.LocalAddress);
        Assert.Equal("192.168.10.21/32", Assert.Single(plan.Sources));
    }

    [Fact]
    public void WindowsAcceptsDetachedFirewallRuleWithoutChangingActiveFirewall()
    {
        var plan = FirewallManager.Plan(new() { Proxy = new() { ListenAddress = "192.168.10.10" }, Devices = [new() { Address = "192.168.10.0/24" }] });
        var instance = FirewallManager.CreateRule(plan);
        try
        {
            dynamic rule = instance;
            Assert.Equal(FirewallManager.RuleName, (string)rule.Name); Assert.Equal(6, (int)rule.Protocol); Assert.Equal(3, (int)rule.Profiles);
            Assert.Equal(1, (int)rule.Direction); Assert.Equal(1, (int)rule.Action); Assert.False((bool)rule.EdgeTraversal);
            Assert.Equal(plan.Executable, (string)rule.ApplicationName); Assert.Equal("3128", (string)rule.LocalPorts);
            Assert.Contains("192.168.10.0", (string)rule.RemoteAddresses); Assert.Equal("192.168.10.10", ((string)rule.LocalAddresses).Split('/')[0]);
        }
        finally { Marshal.FinalReleaseComObject(instance); }
    }

    [Fact]
    public void OnlyAddressPortOrSourceChangesRequireFirewallRefresh()
    {
        var config = new Configuration { Proxy = new() { ListenAddress = "192.168.10.10" }, Devices = [new() { Address = "192.168.10.21" }] };
        var changed = JsonDefaults.Clone(config); changed.Services.Add(new() { Hosts = ["example.com"] });
        Assert.False(FirewallManager.ScopeChanged(config, changed));
        changed.Devices[0].Address = "192.168.10.22"; Assert.True(FirewallManager.ScopeChanged(config, changed));
        changed = JsonDefaults.Clone(config); changed.Proxy.Port++; Assert.True(FirewallManager.ScopeChanged(config, changed));
    }
}
