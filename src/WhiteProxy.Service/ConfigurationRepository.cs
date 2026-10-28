using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using WhiteProxy.Core;

namespace WhiteProxy.Service;

public interface IConfigurationRepository
{
    Task<Configuration> LoadAsync(CancellationToken ct);
    Task SaveAsync(Configuration config, CancellationToken ct);
}

public sealed class ConfigurationRepository(string directory) : IConfigurationRepository
{
    public string DirectoryPath { get; } = directory;
    public string FilePath => Path.Combine(DirectoryPath, "configuration.json");
    public void SecureDirectory()
    {
        // 所有者は未昇格でもACLを変更できる。先に権限を確認し、自分自身を締め出さない。
        RuntimeAccess.RequireDataDirectoryAccess();
        Directory.CreateDirectory(DirectoryPath);
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid, WellKnownSidType.NetworkServiceSid })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(DirectoryPath).SetAccessControl(acl);
    }

    public async Task<Configuration> LoadAsync(CancellationToken ct)
    {
        // 破損時に古い許可を自動復活させない。バックアップ復旧は管理者が明示実行する。
        if (new FileInfo(FilePath).Length > 4 * 1024 * 1024) throw new InvalidDataException("設定サイズ上限です。");
        var bytes = await File.ReadAllBytesAsync(FilePath, ct);
        var config = JsonSerializer.Deserialize<Configuration>(bytes, JsonDefaults.Options) ?? throw new InvalidDataException("設定が空です。");
        config = RuleOrder.Upgrade(config);
        var errors = ConfigurationValidator.Validate(config);
        if (errors.Count != 0) throw new InvalidDataException(string.Join("\n", errors));
        return config;
    }

    public async Task SaveAsync(Configuration config, CancellationToken ct)
    {
        var path = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(config, JsonDefaults.Options);
            if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("設定サイズ上限です。");
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await file.WriteAsync(bytes, ct); file.Flush(true);
            }
            if (File.Exists(FilePath)) File.Replace(path, FilePath, FilePath + ".bak", true);
            else File.Move(path, FilePath);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
