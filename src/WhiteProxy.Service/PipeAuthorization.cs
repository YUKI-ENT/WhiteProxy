using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Security.AccessControl;
using System.Security.Principal;

[assembly: InternalsVisibleTo("WhiteProxy.Windows.Tests")]
namespace WhiteProxy.Service;

internal static class PipeAuthorization
{
    internal static bool IsAdmin(WindowsIdentity? identity) => identity is not null &&
        new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

    internal static PipeSecurity CreateSecurity(bool admin)
    {
        var acl = new PipeSecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.NetworkServiceSid })
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        if (!admin)
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return acl;
    }
}
