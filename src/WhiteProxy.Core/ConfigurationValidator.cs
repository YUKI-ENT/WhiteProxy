using System.Net;
using System.Net.Sockets;

namespace WhiteProxy.Core;

public static class ConfigurationValidator
{
    public static List<string> Validate(Configuration config)
    {
        var errors = new List<string>();
        try
        {
            if (config.SchemaVersion != 2) errors.Add("未対応のスキーマです。順序付きルール対応のサービスへ更新してください。");
            if (!IPAddress.TryParse(config.Proxy.ListenAddress, out var listen) || listen.AddressFamily != AddressFamily.InterNetwork)
                errors.Add("待受IPは IPv4 を指定してください（IPv6待受は未対応）。");
            if (config.Proxy.Port is < 1 or > 65535) errors.Add("ポートは1〜65535です。");
            if (config.Proxy.MaximumGrantHours is < 1 or > 24) errors.Add("一時許可の上限は1〜24時間です。");
            if (config.Proxy.LogRetentionDays is < 1 or > 365) errors.Add("ログ保持日数は1〜365です。");
            if (config.Proxy.MaxConnections is < 1 or > 4096) errors.Add("最大接続数は1〜4096です。");
            _ = TimeZoneInfo.FindSystemTimeZoneById(config.Proxy.TimeZoneId);
            if (config.Devices.Count > 4096 || config.Services.Count > 1024 || config.Policies.Count > 32768 || config.TemporaryGrants.Count > 50000)
                errors.Add("設定件数の上限を超えています。");
            CheckIds(config.Devices.Select(d => d.Id), errors);
            CheckIds(config.Services.Select(s => s.Id), errors);
            CheckIds(config.TemporaryGrants.Select(g => g.Id), errors);
            var nets = new List<Ipv4Network>();
            foreach (var device in config.Devices)
            {
                CheckText(device.Name, 100); CheckText(device.Group, 100);
                var net = Ipv4Network.Parse(device.Address);
                nets.Add(net);
            }
            foreach (var service in config.Services)
            {
                CheckText(service.Name, 100); CheckText(service.Notes, 2000); CheckText(service.Source, 2000);
                if (service.Hosts.Count > 1024 || service.AllowedPorts.Count > 128 || service.AllowedPorts.Any(p => p is < 1 or > 65535))
                    errors.Add("ホスト・ポートの数または値が不正です: " + service.Name);
                foreach (var pattern in service.Hosts)
                {
                    var raw = pattern.StartsWith("*.", StringComparison.Ordinal) ? pattern[2..] : pattern;
                    var normalized = HostRules.Normalize(raw);
                    if (pattern.StartsWith("*.") && IPAddress.TryParse(normalized, out _)) errors.Add("IPにワイルドカードは使えません。");
                }
            }
            var keys = new HashSet<string>();
            foreach (var p in config.Policies)
            {
                CheckPair(p.DeviceId, p.ServiceId, config);
                if (!keys.Add(p.DeviceId + "/" + p.ServiceId)) errors.Add("端末×サービスのポリシーが重複しています。");
                if (p.Schedules.Count > 100) errors.Add("スケジュールが多すぎます。");
                foreach (var s in p.Schedules)
                    if (s.Start == s.End || s.Days.Count == 0 || s.Days.Any(d => !Enum.IsDefined(d))) errors.Add("曜日・開始・終了時刻を確認してください。");
            }
            foreach (var g in config.TemporaryGrants)
            {
                CheckPair(g.DeviceId, g.ServiceId, config);
                CheckText(g.Reason, 500); CheckText(g.CreatedBy, 256);
                if (g.ExpiresAtUtc <= g.StartsAtUtc || g.ExpiresAtUtc - g.StartsAtUtc > TimeSpan.FromHours(24) ||
                    g.StartsAtUtc.Offset != TimeSpan.Zero || g.ExpiresAtUtc.Offset != TimeSpan.Zero)
                    errors.Add("一時許可はUTCで1枠24時間以内、期限必須です。");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException or NullReferenceException)
        { errors.Add("設定値が不正です。必須項目・ホスト・IP・タイムゾーンを確認してください。"); }
        return errors;
    }
    private static void CheckIds(IEnumerable<string> ids, List<string> errors)
    {
        var seen = new HashSet<string>();
        foreach (var id in ids)
            if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.') || !seen.Add(id))
                errors.Add("IDは64文字以内の英数字・ハイフン・アンダースコア・ドットを使用し、重複しないようにしてください。");
    }
    private static void CheckText(string text, int max)
    {
        if (text is null || text.Length > max || text.Any(c => char.IsControl(c) && c != '\n' && c != '\r')) throw new ArgumentException();
    }
    private static void CheckPair(string deviceId, string serviceId, Configuration c)
    {
        if (!c.Devices.Any(d => d.Id == deviceId) || !c.Services.Any(s => s.Id == serviceId)) throw new ArgumentException();
    }
}
