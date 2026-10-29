using System.Text.Json;
using WhiteProxy.Core;

namespace WhiteProxy.Service;

public static class Templates
{
    public static Configuration CreateDefault()
    {
        using var stream = typeof(Templates).Assembly.GetManifestResourceStream("WhiteProxy.Defaults.WindowsUpdate.services.json")!;
        using var pack = JsonDocument.Parse(stream);
        var service = pack.RootElement.GetProperty("services")[0].Deserialize<ServiceDefinition>(JsonDefaults.Options)!;
        service.Id = "windows-update";
        service.BuiltIn = true;
        return new() { Services = [service] };
    }
}
