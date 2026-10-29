using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using WhiteProxy.Core;
using WhiteProxy.Proxy;

namespace WhiteProxy.Tests;

public class StreamingTests
{
    [Fact]
    public async Task HttpsCertificateIsOriginCertificateAndTimerClosesActiveTlsTunnel()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Windows Schannelのサーバー認証は一時CNGキーを受け付けない環境がある。
        using var cert = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var originPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var c = ProxyTests.Config(ProxyTests.FreePort(), originPort, new ManualClock());
        c.TemporaryGrants[0].StartsAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
        c.TemporaryGrants[0].ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(3);
        using var proxy = new ProxyEngine(() => c, TimeProvider.System, _ => { }, false); proxy.Start();
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var ssl = new SslStream(client.GetStream());
            await ssl.AuthenticateAsServerAsync(cert, false, SslProtocols.Tls12, false);
            await ssl.WriteAsync("hello"u8.ToArray());
            try { _ = await ssl.ReadAsync(new byte[1]); } catch (IOException) { }
        });
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, c.Proxy.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{originPort} HTTP/1.1\r\nHost: 127.0.0.1:{originPort}\r\n\r\n"));
        Assert.Contains("200", await ProxyTests.ReadHeaders(stream));
        var originCert = false;
        using var tls = new SslStream(stream, false, (_, presented, _, _) => originCert = presented?.GetCertHashString() == cert.GetCertHashString());
        try { await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost", EnabledSslProtocols = SslProtocols.Tls12 }); }
        catch { await server.WaitAsync(TimeSpan.FromSeconds(5)); throw; }
        var payload = new byte[5]; await tls.ReadExactlyAsync(payload).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(originCert); Assert.Equal("hello", Encoding.ASCII.GetString(payload));
        // Sweepの手動呼出しなし。実時間の周期監視により5秒以内に切断する。
        await ProxyTests.AssertClosed(tls);
        await server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("block")]
    [InlineData("device")]
    [InlineData("service")]
    [InlineData("invalid")]
    public async Task PolicyChangesCloseExistingConnect(string change)
    {
        var clock = new ManualClock();
        using var origin = new TcpListener(IPAddress.Loopback, 0); origin.Start();
        var port = ((IPEndPoint)origin.LocalEndpoint).Port;
        Configuration? c = ProxyTests.Config(ProxyTests.FreePort(), port, clock);
        using var proxy = new ProxyEngine(() => c, clock, _ => { }, false); proxy.Start();
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, c.Proxy.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{port} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n\r\n"));
        Assert.Contains("200", await ProxyTests.ReadHeaders(stream));
        using var upstream = await origin.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await stream.WriteAsync("PING\r\n\r\n"u8.ToArray());
        await upstream.GetStream().ReadExactlyAsync(new byte[8]).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        switch (change)
        {
            case "block": c.Proxy.GlobalBlock = true; break;
            case "device": c.Devices[0].Enabled = false; break;
            case "service": c.Services[0].Enabled = false; break;
            case "invalid": c = null; break;
        }
        proxy.Sweep(); await ProxyTests.AssertClosed(stream);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    [InlineData("[::ffff:127.0.0.1]")]
    [InlineData("10.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    public async Task PrivateDnsAnswerIsBlockedByActualOutboundConnector(string host)
    {
        var clock = new ManualClock();
        using var origin = new TcpListener(IPAddress.Loopback, 0); origin.Start();
        var port = ((IPEndPoint)origin.LocalEndpoint).Port;
        var c = ProxyTests.Config(ProxyTests.FreePort(), port, clock); c.Services[0].Hosts = [host];
        using var proxy = new ProxyEngine(() => c, clock, _ => { }); proxy.Start();
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, c.Proxy.Port);
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\n\r\n"));
        var response = await ProxyTests.ReadHeaders(client.GetStream());
        Assert.DoesNotContain("200", response);
        Assert.False(origin.Pending());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MismatchedAuthorityNeverConnectsToOrigin(bool connect)
    {
        var clock = new ManualClock();
        using var origin = new TcpListener(IPAddress.Loopback, 0); origin.Start();
        var port = ((IPEndPoint)origin.LocalEndpoint).Port;
        var c = ProxyTests.Config(ProxyTests.FreePort(), port, clock);
        using var proxy = new ProxyEngine(() => c, clock, _ => { }, false); proxy.Start();
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, c.Proxy.Port);
        var raw = connect ? $"CONNECT 127.0.0.1:{port} HTTP/1.1\r\nHost: evil.example:{port}\r\n\r\n" : $"GET http://127.0.0.1:{port}/ HTTP/1.1\r\nHost: evil.example:{port}\r\n\r\n";
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(raw));
        Assert.Contains("403", await ProxyTests.ReadHeaders(client.GetStream()));
        Assert.False(origin.Pending());
    }

    [Fact]
    public async Task HttpStreamIsCutOffWithoutBufferingAndLogsContainNoSecrets()
    {
        var clock = new ManualClock();
        using var origin = new TcpListener(IPAddress.Loopback, 0); origin.Start();
        var port = ((IPEndPoint)origin.LocalEndpoint).Port;
        var c = ProxyTests.Config(ProxyTests.FreePort(), port, clock);
        var events = new System.Collections.Concurrent.ConcurrentQueue<TrafficEvent>();
        using var proxy = new ProxyEngine(() => c, clock, events.Enqueue, false); proxy.Start();
        var source = Task.Run(async () =>
        {
            using var server = await origin.AcceptTcpClientAsync();
            await ProxyTests.ReadHeaders(server.GetStream());
            await server.GetStream().WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 9999999999\r\nContent-Type: text/event-stream\r\n\r\n"u8.ToArray());
            await server.GetStream().WriteAsync(new byte[65536]);
            try { _ = await server.GetStream().ReadAsync(new byte[1]); } catch (IOException) { }
        });
        using var handler = new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{c.Proxy.Port}") };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/stream?q=SECRET-QUERY");
        request.Headers.Add("Cookie", "SECRET-COOKIE"); request.Headers.Add("Authorization", "Bearer SECRET-TOKEN");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        using var stream = await response.Content.ReadAsStreamAsync();
        await stream.ReadExactlyAsync(new byte[65536]).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        clock.UtcNow = clock.UtcNow.AddMinutes(90); proxy.Sweep();
        await ProxyTests.AssertClosed(stream);
        Assert.DoesNotContain("SECRET", System.Text.Json.JsonSerializer.Serialize(events));
        Assert.All(events.Where(e => e.Host == "127.0.0.1"), e => Assert.Equal("/stream", e.RequestPath));
        await source.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WebSocketUpgradeIsRelayedAndRevocationClosesSocket()
    {
        var clock = new ManualClock();
        using var origin = new TcpListener(IPAddress.Loopback, 0); origin.Start();
        var port = ((IPEndPoint)origin.LocalEndpoint).Port;
        var c = ProxyTests.Config(ProxyTests.FreePort(), port, clock);
        using var proxy = new ProxyEngine(() => c, clock, _ => { }, false); proxy.Start();
        var source = Task.Run(async () =>
        {
            using var server = await origin.AcceptTcpClientAsync();
            Assert.Contains("Upgrade: websocket", await ProxyTests.ReadHeaders(server.GetStream()));
            await server.GetStream().WriteAsync("HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=\r\n\r\n"u8.ToArray());
            await server.GetStream().WriteAsync(new byte[] { 0x81, 5, 104, 101, 108, 108, 111 });
            try { _ = await server.GetStream().ReadAsync(new byte[1]); } catch (IOException) { }
        });
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, c.Proxy.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET http://127.0.0.1:{port}/ws HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n\r\n"));
        Assert.Contains("101", await ProxyTests.ReadHeaders(stream));
        var frame = new byte[7]; await stream.ReadExactlyAsync(frame).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("hello", Encoding.ASCII.GetString(frame, 2, 5));
        c.TemporaryGrants[0].Revoked = true; proxy.Sweep(); await ProxyTests.AssertClosed(stream);
        await source.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
