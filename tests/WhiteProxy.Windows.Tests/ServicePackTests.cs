using System.Text;
using System.Text.Json;
using WhiteProxy.Admin;
using WhiteProxy.Core;

namespace WhiteProxy.Windows.Tests;

public class ServicePackTests
{
    private static byte[] Pack(string service = "Web", string host = "example.com") => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        format = "WhiteProxy.ServicePack", version = 1, name = "Test pack",
        services = new[] { new { name = service, hosts = new[] { host }, allowedPorts = new[] { 443 }, source = "", notes = "" } }
    }));

    [Fact]
    public void ImportOnlyAddsDefinitionsAndKeepsOriginalConfigurationAndRulesUntouched()
    {
        var original = new Configuration
        {
            Revision = 7, Proxy = new() { GlobalBlock = true },
            Devices = [new() { Id = "pc" }], Services = [new() { Id = "existing", Name = "Existing", Hosts = ["old.example"] }],
            Policies = [new() { DeviceId = "pc", ServiceId = "existing", Deny = true }]
        };
        var before = JsonSerializer.Serialize(original, JsonDefaults.Options);
        var plan = ServicePacks.Prepare(original, ServicePacks.Parse(Pack()));
        var added = Assert.Single(plan.Added);
        Assert.Equal(2, plan.Candidate.Services.Count); Assert.Equal(7, plan.Candidate.Revision); Assert.True(plan.Candidate.Proxy.GlobalBlock);
        Assert.Equal("existing", Assert.Single(plan.Candidate.Policies).ServiceId); Assert.True(plan.Candidate.Policies[0].Deny);
        Assert.DoesNotContain(plan.Candidate.Policies, p => p.ServiceId == added.Id);
        Assert.Equal(before, JsonSerializer.Serialize(original, JsonDefaults.Options));
        Assert.True(added.Enabled); Assert.False(added.BuiltIn);
    }

    [Fact]
    public void SameNameDifferentContentGetsNewIdentityAndReimportIsIdempotent()
    {
        var original = new Configuration { Services = [new() { Id = "old", Name = "Web", Hosts = ["old.example"] }] };
        var pack = ServicePacks.Parse(Pack());
        var first = ServicePacks.Prepare(original, pack);
        Assert.Equal("Web（インポート 2）", Assert.Single(first.Added).Name);
        Assert.Equal("old.example", first.Candidate.Services[0].Hosts[0]);
        Assert.Empty(ServicePacks.Prepare(first.Candidate, pack).Added);
        var simple = ServicePacks.Prepare(new(), pack);
        var again = ServicePacks.Prepare(simple.Candidate, pack);
        Assert.Empty(again.Added); Assert.Single(again.Skipped);
    }

    [Fact]
    public void Utf8BomAndDomainNormalizationAreSupported()
    {
        var bytes = new byte[] { 0xef, 0xbb, 0xbf }.Concat(Pack(host: "EXAMPLE.COM.")).ToArray();
        var pack = ServicePacks.Parse(bytes);
        Assert.Equal("example.com", pack.Services[0].Hosts[0]);
        var existing = new Configuration { Services = [new() { Name = "Web", Hosts = ["EXAMPLE.COM."] , AllowedPorts = [443] }] };
        Assert.Empty(ServicePacks.Prepare(existing, pack).Added);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("https://example.com/path")]
    [InlineData("*.127.0.0.1")]
    [InlineData("example.com:443")]
    public void InvalidHostPatternsAreRejected(string host)
        => Assert.Throws<InvalidDataException>(() => ServicePacks.Parse(Pack(host: host)));

    [Theory]
    [InlineData("{bad")]
    [InlineData("null")]
    [InlineData("{\"format\":\"WhiteProxy.ServicePack\",\"version\":2,\"name\":\"X\",\"services\":[]}")]
    [InlineData("{\"format\":\"WhiteProxy.ServicePack\",\"version\":1,\"name\":\"X\",\"services\":[],\"policies\":[]}")]
    public void InvalidFormatOrConfigurationInjectionCannotImport(string json)
        => Assert.Throws<InvalidDataException>(() => ServicePacks.Parse(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void OversizedPackAndInvalidPortsAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => ServicePacks.Parse(new byte[ServicePacks.MaximumBytes + 1]));
        var pack = ServicePacks.Parse(Pack()); pack.Services[0].AllowedPorts = [0];
        Assert.Throws<InvalidDataException>(() => ServicePacks.Prepare(new(), pack));
    }

    [Fact]
    public void ShippedOfficeExampleUsesImportSchemaAndOnlyAddsDefinitions()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WhiteProxy.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var path = Path.Combine(directory.FullName, "docs", "service-packs", "microsoft365-licensing.json");
        var pack = ServicePacks.Parse(File.ReadAllBytes(path));
        var plan = ServicePacks.Prepare(new(), pack);
        Assert.Equal(2, Assert.Single(plan.Added).Hosts.Count); Assert.Empty(plan.Candidate.Policies);
    }

    [Fact]
    public async Task PreviewShowsTargetsBeforeSavingAndDoesNotPartiallyImport()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var pack = ServicePacks.Parse(Pack()); var plan = ServicePacks.Prepare(new(), pack);
                Configuration? saved = null;
                using var form = ServiceImportEditor.Create(pack.Name, plan, candidate => { saved = candidate; return Task.FromResult(true); });
                var controls = Descendants(form).ToList();
                var details = Assert.Single(controls.OfType<TextBox>(), box => box.Name == "ImportDetails");
                Assert.Contains("example.com", details.Text); Assert.Contains("443", details.Text); Assert.Null(saved);
                Assert.True(form.ValidateInput!()); Assert.Null(saved);
                Assert.True(form.SaveAsync!().GetAwaiter().GetResult()); Assert.Same(plan.Candidate, saved);
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void ExportedSelectionReimportsWithAllDefinitionFieldsButWithoutOperationalState()
    {
        var selected = new ServiceDefinition
        {
            Id = "private-id", Name = "ライセンス認証", Hosts = ["EXAMPLE.COM.", "*.example.com"], AllowedPorts = [443, 80],
            Source = "https://example.com/docs", DefinitionVersion = "v2", Notes = "社内確認済み", Enabled = false, BuiltIn = true
        };
        var before = JsonSerializer.Serialize(selected, JsonDefaults.Options);
        var bytes = ServicePacks.Export([selected]);
        Assert.Contains("ライセンス認証", Encoding.UTF8.GetString(bytes));
        using var document = JsonDocument.Parse(bytes);
        var entry = document.RootElement.GetProperty("services")[0];
        foreach (var property in new[] { "id", "enabled", "builtIn", "policies", "devices", "temporaryGrants" }) Assert.False(entry.TryGetProperty(property, out _));
        var imported = Assert.Single(ServicePacks.Prepare(new(), ServicePacks.Parse(bytes)).Added);
        Assert.Equal(selected.Name, imported.Name); Assert.Equal(new[] { "example.com", "*.example.com" }, imported.Hosts);
        Assert.Equal(new[] { 80, 443 }, imported.AllowedPorts); Assert.Equal(selected.Source, imported.Source);
        Assert.Equal(selected.DefinitionVersion, imported.DefinitionVersion); Assert.Equal(selected.Notes, imported.Notes);
        Assert.NotEqual(selected.Id, imported.Id); Assert.Equal(before, JsonSerializer.Serialize(selected, JsonDefaults.Options));
    }

    [Fact]
    public void ExportSupportsMultipleServicesAndRejectsUnimportableSelections()
    {
        var selected = new[] { new ServiceDefinition { Name = "First", Hosts = ["one.example"] }, new ServiceDefinition { Name = "Second", Hosts = ["two.example"] } };
        var pack = ServicePacks.Parse(ServicePacks.Export(selected));
        Assert.Equal(new[] { "First", "Second" }, pack.Services.Select(s => s.Name));
        Assert.Equal(2, ServicePacks.Prepare(new(), pack).Added.Count);
        Assert.Throws<InvalidDataException>(() => ServicePacks.Export([]));
        Assert.Throws<InvalidDataException>(() => ServicePacks.Export([new() { Hosts = [] }]));
        Assert.Throws<InvalidDataException>(() => ServicePacks.Export(Enumerable.Repeat(selected[0], 129)));
    }

    [Fact]
    public async Task ExportFileCanBeImportedAndLeavesNoTemporaryFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WhiteProxy-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "services.json"); await File.WriteAllTextAsync(path, "old");
            var bytes = ServicePacks.Export([new() { Name = "Selected", Hosts = ["example.com"] }]);
            await ServicePacks.WriteExportAsync(path, bytes);
            var plan = ServicePacks.Prepare(new(), ServicePacks.Parse(await File.ReadAllBytesAsync(path)));
            Assert.Equal("Selected", Assert.Single(plan.Added).Name);
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static IEnumerable<Control> Descendants(Control control)
    {
        foreach (Control child in control.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
