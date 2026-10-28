using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using WhiteProxy.Service;
using WhiteProxy.Shared;

namespace WhiteProxy.Windows.Tests;

public class PipeTests
{
    [Fact]
    public void ReadAndAdminAclSeparateUsersAndDenyNetworkLogon()
    {
        foreach (var admin in new[] { false, true })
        {
            var rules = PipeAuthorization.CreateSecurity(admin).GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
            Assert.Contains(rules, r => r.IdentityReference == new SecurityIdentifier(WellKnownSidType.NetworkSid, null) && r.AccessControlType == AccessControlType.Deny);
            Assert.Equal(!admin, rules.Any(r => r.IdentityReference == new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null) && r.AccessControlType == AccessControlType.Allow));
        }
    }

    [Fact]
    public async Task LocalNamedPipeImpersonationInspectsRealCallerToken()
    {
        var name = "WhiteProxy-test-" + Guid.NewGuid().ToString("N");
        using var identity = WindowsIdentity.GetCurrent();
        var acl = PipeAuthorization.CreateSecurity(false);
        // テスト実行者がUsersグループ外でも自身のテスト用パイプへ接続可能にする。
        acl.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        using var server = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 4096, acl);
        var serving = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            await Protocol.ReadAsync<IpcRequest>(server, default);
            bool? admin = null; string? sid = null;
            server.RunAsClient(() =>
            {
                using var caller = WindowsIdentity.GetCurrent(true);
                sid = caller?.User?.Value; admin = PipeAuthorization.IsAdmin(caller);
            });
            await Protocol.WriteAsync(server, new IpcResponse { Success = admin == true, Message = sid ?? "" }, default);
        });
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        await client.ConnectAsync(5000);
        await Protocol.WriteAsync(client, new IpcRequest { Operation = "apply" }, default);
        var response = await Protocol.ReadAsync<IpcResponse>(client, default);
        Assert.Equal(PipeAuthorization.IsAdmin(identity), response.Success);
        Assert.Equal(identity.User!.Value, response.Message);
        await serving.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
