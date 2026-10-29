using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using WhiteProxy.Core;
using WhiteProxy.Proxy;
using WhiteProxy.Service;
using WhiteProxy.Shared;

namespace WhiteProxy.Windows.Tests;

public class StartupRetryTests
{
    [Fact]
    public void TransientFailureBacksOffAndStopsAfterSevenRetries()
    {
        var retry = new StartupRetry();
        var now = DateTimeOffset.UtcNow;
        foreach (var seconds in new[] { 2, 4, 8, 15, 30, 30, 30 })
        {
            Assert.Equal(TimeSpan.FromSeconds(seconds), retry.Failed(new Exception(), now));
            Assert.Equal(now.AddSeconds(seconds), retry.NextAttemptUtc);
            now = retry.NextAttemptUtc!.Value;
        }
        Assert.Null(retry.Failed(new Exception(), now));
        Assert.False(retry.Pending);
    }

    [Fact]
    public void PermanentFailureOrSuccessCancelsPendingRetry()
    {
        foreach (var error in new Exception[] { new UnauthorizedAccessException(), new InvalidDataException(), new JsonException(), new ArgumentException() })
        {
            var retry = new StartupRetry();
            retry.Failed(new SocketException(10049), DateTimeOffset.UtcNow);
            Assert.Null(retry.Failed(new Exception("wrapped", error), DateTimeOffset.UtcNow));
            Assert.False(retry.Pending);
        }
        var recovered = new StartupRetry();
        recovered.Failed(new Exception(), DateTimeOffset.UtcNow);
        recovered.Complete();
        Assert.False(recovered.Pending);
    }

    [Fact]
    public async Task WorkerRecoversWhenOccupiedPortBecomesAvailable()
    {
        var root = Path.Combine(Path.GetTempPath(), "WhiteProxy-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var repository = new ConfigurationRepository(root);
        var config = Templates.CreateDefault();
        config.Proxy.Port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        await repository.SaveAsync(config, default);
        await using var logs = new LogStore(root);
        using var worker = new ProxyWorker(repository);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(ProxyWorker).GetField("logs", flags)!.SetValue(worker, logs);
        typeof(ProxyWorker).GetField("clock", flags)!.SetValue(worker, new ClockGuard(TimeProvider.System));
        Task Attempt(CancellationToken ct = default) => (Task)typeof(ProxyWorker).GetMethod("TryStartProxyAsync", flags)!.Invoke(worker, [ct])!;
        ServiceStatus Status() => (ServiceStatus)typeof(ProxyWorker).GetMethod("Status", flags)!.Invoke(worker, null)!;
        try
        {
            await Attempt();
            Assert.False(Status().Running);
            Assert.False(Status().Healthy);
            Assert.Contains("自動再試行", Status().Error);
            Assert.Equal("Retrying", StartupReport.Read(root)?.State);
            Assert.NotEmpty(StartupReport.Read(root)!.ErrorType);
            occupied.Stop();
            await Attempt();
            Assert.True(Status().Healthy);
            Assert.True(Status().Running);
            Assert.Equal("Ready", StartupReport.Read(root)?.State);
            Assert.Contains(logs.Snapshot(), entry => entry.Reason.Contains("起動再試行成功"));
        }
        finally
        {
            (typeof(ProxyWorker).GetField("engine", flags)!.GetValue(worker) as ProxyEngine)?.Dispose();
            await logs.DisposeAsync();
            Directory.Delete(root, true);
        }
    }
}
