using System.Diagnostics;
using WhiteProxy.Service;

namespace WhiteProxy.Admin;

internal sealed record ServiceUpdatePlan(string Source, string Destination, Version PreviousVersion, Version NewVersion, bool Restart);

internal static class ServiceAutoUpdate
{
    internal static ServiceUpdatePlan? Inspect() => FindUpdate(Environment.ProcessPath!, ServiceDiagnostics.Inspect(),
        File.Exists, path => Version.TryParse(FileVersionInfo.GetVersionInfo(path).FileVersion, out var version) ? version : null);

    internal static ServiceUpdatePlan? FindUpdate(string source, ServiceInstallation installation,
        Func<string, bool> exists, Func<string, Version?> version)
    {
        // 開発ビルド・未登録・状態不明・無効化したサービスは自動更新しない。
        if (!installation.Installed || installation.Error.Length > 0 || installation.State is not ("Running" or "Stopped") ||
            installation.StartMode == "Disabled" || exists(Path.ChangeExtension(source, ".runtimeconfig.json"))) return null;
        var destination = ExecutablePath(installation.Executable);
        if (destination is null || !exists(destination) ||
            !string.Equals(Path.GetFullPath(destination), ServiceOperations.InstalledExecutablePath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)) return null;
        var next = version(source);
        var previous = version(destination);
        if (next is null || previous is null || next <= previous) return null;
        return new(source, destination, previous, next, installation.State == "Running");
    }

    internal static string? ExecutablePath(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        var exeEnd = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeEnd < 0 || exeEnd + 4 < command.Length && !char.IsWhiteSpace(command[exeEnd + 4])) return null;
        return command[..(exeEnd + 4)];
    }

    internal static async Task<string> ApplyAsync(ServiceUpdatePlan plan, Func<Task> closeGui,
        Func<bool, Task> update)
    {
        await closeGui();
        await update(plan.Restart);
        return $"サービスを {plan.PreviousVersion} → {plan.NewVersion} に自動更新しました。" +
            (plan.Restart ? " 稼働を再開しました。" : " 停止状態を維持しています。");
    }
}
