using WhiteProxy.Core;

namespace WhiteProxy.Admin;

internal static class DashboardRules
{
    internal static string Key(AccessPolicy rule) => rule.DeviceId + "/" + rule.ServiceId;

    internal static List<AccessPolicy> List(Configuration config, DateTimeOffset now)
    {
        var result = config.Policies.Select(JsonDefaults.Clone).ToList();
        var pairs = result.Select(p => (p.DeviceId, p.ServiceId)).ToHashSet();
        foreach (var grant in config.TemporaryGrants.Where(g => !g.Revoked && g.ExpiresAtUtc > now))
            if (pairs.Add((grant.DeviceId, grant.ServiceId)))
                result.Add(new() { DeviceId = grant.DeviceId, ServiceId = grant.ServiceId });
        return result;
    }

    internal static string ScheduleText(AccessPolicy rule) => rule.AlwaysAllow ? "全日・24時間" :
        rule.Schedules.Any(s => s.Enabled) ? string.Join(" / ", rule.Schedules.Where(s => s.Enabled).Select(s =>
            $"{string.Join('・', s.Days.Select(Editors.DayName))} {s.Start:HH:mm}～{s.End:HH:mm}{(s.End < s.Start ? "（翌日）" : "")}")) : "一時許可のみ";

    internal static Decision Evaluate(Configuration config, AccessPolicy rule, DateTimeOffset now)
    {
        var first = CoveringRule(config, rule, now) ?? rule;
        return new PolicyEngine(new SnapshotClock(now)).EvaluateRule(config, first.DeviceId, first.ServiceId);
    }

    internal static string Reason(Configuration config, AccessPolicy rule, DateTimeOffset now)
    {
        var covering = CoveringRule(config, rule, now);
        var reason = Evaluate(config, rule, now).Reason;
        if (covering is not null) return $"上位 {RuleOrder.List(config).FindIndex(p => Key(p) == Key(covering)) + 1} 行目を適用：{reason}";
        var target = config.Devices.FirstOrDefault(d => d.Id == rule.DeviceId);
        if (target is not null && RuleOrder.List(config).TakeWhile(p => Key(p) != Key(rule)).Any(p => p.ServiceId == rule.ServiceId && IsDecisive(config, p, now) &&
            config.Devices.Any(d => d.Id == p.DeviceId && Ipv4Network.Parse(d.Address).Overlaps(Ipv4Network.Parse(target.Address)))))
            reason += "（一部は上位優先）";
        return reason;
    }

    private static bool IsDecisive(Configuration config, AccessPolicy rule, DateTimeOffset now)
    {
        var device = config.Devices.FirstOrDefault(d => d.Id == rule.DeviceId);
        return device is { Enabled: true, Paused: false } &&
            (rule.Deny || new PolicyEngine(new SnapshotClock(now)).EvaluateRule(config, rule.DeviceId, rule.ServiceId).Allowed);
    }

    private static AccessPolicy? CoveringRule(Configuration config, AccessPolicy rule, DateTimeOffset now)
    {
        var target = config.Devices.FirstOrDefault(d => d.Id == rule.DeviceId);
        if (target is null) return null;
        var network = Ipv4Network.Parse(target.Address);
        return RuleOrder.List(config).TakeWhile(p => Key(p) != Key(rule)).FirstOrDefault(p => p.ServiceId == rule.ServiceId && IsDecisive(config, p, now) &&
            config.Devices.Any(d => d.Id == p.DeviceId && Ipv4Network.Parse(d.Address) is var upper && upper.Prefix <= network.Prefix && upper.Overlaps(network)));
    }

    internal static bool Move(Configuration config, string key, int direction)
    {
        if (direction is not -1 and not 1) return false;
        var index = config.Policies.FindIndex(p => Key(p) == key);
        var destination = index + direction;
        if (index < 0 || destination < 0 || destination >= config.Policies.Count) return false;
        (config.Policies[index], config.Policies[destination]) = (config.Policies[destination], config.Policies[index]);
        return true;
    }

    // insertionIndexは移動前の行と行の間（0～Count）。ドラッグ表示の挿入線と一致させる。
    internal static bool MoveTo(Configuration config, string key, int insertionIndex)
    {
        var index = config.Policies.FindIndex(p => Key(p) == key);
        if (index < 0 || insertionIndex < 0 || insertionIndex > config.Policies.Count) return false;
        var destination = insertionIndex > index ? insertionIndex - 1 : insertionIndex;
        if (destination == index) return false;
        var rule = config.Policies[index]; config.Policies.RemoveAt(index); config.Policies.Insert(destination, rule);
        return true;
    }

    internal static AccessPolicy? CloneAfter(Configuration config, string key)
    {
        var index = config.Policies.FindIndex(p => Key(p) == key);
        if (index < 0) return null;
        var source = config.Policies[index];
        var device = config.Devices.FirstOrDefault(d => d.Id == source.DeviceId);
        if (device is null) return null;
        var copy = JsonDefaults.Clone(device); copy.Id = Guid.NewGuid().ToString("N"); copy.Name = (copy.Name.Length > 90 ? copy.Name[..90] : copy.Name) + " のコピー";
        config.Devices.Add(copy);
        var rule = JsonDefaults.Clone(source); rule.DeviceId = copy.Id;
        config.Policies.Insert(index + 1, rule);
        // 期限・監査主体を持つ一時許可は複製しない。
        return rule;
    }

    internal static string NextStart(Configuration config, AccessPolicy rule, DateTimeOffset now)
    {
        if (rule.Deny || config.Proxy.GlobalBlock) return "遮断解除が必要";
        var device = config.Devices.FirstOrDefault(d => d.Id == rule.DeviceId);
        var service = config.Services.FirstOrDefault(s => s.Id == rule.ServiceId);
        if (device is null || !device.Enabled || device.Paused || service is null || !service.Enabled || service.Hosts.Count == 0 || service.AllowedPorts.Count == 0)
            return "対象・許可先の確認が必要";
        if (rule.AlwaysAllow) return "常時";
        var zone = TimeZoneInfo.FindSystemTimeZoneById(config.Proxy.TimeZoneId);
        var starts = rule.Schedules.Where(s => s.Enabled).SelectMany(s => PolicyEngine.Intervals(s, zone, now)).Select(i => i.Start)
            .Concat(config.TemporaryGrants.Where(g => !g.Revoked && g.DeviceId == rule.DeviceId && g.ServiceId == rule.ServiceId).Select(g => g.StartsAtUtc))
            .Where(t => t > now).Order().ToList();
        return starts.Count == 0 ? "予定なし" : TimeZoneInfo.ConvertTime(starts[0], zone).ToString("MM/dd HH:mm");
    }

    private sealed class SnapshotClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
