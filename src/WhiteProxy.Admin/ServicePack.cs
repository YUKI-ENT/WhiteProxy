using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using WhiteProxy.Core;
using WhiteProxy.Shared;

namespace WhiteProxy.Admin;

internal sealed class ServicePack
{
    public string Format { get; set; } = "";
    public int Version { get; set; }
    public string Name { get; set; } = "";
    public List<ServicePackEntry> Services { get; set; } = [];
}

internal sealed class ServicePackEntry
{
    public string Name { get; set; } = "";
    public List<string> Hosts { get; set; } = [];
    public List<int> AllowedPorts { get; set; } = [];
    public string Source { get; set; } = "";
    public string DefinitionVersion { get; set; } = "";
    public string Notes { get; set; } = "";
}

internal sealed record ServiceImportPlan(Configuration Candidate, IReadOnlyList<ServiceDefinition> Added, IReadOnlyList<string> Skipped);

internal static class ServicePacks
{
    internal const int MaximumBytes = 1024 * 1024;
    private static string NormalizePattern(string pattern) => pattern.StartsWith("*.", StringComparison.Ordinal)
        ? "*." + HostRules.Normalize(pattern[2..]) : HostRules.Normalize(pattern);
    private static bool SameImportName(string actual, string requested) => actual.Equals(requested, StringComparison.OrdinalIgnoreCase) ||
        actual.StartsWith((requested.Length > 80 ? requested[..80] : requested) + "（インポート ", StringComparison.OrdinalIgnoreCase) && actual.EndsWith('）');

    internal static byte[] Export(IEnumerable<ServiceDefinition> selected)
    {
        var services = selected.ToList();
        if (services.Count is < 1 or > 128) throw new InvalidDataException("エクスポートするサービスを1～128件選択してください。");
        if (services.Any(s => s.Hosts.Count == 0)) throw new InvalidDataException("ホスト未登録のサービスはエクスポートできません。接続先を登録してから実行してください。");
        var pack = new ServicePack
        {
            Format = "WhiteProxy.ServicePack", Version = 1, Name = services.Count == 1 ? services[0].Name : $"許可サービス（{services.Count}件）",
            Services = services.Select(s => new ServicePackEntry { Name = s.Name, Hosts = s.Hosts.ToList(), AllowedPorts = s.AllowedPorts.ToList(),
                Source = s.Source, DefinitionVersion = s.DefinitionVersion, Notes = s.Notes }).ToList()
        };
        var options = new JsonSerializerOptions(JsonDefaults.Options) { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };
        pack = Parse(JsonSerializer.SerializeToUtf8Bytes(pack, options));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(pack, options);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("エクスポート内容が1MBを超えます。選択するサービス数を減らしてください。");
        return bytes;
    }

    internal static async Task WriteExportAsync(string path, byte[] bytes)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static ServicePack Parse(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("サービスセットは1MB以内にしてください。");
        // UTF-8 BOM付きも読み込む。
        var offset = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        ServicePack pack;
        try { pack = JsonSerializer.Deserialize<ServicePack>(bytes.AsSpan(offset), JsonDefaults.Options) ?? throw new JsonException(); }
        catch (JsonException) { throw new InvalidDataException("サービスセットのJSON形式が不正です。形式と項目名を確認してください。"); }
        if (pack.Format != "WhiteProxy.ServicePack" || pack.Version != 1 || string.IsNullOrWhiteSpace(pack.Name) || pack.Name.Length > 100 ||
            pack.Services is null || pack.Services.Count is < 1 or > 128)
            throw new InvalidDataException("format、version、name、services（1～128件）を確認してください。");
        foreach (var entry in pack.Services)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Length > 100 || entry.Hosts is null || entry.Hosts.Count is < 1 or > 1024 ||
                entry.AllowedPorts is null || entry.AllowedPorts.Count is < 1 or > 128 || entry.AllowedPorts.Any(p => p is < 1 or > 65535) ||
                entry.Source is null || entry.Source.Length > 2000 || entry.Notes is null || entry.Notes.Length > 2000 || entry.DefinitionVersion is null || entry.DefinitionVersion.Length > 100)
                throw new InvalidDataException("各サービスの名前・ホスト・TCPポート・文字数を確認してください。");
            try
            {
                entry.Hosts = entry.Hosts.Select(pattern =>
                {
                    if (pattern is null) throw new ArgumentException();
                    var wildcard = pattern.StartsWith("*.", StringComparison.Ordinal);
                    var host = HostRules.Normalize(wildcard ? pattern[2..] : pattern);
                    if (wildcard && System.Net.IPAddress.TryParse(host, out _)) throw new ArgumentException();
                    return (wildcard ? "*." : "") + host;
                }).Distinct().ToList();
            }
            catch (ArgumentException) { throw new InvalidDataException("ホストは完全一致名、または *.example.com 形式にしてください。URLやIP範囲は指定できません。"); }
            entry.AllowedPorts = entry.AllowedPorts.Distinct().Order().ToList();
        }
        return pack;
    }

    internal static ServiceImportPlan Prepare(Configuration original, ServicePack pack)
    {
        // 呼出側のDTO編集で検証を回避できないよう再検証する。
        pack = Parse(JsonSerializer.SerializeToUtf8Bytes(pack, JsonDefaults.Options));
        var candidate = JsonDefaults.Clone(original);
        var added = new List<ServiceDefinition>(); var skipped = new List<string>();
        foreach (var entry in pack.Services)
        {
            if (candidate.Services.Any(s => SameImportName(s.Name, entry.Name) && s.Hosts.Select(NormalizePattern).Order().SequenceEqual(entry.Hosts.Order()) && s.AllowedPorts.Order().SequenceEqual(entry.AllowedPorts.Order())))
            { skipped.Add(entry.Name); continue; }
            var name = entry.Name; var suffix = 2;
            while (candidate.Services.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                name = (entry.Name.Length > 80 ? entry.Name[..80] : entry.Name) + $"（インポート {suffix++}）";
            var service = new ServiceDefinition { Name = name, Hosts = entry.Hosts.ToList(), AllowedPorts = entry.AllowedPorts.ToList(),
                Source = entry.Source, DefinitionVersion = entry.DefinitionVersion, Notes = entry.Notes, Enabled = true, BuiltIn = false };
            candidate.Services.Add(service); added.Add(service);
        }
        var errors = ConfigurationValidator.Validate(candidate);
        if (errors.Count > 0) throw new InvalidDataException(string.Join("\n", errors));
        if (JsonSerializer.SerializeToUtf8Bytes(candidate, JsonDefaults.Options).Length > Protocol.MaxMessageBytes - 8192)
            throw new InvalidDataException("現在の設定と合わせたサイズが上限を超えます。取り込むサービス数を減らしてください。");
        return new(candidate, added, skipped);
    }
}
