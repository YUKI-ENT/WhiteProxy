namespace WhiteProxy.Core;

/// <summary>壁時計と単調増加時計の乖離を検知する。一度異常になったら再起動まで閉鎖。</summary>
public sealed class ClockGuard
{
    private readonly TimeProvider clock;
    private DateTimeOffset previousUtc;
    private long previousTimestamp;
    public bool Healthy { get; private set; } = true;
    public ClockGuard(TimeProvider clock, DateTimeOffset? lastSavedUtc = null)
    {
        this.clock = clock; previousUtc = clock.GetUtcNow(); previousTimestamp = clock.GetTimestamp();
        if (lastSavedUtc.HasValue && previousUtc < lastSavedUtc.Value.AddSeconds(-5)) Healthy = false;
    }
    public bool Check()
    {
        var utc = clock.GetUtcNow(); var timestamp = clock.GetTimestamp();
        var elapsed = clock.GetElapsedTime(previousTimestamp, timestamp);
        if (utc < previousUtc.AddSeconds(-2) || Math.Abs(((utc - previousUtc) - elapsed).TotalSeconds) > 120) Healthy = false;
        previousUtc = utc; previousTimestamp = timestamp;
        return Healthy;
    }
}
