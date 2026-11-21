using System.Reflection;
using System.Text.Json;
using WhiteProxy.Admin;
using WhiteProxy.Core;

namespace WhiteProxy.Windows.Tests;

public class OfflineConfigurationTests
{
    [Fact]
    public async Task StoppedGuiReadsSavedRulesWithoutWritingOrRestoringBackup()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "WhiteProxy-offline-" + Guid.NewGuid());
            try
            {
                Directory.CreateDirectory(directory);
                var config = new Configuration
                {
                    Devices = [new() { Id = "pc", Name = "PC", Address = "192.168.1.2" }],
                    Services = [new() { Id = "web", Name = "Web", Hosts = ["example.com"] }],
                    Policies = [new() { DeviceId = "pc", ServiceId = "web" }]
                };
                var path = Path.Combine(directory, "configuration.json");
                var original = JsonSerializer.Serialize(config, JsonDefaults.Options);
                File.WriteAllText(path, original);
                using var form = new MainForm();
                SynchronizationContext.SetSynchronizationContext(null);
                Assert.True(form.LoadSavedConfigurationAsync(directory).GetAwaiter().GetResult());
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var loaded = (Configuration)typeof(MainForm).GetField("current", flags)!.GetValue(form)!;
                Assert.Single(loaded.Policies); Assert.Single(loaded.Devices);
                Assert.Equal(original, File.ReadAllText(path));
                File.Move(path, path + ".bak");
                Assert.False(form.LoadSavedConfigurationAsync(directory).GetAwaiter().GetResult());
                Assert.False(File.Exists(path));
                Assert.Equal(original, File.ReadAllText(path + ".bak"));
                Assert.Single(((Configuration)typeof(MainForm).GetField("current", flags)!.GetValue(form)!).Policies);
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
