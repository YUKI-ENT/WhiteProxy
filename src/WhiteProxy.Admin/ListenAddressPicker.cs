using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WhiteProxy.Admin;

internal static class ListenAddressPicker
{
    internal static List<Choice> CurrentOptions()
    {
        var result = new List<Choice> { new("127.0.0.1", "このPC内だけで利用 — 127.0.0.1") };
        var addresses = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                .Select(a => (Address: a.Address.ToString(), Adapter: n.Name))).GroupBy(a => a.Address);
        foreach (var address in addresses)
            result.Add(new(address.Key, address.Key + " — " + string.Join(" / ", address.Select(a => a.Adapter).Distinct())));
        result.Add(new("0.0.0.0", "すべてのNICで待受 — 0.0.0.0"));
        return result;
    }

    internal static ComboBox Create(string saved, IReadOnlyList<Choice> available)
    {
        var normalized = IPAddress.TryParse(saved, out var ip) ? ip.ToString() : saved;
        var choices = available.ToList();
        if (!choices.Any(c => c.Id == normalized)) choices.Add(new(normalized, "現在は未割当の保存値 — " + saved + "（選び直してください）"));
        var combo = Editors.Choices(choices, normalized);
        combo.Name = "ListenAddress"; combo.DropDownWidth = 620; combo.MaxDropDownItems = 12;
        return combo;
    }

    internal static string Hint(string? id, IReadOnlyList<Choice> available) => id switch
    {
        "127.0.0.1" => "このPC上のアプリからだけ接続できます。LAN側の端末からは接続できません。",
        "0.0.0.0" => "このPCのすべてのNICで待ち受けます。接続元の端末範囲は、許可ルールのIPv4/CIDRで指定します。",
        _ when available.Any(c => c.Id == id) => "選択したこのPCのIPで待ち受けます。接続元の端末範囲は、許可ルールのIPv4/CIDRで指定します。",
        _ => "保存されているIPは現在このPCにありません。一覧から使用する待受先を選び直してください。"
    };
}
