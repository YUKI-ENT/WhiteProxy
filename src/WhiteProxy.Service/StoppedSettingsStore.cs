using System.Security.Principal;
using WhiteProxy.Core;
using WhiteProxy.Shared;

namespace WhiteProxy.Service;

/// <summary>サービス停止中の初期設定。端末・許可・監査履歴は変更しない。</summary>
public sealed class StoppedSettingsStore
{
    private readonly ConfigurationRepository repository = new(ServiceHost.DataDirectory);
    public static void EnsureStopped(ServiceInstallation installation)
    {
        if (installation.Error.Length > 0 || installation.Installed && installation.State != "Stopped")
            throw new InvalidOperationException("待受・運用設定はサービスを停止してから変更してください。");
    }
    public async Task<Configuration> ReadAsync()
    {
        RuntimeAccess.RequireDataDirectoryAccess();
        EnsureStopped(ServiceDiagnostics.Inspect());
        if (!File.Exists(repository.FilePath) && !File.Exists(repository.FilePath + ".bak")) return Templates.CreateDefault();
        return await repository.LoadAsync(default);
    }
    public async Task<Configuration> SaveAsync(ProxySettings settings, long expectedRevision)
    {
        var original = await ReadAsync();
        var updated = Update(original, settings, expectedRevision);
        ListenAddresses.EnsureCurrent(updated.Proxy.ListenAddress);
        repository.SecureDirectory();
        // 編集中に別のGUIから開始された場合にも、保存を中止する。
        EnsureStopped(ServiceDiagnostics.Inspect());
        await repository.SaveAsync(updated, default);
        await using var logs = new LogStore(repository.DirectoryPath) { RetentionDays = updated.Proxy.LogRetentionDays };
        using var identity = WindowsIdentity.GetCurrent();
        logs.Add(new(DateTimeOffset.UtcNow, "監査", "", "", 0, true,
            $"停止中の運用設定保存: revision={updated.Revision}", Actor: identity.User?.Value ?? ""));
        return updated;
    }
    internal static Configuration Update(Configuration original, ProxySettings settings, long expectedRevision)
    {
        if (original.Revision != expectedRevision)
            throw new InvalidOperationException("別の操作で設定が変更されました。画面を開き直してください。");
        var updated = JsonDefaults.Clone(original);
        updated.Proxy = JsonDefaults.Clone(settings);
        updated.Proxy.GlobalBlock = original.Proxy.GlobalBlock;
        updated.Revision++;
        var errors = ConfigurationValidator.Validate(updated);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        return updated;
    }
}
