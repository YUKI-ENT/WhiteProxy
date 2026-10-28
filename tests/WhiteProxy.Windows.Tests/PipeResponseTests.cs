using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using WhiteProxy.Service;
using WhiteProxy.Shared;

namespace WhiteProxy.Windows.Tests;

public class PipeResponseTests
{
    [Fact]
    public async Task SlowReaderReceivesCompleteResponseBeforeDisconnectAndPipeCanBeReused()
    {
        var name = "WhiteProxy-slow-reader-" + Guid.NewGuid().ToString("N");
        using var identity = WindowsIdentity.GetCurrent();
        var acl = PipeAuthorization.CreateSecurity(false);
        acl.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        using var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 8192, 8192, acl);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serving = Task.Run(async () =>
        {
            for (var i = 0; i < 3; i++)
            {
                await pipe.WaitForConnectionAsync(timeout.Token);
                await Protocol.ReadAsync<IpcRequest>(pipe, timeout.Token);
                await Protocol.WriteResponseAsync(pipe, new IpcResponse { Success = true, Message = new string('あ', 10000) }, timeout.Token);
                if (i == 0) written.SetResult();
                pipe.Disconnect();
                if (i == 0) disconnected.SetResult();
            }
        }, timeout.Token);
        for (var i = 0; i < 3; i++)
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
            await client.ConnectAsync(timeout.Token);
            await Protocol.WriteAsync(client, new IpcRequest(), timeout.Token);
            if (i == 0)
            {
                // 小さなフレームでなく大きい応答をあえて読み遅らせる。書込中でも切断してはならない。
                await Task.Delay(100, timeout.Token);
                Assert.False(disconnected.Task.IsCompleted);
            }
            var response = await Protocol.ReadAsync<IpcResponse>(client, timeout.Token);
            Assert.True(response.Success);
            Assert.Equal(new string('あ', 10000), response.Message);
        }
        await serving;
        Assert.True(written.Task.IsCompletedSuccessfully);
    }
}
