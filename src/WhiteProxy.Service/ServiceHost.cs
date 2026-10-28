using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WhiteProxy.Service;

public static class ServiceHost
{
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WhiteProxy");
    public static async Task RunAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.Services.AddWindowsService(o => o.ServiceName = "WhiteProxy");
        builder.Services.AddSingleton(new ConfigurationRepository(DataDirectory));
        builder.Services.AddHostedService<ProxyWorker>();
        await builder.Build().RunAsync();
    }
}
