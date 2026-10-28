using System.Reflection;

namespace WhiteProxy.Admin;

internal static class AppVersion
{
    internal static string Current => typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "不明";
}
