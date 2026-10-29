using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using WhiteProxy.Core;

namespace WhiteProxy.Admin;

internal sealed record FirewallPlan(string Executable, string LocalAddress, int Port, List<string> Sources, string Explanation)
{
    internal bool AllowInbound => Sources.Count > 0;
    internal const int Profiles = 3; // Domain | Private。Publicへ自動開放しない。
}

internal static class FirewallManager
{
    internal const string RuleName = "WhiteProxy-Managed-Inbound";
    internal const string WebRuleName = "WhiteProxy-Web-Control";
    internal static string ExecutablePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WhiteProxy", "WhiteProxy.exe");

    internal static FirewallPlan Plan(Configuration config)
    {
        var errors = ConfigurationValidator.Validate(config);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
        var local = IPAddress.Parse(config.Proxy.ListenAddress);
        if (IPAddress.IsLoopback(local)) return new(ExecutablePath, local.ToString(), config.Proxy.Port, [], "ループバック待受のため外部向け許可は不要です。");
        var sources = new List<string>(); var skippedAny = false;
        foreach (var device in config.Devices.Where(d => d.Enabled))
        {
            var network = Ipv4Network.Parse(device.Address);
            if (network.Prefix == 0) { skippedAny = true; continue; }
            var ip = new IPAddress(new byte[] { (byte)(network.Address >> 24), (byte)(network.Address >> 16), (byte)(network.Address >> 8), (byte)network.Address });
            if (IPAddress.IsLoopback(ip)) continue;
            sources.Add(ip + "/" + network.Prefix.ToString(CultureInfo.InvariantCulture));
        }
        sources = sources.Distinct().ToList();
        var explanation = sources.Count == 0 ? "送信元を限定できる登録端末がないため、受信許可は作成していません。" :
            $"TCP {config.Proxy.Port} / 登録端末 {sources.Count} 範囲 / ドメイン・プライベートのみ許可。";
        if (skippedAny) explanation += " 全IPv4（/0）は自動許可の対象外です。限定した送信元を登録してください。";
        return new(ExecutablePath, local.Equals(IPAddress.Any) ? "*" : local.ToString(), config.Proxy.Port, sources, explanation);
    }

    internal static Task<string> SyncAsync(Configuration config) => Task.Run(() =>
    {
        var plan = Plan(config); Apply(plan); return "Windows Firewall：" + plan.Explanation;
    });

    internal static bool ScopeChanged(Configuration before, Configuration after)
    {
        var first = Plan(before); var second = Plan(after);
        return first.LocalAddress != second.LocalAddress || first.Port != second.Port || !first.Sources.Order().SequenceEqual(second.Sources.Order());
    }

    internal static Task RemoveAsync() => Task.Run(() => { Apply(null); Apply(null, WebRuleName); });
    internal static Task<string> SyncWebAsync(bool enabled, int port) => Task.Run(() =>
    {
        if (port is < 1 or > 65535) throw new InvalidOperationException("Webポートが不正です。");
        Apply(enabled ? new(ExecutablePath, "*", port, ["LocalSubnet"], "") : null, WebRuleName);
        return enabled ? $"Web管理の受信許可：TCP {port} / ローカルサブネット / ドメイン・プライベート。" : "Web管理の受信許可を削除しました。";
    });

    internal static object CreateRule(FirewallPlan plan, string name = RuleName)
    {
        var ruleObject = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: true)!)!;
        try
        {
            dynamic rule = ruleObject;
            rule.Name = name; rule.Description = name == WebRuleName ? "WhiteProxyのHTTPS管理画面。ローカルサブネット内のみ。パスワード認証必須。" : "WhiteProxyが管理する受信許可。登録端末の送信元IP/CIDRに限定。";
            rule.ApplicationName = plan.Executable;
            rule.Protocol = 6; rule.LocalPorts = plan.Port.ToString(CultureInfo.InvariantCulture);
            rule.LocalAddresses = plan.LocalAddress; rule.RemoteAddresses = string.Join(',', plan.Sources);
            rule.Direction = 1; rule.Profiles = FirewallPlan.Profiles; rule.Action = 1; rule.EdgeTraversal = false; rule.Enabled = true;
            return ruleObject;
        }
        catch { Marshal.FinalReleaseComObject(ruleObject); throw; }
    }

    private static void Apply(FirewallPlan? plan, string ruleName = RuleName)
    {
        object? policyObject = null, rulesObject = null, ruleObject = null;
        try
        {
            policyObject = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!);
            dynamic policy = policyObject!; rulesObject = policy.Rules; dynamic rules = rulesObject;
            if (plan is { AllowInbound: true })
            {
                ruleObject = CreateRule(plan, ruleName);
            }
            // 他アプリや手動作成ルールには触れない。作り直し失敗時も広い旧ルールを残さない。
            try { rules.Remove(ruleName); }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x80070002)) { }
            if (ruleObject is not null) rules.Add((dynamic)ruleObject);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or System.Security.SecurityException)
        { throw new InvalidOperationException($"Windows Firewallの自動設定に失敗しました（{ex.GetType().Name}, 0x{ex.HResult:X8}）。管理者権限と組織のFirewallポリシーを確認してください。", ex); }
        finally
        {
            foreach (var instance in new[] { ruleObject, rulesObject, policyObject })
                if (instance is not null && Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance);
        }
    }
}
