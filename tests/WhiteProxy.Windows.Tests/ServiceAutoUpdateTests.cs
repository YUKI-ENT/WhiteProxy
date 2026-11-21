using WhiteProxy.Admin;
using WhiteProxy.Service;
using System.Reflection;

namespace WhiteProxy.Windows.Tests;

public class ServiceAutoUpdateTests
{
    private const string Source = @"D:\updates\WhiteProxy.exe";
    private static ServiceInstallation Installed(string state = "Running", string startMode = "Automatic") =>
        new(true, state, $"\"{ServiceOperations.InstalledExecutablePath}\" --service", "NetworkService", "", startMode);

    private static ServiceUpdatePlan? Find(ServiceInstallation installation, Version sourceVersion, Version installedVersion,
        bool development = false) => ServiceAutoUpdate.FindUpdate(Source, installation,
            path => path.Equals(ServiceOperations.InstalledExecutablePath, StringComparison.OrdinalIgnoreCase) ||
                development && path.Equals(Path.ChangeExtension(Source, ".runtimeconfig.json"), StringComparison.OrdinalIgnoreCase),
            path => path == Source ? sourceVersion : installedVersion);

    [Theory]
    [InlineData("Running", true)]
    [InlineData("Stopped", false)]
    public void NewerPublishedGuiUpdatesServiceAndRemembersRunningState(string state, bool restart)
    {
        var plan = Find(Installed(state, "Manual"), new(2026, 11, 21, 4), new(2026, 11, 21, 3));
        Assert.NotNull(plan); Assert.Equal(restart, plan.Restart);
        Assert.Equal(ServiceOperations.InstalledExecutablePath, plan.Destination);
    }

    [Theory]
    [InlineData("2026.11.21.4", "2026.11.21.4")]
    [InlineData("2026.11.21.3", "2026.11.21.4")]
    public void SameOrOlderGuiNeverReplacesService(string source, string installed) =>
        Assert.Null(Find(Installed(), Version.Parse(source), Version.Parse(installed)));

    [Fact]
    public void DevelopmentUnregisteredUnknownAndDisabledServicesAreNotAutomaticallyChanged()
    {
        var next = new Version(2026, 11, 21, 4); var old = new Version(2026, 11, 21, 3);
        Assert.Null(Find(Installed(), next, old, development: true));
        Assert.Null(Find(Installed() with { Installed = false }, next, old));
        Assert.Null(Find(Installed("StartPending"), next, old));
        Assert.Null(Find(Installed() with { Error = "access denied" }, next, old));
        Assert.Null(Find(Installed("Stopped", "Disabled"), next, old));
        Assert.Null(ServiceAutoUpdate.FindUpdate(ServiceOperations.InstalledExecutablePath, Installed(),
            path => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase), _ => next));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\WhiteProxy\\WhiteProxy.exe\" --service", "C:\\Program Files\\WhiteProxy\\WhiteProxy.exe")]
    [InlineData("C:\\WhiteProxy\\WhiteProxy.exe --service", "C:\\WhiteProxy\\WhiteProxy.exe")]
    [InlineData("\"unterminated", null)]
    [InlineData("invalid", null)]
    public void ReadsServiceExecutableWithoutRunningCommand(string command, string? expected) =>
        Assert.Equal(expected, ServiceAutoUpdate.ExecutablePath(command));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClosesPreviousGuiBeforeUpdatingAndPassesOriginalRunningState(bool restart)
    {
        var steps = new List<string>();
        var plan = new ServiceUpdatePlan(Source, ServiceOperations.InstalledExecutablePath, new(1, 0), new(2, 0), restart);
        var notice = await ServiceAutoUpdate.ApplyAsync(plan,
            () => { steps.Add("close"); return Task.CompletedTask; },
            resume => { Assert.Equal(restart, resume); steps.Add("update"); return Task.CompletedTask; });
        Assert.Equal(new[] { "close", "update" }, steps);
        Assert.Contains("自動更新しました", notice);
    }

    [Fact]
    public async Task OldGuiThatCannotClosePreventsServiceChanges()
    {
        var changed = false;
        var plan = new ServiceUpdatePlan(Source, ServiceOperations.InstalledExecutablePath, new(1, 0), new(2, 0), true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ServiceAutoUpdate.ApplyAsync(plan,
            () => throw new InvalidOperationException("close the old GUI"),
            _ => { changed = true; return Task.CompletedTask; }));
        Assert.False(changed);
    }

    [Theory]
    [InlineData("WhiteProxy v2026.11.21.4 — 管理コンソール（管理者）", true)]
    [InlineData("WhiteProxy v2026.11.21.3 — 管理コンソール（管理者）", false)]
    [InlineData("WhiteProxy — 管理コンソール（管理者）", false)]
    [InlineData("Other window", false)]
    public void OnlySupportingGuiVersionsReceiveExitMessage(string title, bool expected) =>
        Assert.Equal(expected, GuiInstance.SupportsUpgradeExit(title));

    [Fact]
    public void ReplacingExecutablePreservesPreviousBinary()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WhiteProxy-update-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var destination = Path.Combine(directory, "WhiteProxy.exe");
            var temporary = Path.Combine(directory, "WhiteProxy.new.exe");
            File.WriteAllText(destination, "old version"); File.WriteAllText(temporary, "new version");
            ServiceOperations.ReplaceExecutable(temporary, destination);
            Assert.Equal("new version", File.ReadAllText(destination));
            Assert.Equal("old version", File.ReadAllText(Path.ChangeExtension(destination, ".previous.exe")));
            Assert.False(File.Exists(temporary));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task UpgradeMessageClosesIdleGuiButKeepsServiceOperationsOpen(bool busy) => RunSta(() =>
    {
        using var form = new MainForm();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(MainForm).GetField("serviceBusy", flags)!.SetValue(form, busy);
        var message = Message.Create(form.Handle, (int)GuiInstance.ExitForUpgradeMessage, IntPtr.Zero, IntPtr.Zero);
        typeof(MainForm).GetMethod("WndProc", flags)!.Invoke(form, [message]);
        Assert.Equal(!busy, form.IsDisposed);
    });

    [Fact]
    public Task ProgressDialogReportsSuccessAndFailureAndAlwaysCloses() => RunSta(() =>
    {
        var plan = new ServiceUpdatePlan(Source, ServiceOperations.InstalledExecutablePath, new(1, 0), new(2, 0), true);
        using (var success = new ServiceUpdateDialog(plan, async () => { await Task.Yield(); return "updated"; }))
        {
            success.ShowDialog();
            Assert.Equal("updated", success.Notice); Assert.Null(success.Failure);
        }
        var error = new IOException("could not deploy");
        using var failed = new ServiceUpdateDialog(plan, () => Task.FromException<string>(error));
        failed.ShowDialog(); Assert.Same(error, failed.Failure);
    });

    private static async Task RunSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
