namespace WhiteProxy.Core;

public static class RuleOrder
{
    // 旧データの一時許可だけの組合せは末尾に補う。新しい許可は作らない。
    public static List<AccessPolicy> List(Configuration config)
    {
        var rules = config.Policies.ToList();
        var pairs = rules.Select(p => (p.DeviceId, p.ServiceId)).ToHashSet();
        foreach (var grant in config.TemporaryGrants)
            if (pairs.Add((grant.DeviceId, grant.ServiceId)))
                rules.Add(new() { DeviceId = grant.DeviceId, ServiceId = grant.ServiceId });
        return rules;
    }

    public static Configuration Upgrade(Configuration config)
    {
        if (config.SchemaVersion != 1) return config;
        var next = JsonDefaults.Clone(config);
        // 旧方式の「拒否優先」を移行時に保つ。既存の許可同士の相対順序は保つ。
        next.Policies = RuleOrder.List(next).OrderByDescending(p => p.Deny).ToList();
        next.SchemaVersion = 2;
        return next;
    }
}
