using System.Text.Json;

namespace WhiteProxy.Service;

// 最初の試行 + 最大7回。設定破損や権限不足は待機しても直らない。
internal sealed class StartupRetry
{
    private static readonly int[] Delays = [2, 4, 8, 15, 30, 30, 30];
    private int failures;
    internal DateTimeOffset? NextAttemptUtc { get; private set; }
    internal bool Pending => NextAttemptUtc.HasValue;
    internal TimeSpan? Failed(Exception exception, DateTimeOffset now)
    {
        NextAttemptUtc = null;
        var cause = exception.GetBaseException();
        if (cause is UnauthorizedAccessException or System.Security.SecurityException or InvalidDataException or JsonException or ArgumentException || failures >= Delays.Length)
            return null;
        var delay = TimeSpan.FromSeconds(Delays[failures++]);
        NextAttemptUtc = now + delay;
        return delay;
    }
    internal void Complete() => NextAttemptUtc = null;
}
