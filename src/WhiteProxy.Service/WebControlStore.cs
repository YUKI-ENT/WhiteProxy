using System.Security.Cryptography;
using System.Text.Json;
using WhiteProxy.Shared;

namespace WhiteProxy.Service;

internal sealed class WebControlOptions
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 3129;
    public long Revision { get; set; }
    public string Salt { get; set; } = "";
    public string PasswordHash { get; set; } = "";
}

public sealed class WebControlStore(string root)
{
    private const int Iterations = 210_000;
    private string FilePath => Path.Combine(root, "web-control.json");
    internal WebControlOptions Read()
    {
        if (!File.Exists(FilePath)) return new();
        if (new FileInfo(FilePath).Length > 16384) throw new InvalidDataException("Web設定ファイルが大きすぎます。");
        var options = JsonSerializer.Deserialize<WebControlOptions>(File.ReadAllText(FilePath)) ?? throw new InvalidDataException("Web設定が不正です。");
        if (options.Port is < 1 or > 65535 || options.Revision < 0 || options.Salt is null || options.PasswordHash is null ||
            (options.Enabled || options.PasswordHash.Length > 0 || options.Salt.Length > 0) && (Convert.FromBase64String(options.Salt).Length != 16 || Convert.FromBase64String(options.PasswordHash).Length != 32))
            throw new InvalidDataException("Web設定が不正です。");
        return options;
    }
    public WebControlStatus ReadStatus()
    {
        RuntimeAccess.RequireDataDirectoryAccess();
        return Public(Read());
    }
    internal static WebControlStatus Public(WebControlOptions options) => new(options.Enabled, options.Port, options.PasswordHash.Length != 0, options.Revision);
    internal WebControlOptions Prepare(WebControlChange change, int proxyPort)
    {
        var options = Read();
        if (options.Revision != change.ExpectedRevision) throw new InvalidOperationException("Web設定が別の操作で更新されています。再読込してください。");
        if (change.Port is < 1 or > 65535 || change.Port == proxyPort) throw new InvalidOperationException("Webポートは1～65535で、プロキシーポートと別にしてください。");
        if (change.Password is null || change.Password.Length > 256) throw new InvalidOperationException("パスワードは12～256文字で指定してください。");
        if (change.Password.Length > 0)
        {
            if (change.Password.Length < 12 || string.IsNullOrWhiteSpace(change.Password)) throw new InvalidOperationException("パスワードは空白だけを避け、12～256文字で指定してください。");
            var salt = RandomNumberGenerator.GetBytes(16);
            options.Salt = Convert.ToBase64String(salt);
            options.PasswordHash = Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(change.Password, salt, Iterations, HashAlgorithmName.SHA256, 32));
        }
        if (change.Enabled && options.PasswordHash.Length == 0) throw new InvalidOperationException("Webコントロールを有効にするにはパスワードを設定してください。");
        options.Enabled = change.Enabled; options.Port = change.Port; options.Revision++;
        return options;
    }
    internal void Save(WebControlOptions options)
    {
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(options)); File.Move(temporary, FilePath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public WebControlStatus SaveStopped(WebControlChange change, int proxyPort)
    {
        RuntimeAccess.RequireDataDirectoryAccess();
        StoppedSettingsStore.EnsureStopped(ServiceDiagnostics.Inspect());
        new ConfigurationRepository(root).SecureDirectory();
        var repository = new ConfigurationRepository(root);
        if (File.Exists(repository.FilePath)) proxyPort = repository.LoadAsync(default).GetAwaiter().GetResult().Proxy.Port;
        var options = Prepare(change, proxyPort);
        StoppedSettingsStore.EnsureStopped(ServiceDiagnostics.Inspect());
        Save(options);
        return Public(options);
    }
    internal static bool Verify(WebControlOptions options, string password)
    {
        if (password.Length is < 1 or > 256 || options.PasswordHash.Length == 0) return false;
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(options.Salt), Iterations, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(actual, Convert.FromBase64String(options.PasswordHash));
    }
}
