using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using WhiteProxy.Service;
using WhiteProxy.Shared;

namespace WhiteProxy.Windows.Tests;

public class ProxyWorkerPipeTests
{
    [Theory]
    [InlineData("connect-close")]
    [InlineData("partial-header")]
    [InlineData("partial-body")]
    [InlineData("bad-size")]
    [InlineData("abort-response")]
    [InlineData("before-accept")]
    [InlineData("null-body")]
    [InlineData("malformed-json")]
    [InlineData("extra-byte")]
    public async Task ActualWorkerSurvivesAbruptClientAndAcceptsNextRequests(string failure)
    {
        var name = "WhiteProxy-interrupted-" + Guid.NewGuid().ToString("N");
        using var identity = WindowsIdentity.GetCurrent();
        var acl = PipeAuthorization.CreateSecurity(false);
        acl.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        using var listener = new NamedPipeListener(() => NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 8192, 8192, acl));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var worker = new ProxyWorker(new ConfigurationRepository(Path.GetTempPath()));
        Task? serving = failure == "before-accept" ? null : worker.ServePipe(listener, false, stop.Token);
        try
        {
            using (var brokenClient = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation))
            {
                await brokenClient.ConnectAsync(stop.Token);
                switch (failure)
                {
                    case "partial-header": await brokenClient.WriteAsync(new byte[] { 22, 0 }, stop.Token); break;
                    case "partial-body":
                        await brokenClient.WriteAsync(BitConverter.GetBytes(100), stop.Token);
                        await brokenClient.WriteAsync("{"u8.ToArray(), stop.Token); break;
                    case "bad-size": await brokenClient.WriteAsync(BitConverter.GetBytes(int.MaxValue), stop.Token); break;
                    case "abort-response": await Protocol.WriteAsync(brokenClient, new IpcRequest(), stop.Token); break;
                    case "null-body":
                    case "malformed-json":
                        var bytes = System.Text.Encoding.UTF8.GetBytes(failure == "null-body" ? "null" : "{broken");
                        await brokenClient.WriteAsync(BitConverter.GetBytes(bytes.Length), stop.Token);
                        await brokenClient.WriteAsync(bytes, stop.Token); break;
                    case "extra-byte":
                        await Protocol.WriteAsync(brokenClient, new IpcRequest(), stop.Token);
                        await Protocol.ReadAsync<IpcResponse>(brokenClient, stop.Token);
                        await brokenClient.WriteAsync(new byte[] { 1 }, stop.Token); break;
                }
            }
            serving ??= worker.ServePipe(listener, false, stop.Token);
            await Task.Delay(50, stop.Token);
            Assert.False(serving.IsFaulted, serving.Exception?.ToString());
            for (var i = 0; i < 3; i++)
            {
                using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
                await client.ConnectAsync(stop.Token);
                await Protocol.WriteAsync(client, new IpcRequest(), stop.Token);
                Assert.True((await Protocol.ReadAsync<IpcResponse>(client, stop.Token)).Success);
            }
        }
        finally
        {
            stop.Cancel();
            if (serving is not null) await serving;
        }
    }

    [Fact]
    public async Task ActualWorkerAcceptsRepeatedStatusRequestsAfterClientEof()
    {
        var name = "WhiteProxy-worker-" + Guid.NewGuid().ToString("N");
        using var identity = WindowsIdentity.GetCurrent();
        var acl = PipeAuthorization.CreateSecurity(false);
        acl.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        using var listener = new NamedPipeListener(() => NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 8192, 8192, acl));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var worker = new ProxyWorker(new ConfigurationRepository(Path.GetTempPath()));
        var serving = worker.ServePipe(listener, false, stop.Token);
        try
        {
            for (var i = 0; i < 5; i++)
            {
                using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
                await client.ConnectAsync(stop.Token);
                await Protocol.WriteAsync(client, new IpcRequest { Operation = "status" }, stop.Token);
                var response = await Protocol.ReadAsync<IpcResponse>(client, stop.Token);
                Assert.True(response.Success);
                client.Dispose();
                // EOFの処理後も、本体の待受タスクが落ちずに次の接続を受け付ける必要がある。
                await Task.Delay(30, stop.Token);
                Assert.False(serving.IsFaulted, serving.Exception?.ToString());
            }
        }
        finally
        {
            stop.Cancel();
            try { await serving; } catch (OperationCanceledException) { }
        }
    }
}
