using System.Text.Json;
using WhiteProxy.Core;
using WhiteProxy.Service;
using WhiteProxy.Shared;

namespace WhiteProxy.Windows.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "WhiteProxy-tests-" + Guid.NewGuid().ToString("N"));
    public StorageTests() => Directory.CreateDirectory(directory);
    [Fact]
    public async Task AtomicSaveRetainsPreviousGenerationAndRejectsCorruption()
    {
        var repo = new ConfigurationRepository(directory);
        var first = Templates.CreateDefault(); await repo.SaveAsync(first, default);
        var next = JsonDefaults.Clone(first); next.Revision = 1; next.Proxy.GlobalBlock = true;
        await repo.SaveAsync(next, default);
        Assert.Equal(1, (await repo.LoadAsync(default)).Revision);
        var backup = JsonSerializer.Deserialize<Configuration>(await File.ReadAllTextAsync(repo.FilePath + ".bak"), JsonDefaults.Options)!;
        Assert.Equal(0, backup.Revision); Assert.False(backup.Proxy.GlobalBlock);
        await File.WriteAllTextAsync(repo.FilePath, "{invalid");
        await Assert.ThrowsAsync<JsonException>(() => repo.LoadAsync(default));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp-*"));
    }

    [Fact]
    public async Task InvalidSchemaCannotLoad()
    {
        var repo = new ConfigurationRepository(directory);
        var config = Templates.CreateDefault(); config.SchemaVersion = 999;
        await repo.SaveAsync(config, default);
        await Assert.ThrowsAsync<InvalidDataException>(() => repo.LoadAsync(default));
    }

    [Fact]
    public async Task LegacyRulesUpgradeSafelyAndNewOrderSurvivesSaveAndReload()
    {
        var repo = new ConfigurationRepository(directory);
        var c = Templates.CreateDefault(); c.SchemaVersion = 1;
        c.Devices = [new() { Id = "pc", Address = "192.168.10.21" }, new() { Id = "subnet", Address = "192.168.10.0/24" }];
        c.Policies = [new() { DeviceId = "pc", ServiceId = "chatgpt", AlwaysAllow = true }, new() { DeviceId = "subnet", ServiceId = "chatgpt", Deny = true }];
        await repo.SaveAsync(c, default);
        var upgraded = await repo.LoadAsync(default);
        Assert.Equal(2, upgraded.SchemaVersion); Assert.True(upgraded.Policies[0].Deny);
        upgraded.Policies.Reverse(); await repo.SaveAsync(upgraded, default);
        var reopened = await repo.LoadAsync(default);
        Assert.Equal("pc", reopened.Policies[0].DeviceId); Assert.Equal(2, reopened.SchemaVersion);
        var backup = JsonSerializer.Deserialize<Configuration>(await File.ReadAllTextAsync(repo.FilePath + ".bak"), JsonDefaults.Options)!;
        Assert.Equal(1, backup.SchemaVersion);
    }

    [Fact]
    public async Task Utf8LogPersistsAuditAndReloadsRecentEntries()
    {
        await using (var logs = new LogStore(directory))
        {
            Assert.True(logs.Add(new(DateTimeOffset.UtcNow, "監査", "", "", 0, true, "日本語テスト", Actor: "test-sid")));
        }
        await using var reopened = new LogStore(directory);
        Assert.Equal("日本語テスト", Assert.Single(reopened.Snapshot()).Reason);
        Assert.True(reopened.Healthy);
    }

    [Fact]
    public async Task LogWriteFailureBecomesVisibleAndUnhealthy()
    {
        await using var logs = new LogStore(directory);
        var logDirectory = Path.Combine(directory, "logs"); Directory.Delete(logDirectory);
        await File.WriteAllTextAsync(logDirectory, "blocked");
        logs.Add(new(DateTimeOffset.UtcNow, "監査", "", "", 0, true, "test"));
        for (var i = 0; i < 100 && logs.Healthy; i++) await Task.Delay(10);
        Assert.False(logs.Healthy); Assert.True(logs.Dropped > 0);
    }

    [Fact]
    public void TemplatesDefaultToNoRegisteredDevicesAndNoPolicies()
    {
        var config = Templates.CreateDefault();
        Assert.Empty(config.Devices); Assert.Empty(config.Policies); Assert.Empty(config.TemporaryGrants);
        Assert.Empty(ConfigurationValidator.Validate(config));
        Assert.Empty(config.Services.First(s => s.Id == "windows-update").Hosts);
        Assert.All(config.Services.Where(s => s.BuiltIn), s => Assert.NotEmpty(s.Source));
    }
    public void Dispose()
    {
        // このテストが作成した一意の一時ディレクトリだけを削除する。
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
