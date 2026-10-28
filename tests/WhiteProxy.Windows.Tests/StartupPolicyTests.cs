using WhiteProxy.Admin;

namespace WhiteProxy.Windows.Tests;

public class StartupPolicyTests
{
    [Theory]
    [InlineData((int)ApplicationMode.Gui, false, true)]
    [InlineData((int)ApplicationMode.Gui, true, false)]
    [InlineData((int)ApplicationMode.Console, false, true)]
    [InlineData((int)ApplicationMode.Console, true, false)]
    [InlineData((int)ApplicationMode.ServiceManagement, false, true)]
    [InlineData((int)ApplicationMode.ServiceManagement, true, false)]
    [InlineData((int)ApplicationMode.Service, false, false)]
    [InlineData((int)ApplicationMode.Service, true, false)]
    public void OnlyInteractiveAdministrationRequiresElevation(int mode, bool administrator, bool expected)
        => Assert.Equal(expected, StartupPolicy.NeedsElevation((ApplicationMode)mode, administrator));

    [Fact]
    public void GuiDefaultsToAdministrationAndSettingsNavigationIsPreserved()
    {
        Assert.Equal(ApplicationMode.Gui, StartupPolicy.Mode([]));
        Assert.Equal(ApplicationMode.Gui, StartupPolicy.Mode(["--settings"]));
        var info = StartupPolicy.ElevatedStart(@"C:\Program Files\WhiteProxy\WhiteProxy.exe", ["--settings"]);
        Assert.Equal("runas", info.Verb); Assert.True(info.UseShellExecute);
        Assert.Equal(@"C:\Program Files\WhiteProxy\WhiteProxy.exe", info.FileName);
        Assert.Equal("--settings", Assert.Single(info.ArgumentList));
    }
    [Fact]
    public void ServiceModeIsRecognizedBeforeAnyInteractiveMode()
    {
        Assert.Equal(ApplicationMode.Service, StartupPolicy.Mode(["--service"]));
        Assert.Equal(ApplicationMode.Service, StartupPolicy.Mode(["--service", "--console"]));
        Assert.Equal(ApplicationMode.Console, StartupPolicy.Mode(["--console"]));
        Assert.Equal(ApplicationMode.ServiceManagement, StartupPolicy.Mode(["--manage-service", "start"]));
    }
}
