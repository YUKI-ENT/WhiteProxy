using WhiteProxy.Admin;
using WhiteProxy.Core;
using WhiteProxy.Service;

namespace WhiteProxy.Windows.Tests;

public class ServiceUiStateTests
{
    [Theory]
    [InlineData("Automatic", "Windows起動時に自動開始")]
    [InlineData("Manual", "手動")]
    [InlineData("Disabled", "無効")]
    [InlineData("Unknown", "読み取れません")]
    public void StartupDescriptionReflectsActualRegisteredStartMode(string mode, string text)
    {
        var installation = new ServiceInstallation(true, "Stopped", "", "", "", mode);
        Assert.Contains(text, installation.StartupDescription);
        if (mode == "Automatic") Assert.Contains("再起動すると再び開始", installation.StartupDescription);
        else Assert.DoesNotContain("Windows起動時に自動開始します", installation.StartupDescription);
    }
    [Fact]
    public void UnregisteredShowsOnlyInitialRegistrationAndAllowsInitialSettings()
    {
        var state = ServiceUiState.From(new(false, "NotInstalled", "", "", ""), false, false);
        Assert.True(state.ShowRegister); Assert.False(state.ShowMaintenance);
        Assert.True(state.CanConfigure); Assert.False(state.CanStart); Assert.False(state.CanStop);
    }
    [Fact]
    public void RunningDisablesStartAndSettingsAndHidesRegistrationMaintenance()
    {
        var state = ServiceUiState.From(new(true, "Running", "", "", ""), true, false);
        Assert.False(state.CanStart); Assert.True(state.CanStop); Assert.True(state.CanRefresh);
        Assert.False(state.CanConfigure); Assert.False(state.ShowRegister); Assert.False(state.ShowMaintenance);
    }
    [Fact]
    public void StoppedEnablesStartAndSettingsAndShowsMaintenanceInsteadOfRegistration()
    {
        var state = ServiceUiState.From(new(true, "Stopped", "", "", ""), false, false);
        Assert.True(state.CanStart); Assert.False(state.CanStop); Assert.True(state.CanConfigure);
        Assert.False(state.ShowRegister); Assert.True(state.ShowMaintenance);
    }
    [Theory]
    [InlineData("StartPending")]
    [InlineData("StopPending")]
    [InlineData("Unknown")]
    public void TransitionalOrUnknownStateCannotStartStopOrChangeSettings(string status)
    {
        var state = ServiceUiState.From(new(true, status, "", "", ""), false, false);
        Assert.False(state.CanStart); Assert.False(state.CanStop); Assert.False(state.CanConfigure);
    }
    [Fact]
    public void OperationInProgressDisablesAllActionsEvenWhenRefreshReportsStopped()
    {
        var state = ServiceUiState.From(new(true, "Stopped", "", "", ""), false, true);
        Assert.False(state.CanStart); Assert.False(state.CanStop); Assert.False(state.CanConfigure); Assert.False(state.CanRefresh);
    }
    [Fact]
    public void ConsoleModeCannotBeReconfiguredEvenWithoutServiceRegistration()
    {
        var state = ServiceUiState.From(new(false, "NotInstalled", "", "", ""), true, false);
        Assert.False(state.CanConfigure);
    }
    [Fact]
    public void StoppedSettingsPreservePoliciesGrantsAndGlobalBlock()
    {
        var original = Templates.CreateDefault();
        original.Revision = 10; original.Proxy.GlobalBlock = true;
        original.Devices.Add(new() { Id = "pc", Address = "192.168.10.21" });
        original.Policies.Add(new() { DeviceId = "pc", ServiceId = "chatgpt", AlwaysAllow = true });
        original.TemporaryGrants.Add(new() { DeviceId = "pc", ServiceId = "chatgpt", StartsAtUtc = DateTimeOffset.Parse("2026-10-26T00:00:00Z"), ExpiresAtUtc = DateTimeOffset.Parse("2026-10-26T01:00:00Z"), Revoked = true });
        var updated = StoppedSettingsStore.Update(original, new() { Port = 4100, GlobalBlock = false }, 10);
        Assert.Equal(4100, updated.Proxy.Port); Assert.True(updated.Proxy.GlobalBlock); Assert.Equal(11, updated.Revision);
        Assert.Equal(JsonDefaults.Clone(original).Devices[0].Address, updated.Devices[0].Address);
        Assert.True(Assert.Single(updated.Policies).AlwaysAllow); Assert.True(Assert.Single(updated.TemporaryGrants).Revoked);
        Assert.Equal(3128, original.Proxy.Port); Assert.Equal(10, original.Revision);
        Assert.Throws<InvalidOperationException>(() => StoppedSettingsStore.Update(original, new(), 9));
    }
    [Theory]
    [InlineData("Running")]
    [InlineData("StartPending")]
    [InlineData("StopPending")]
    [InlineData("Unknown")]
    public void BackendRejectsOfflineSettingsUnlessServiceIsStopped(string status)
        => Assert.Throws<InvalidOperationException>(() => StoppedSettingsStore.EnsureStopped(new(true, status, "", "", "")));
}
