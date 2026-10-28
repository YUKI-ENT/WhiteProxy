using System.Security.Principal;
using WhiteProxy.Service;

namespace WhiteProxy.Windows.Tests;

public sealed class RuntimeAccessTests
{
    [Theory]
    [InlineData(WellKnownSidType.NetworkServiceSid, false, true)]
    [InlineData(WellKnownSidType.LocalSystemSid, false, true)]
    [InlineData(WellKnownSidType.BuiltinUsersSid, false, false)]
    [InlineData(WellKnownSidType.BuiltinAdministratorsSid, false, false)]
    [InlineData(WellKnownSidType.BuiltinAdministratorsSid, true, true)]
    public void HostingRequiresServiceIdentityOrAnElevatedAdministrator(WellKnownSidType sid, bool elevated, bool expected)
        => Assert.Equal(expected, RuntimeAccess.CanHost(new SecurityIdentifier(sid, null), elevated));

    [Fact]
    public void NonElevatedCallerCannotCreateDirectoryBeforeAuthorization()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        if (RuntimeAccess.CanHost(identity.User, elevated)) return;
        var path = Path.Combine(Path.GetTempPath(), "WhiteProxy-unauthorized-" + Guid.NewGuid().ToString("N"));
        var repository = new ConfigurationRepository(path);
        var exception = Assert.Throws<UnauthorizedAccessException>(repository.SecureDirectory);
        Assert.Contains("Visual Studio", exception.Message);
        Assert.False(Directory.Exists(path));
    }
}
