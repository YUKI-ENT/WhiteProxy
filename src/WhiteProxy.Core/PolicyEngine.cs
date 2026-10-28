using System.Net;

namespace WhiteProxy.Core;

public sealed class PolicyEngine(TimeProvider clock)
{
    public Decision Evaluate(Configuration config, IPAddress peer, string host, int port)
    {
        try
        {
            if (config.Proxy.GlobalBlock) return new(false, "管理者による全遮断");
            host = HostRules.Normalize(host);
            var devices = config.Devices.Where(d => Ipv4Network.Parse(d.Address).Contains(peer)).ToList();
            if (devices.Count == 0) return new(false, "未登録端末");
            var services = config.Services.Where(s => s.AllowedPorts.Contains(port) && s.Hosts.Any(p => HostRules.Matches(host, p))).ToList();
            if (services.Count == 0) return new(false, "未許可ホスト・ポート");
            if (services.Any(s => !s.Enabled)) return new(false, "サービス無効");
            var deviceIds = devices.Select(d => d.Id).ToHashSet();
            var serviceIds = services.Select(s => s.Id).ToHashSet();
            foreach (var policy in RuleOrder.List(config).Where(p => deviceIds.Contains(p.DeviceId) && serviceIds.Contains(p.ServiceId)))
            {
                var device = devices.First(d => d.Id == policy.DeviceId);
                if (!device.Enabled || device.Paused) continue;
                var decision = EvaluatePolicies(config, [device], [services.First(s => s.Id == policy.ServiceId)], [policy]);
                // 時間外・失効・開始前は次へ。明示的な遮断は順序どおり確定する。
                if (policy.Deny || decision.Allowed) return decision;
            }
            return new(false, "時間外・許可なし");
        }
        catch (Exception ex) when (ex is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException or InvalidOperationException)
        {
            return new(false, "設定・日時評価エラー");
        }
    }

    // ダッシュボードの時間枠判定。宛先ごとのホスト一致・DNS・重複サービスの制限は通信時に別途適用する。
    public Decision EvaluateRule(Configuration config, string deviceId, string serviceId)
    {
        try
        {
            if (config.Proxy.GlobalBlock) return new(false, "管理者による全遮断");
            var device = config.Devices.FirstOrDefault(d => d.Id == deviceId);
            var service = config.Services.FirstOrDefault(s => s.Id == serviceId);
            if (device is null || service is null) return new(false, "対象が未登録");
            if (!device.Enabled || device.Paused) return new(false, "端末無効・一時停止");
            if (!service.Enabled) return new(false, "サービス無効");
            if (service.Hosts.Count == 0 || service.AllowedPorts.Count == 0) return new(false, "許可先未設定");
            return EvaluatePolicies(config, [device], [service], config.Policies.Where(p => p.DeviceId == deviceId && p.ServiceId == serviceId).ToList());
        }
        catch (Exception ex) when (ex is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException or InvalidOperationException)
        {
            return new(false, "設定・日時評価エラー");
        }
    }

    private Decision EvaluatePolicies(Configuration config, List<Device> devices, List<ServiceDefinition> services, List<AccessPolicy> policies)
    {
        if (policies.Any(p => p.Deny)) return new(false, "手動遮断（解除まで）");
        var deviceIds = devices.Select(d => d.Id).ToHashSet();
        var serviceIds = services.Select(s => s.Id).ToHashSet();
        var now = clock.GetUtcNow();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(config.Proxy.TimeZoneId);
        var grants = new List<Decision>();
        foreach (var policy in policies)
        {
            if (policy.AlwaysAllow) grants.Add(new(true, "常時許可", policy.DeviceId, policy.ServiceId));
            foreach (var schedule in policy.Schedules.Where(s => s.Enabled))
                foreach (var interval in Intervals(schedule, zone, now))
                    if (interval.Start <= now && now < interval.End)
                        grants.Add(new(true, "定期許可", policy.DeviceId, policy.ServiceId, interval.End));
        }
        foreach (var grant in config.TemporaryGrants.Where(g => !g.Revoked && deviceIds.Contains(g.DeviceId) && serviceIds.Contains(g.ServiceId)))
            if (grant.StartsAtUtc <= now && now < grant.ExpiresAtUtc)
                grants.Add(new(true, "一時許可", grant.DeviceId, grant.ServiceId, grant.ExpiresAtUtc));
        return grants.OrderByDescending(g => g.ExpiresAtUtc ?? DateTimeOffset.MaxValue).FirstOrDefault() ?? new(false, "時間外・許可なし");
    }

    public static IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> Intervals(RecurringSchedule schedule, TimeZoneInfo zone, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        for (var offset = -1; offset <= 7; offset++)
        {
            var date = today.AddDays(offset);
            if (!schedule.Days.Contains(date.DayOfWeek) || schedule.Start == schedule.End) continue;
            var start = date.ToDateTime(schedule.Start, DateTimeKind.Unspecified);
            var end = date.AddDays(schedule.End < schedule.Start ? 1 : 0).ToDateTime(schedule.End, DateTimeKind.Unspecified);
            // 夏時間の欠落・重複する枠は安全側に倒して開放しない。
            if (zone.IsInvalidTime(start) || zone.IsInvalidTime(end) || zone.IsAmbiguousTime(start) || zone.IsAmbiguousTime(end)) continue;
            yield return (new(TimeZoneInfo.ConvertTimeToUtc(start, zone)), new(TimeZoneInfo.ConvertTimeToUtc(end, zone)));
        }
    }
}
