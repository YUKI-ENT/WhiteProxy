using System.Net;
using WhiteProxy.Core;
using WhiteProxy.Proxy;

namespace WhiteProxy.Tests;

public class ListenAddressTests
{
    [Theory]
    [InlineData("192.168.200.111", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("192.168.0.0", false)]
    [InlineData("192.168.200.0", false)]
    [InlineData("192.168.0.0/24", false)]
    [InlineData("::1", false)]
    public void ListenIpMustBelongToThisMachineOrBeAny(string value, bool valid)
    {
        var assigned = new[] { IPAddress.Parse("192.168.200.111"), IPAddress.Loopback };
        Assert.Equal(valid, ListenAddresses.Validate(value, assigned) is null);
    }

    [Fact]
    public void DoesNotRejectEveryAddressEndingInZero()
    {
        // /23等では末尾.0がホストとなる場合がある。末尾で判定せず実際の割当で判定する。
        Assert.Null(ListenAddresses.Validate("192.168.1.0", [IPAddress.Parse("192.168.1.0")]));
    }

    [Fact]
    public void MissingLocalAddressFailsBeforeProxyStarts()
    {
        var assigned = ListenAddresses.CurrentIpv4();
        var missing = Enumerable.Range(1, 254).Select(i => IPAddress.Parse("192.0.2." + i)).First(ip => !assigned.Contains(ip));
        var clock = new ManualClock(); var config = ProxyTests.Config(ProxyTests.FreePort(), 443, clock); config.Proxy.ListenAddress = missing.ToString();
        using var proxy = new ProxyEngine(() => config, clock, _ => { });
        Assert.Throws<ListenAddressUnavailableException>(() => proxy.Start()); Assert.False(proxy.Running);
    }
}
