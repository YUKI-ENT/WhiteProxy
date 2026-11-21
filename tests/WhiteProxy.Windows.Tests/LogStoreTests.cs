using System.Text.Json;
using WhiteProxy.Service;
using WhiteProxy.Shared;

namespace WhiteProxy.Windows.Tests;

public sealed class LogStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "WhiteProxy-LogStore-" + Guid.NewGuid());
    private readonly DateTimeOffset now = DateTimeOffset.UtcNow;

    private LogEntry Entry(DateTimeOffset time, bool allowed, string host = "example.com", string kind = "通信") =>
        new(time, kind, "192.168.1.2", host, 443, allowed, "reason", RequestPath: "/test");

    private void Write(DateTimeOffset day, IEnumerable<LogEntry> entries)
    {
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        File.WriteAllLines(Path.Combine(root, "logs", day.UtcDateTime.ToString("yyyyMMdd") + "-0000.jsonl"),
            entries.Select(e => JsonSerializer.Serialize(e)));
    }

    [Fact]
    public async Task FiltersWholeHistoryBeforeLimitingAndIncludesFilesOutsideRestartCache()
    {
        var old = Entry(now.AddDays(-5), true, "old.example");
        Write(now.AddDays(-5), [old]);
        Write(now.AddDays(-1), [Entry(now.AddDays(-1), false)]);
        Write(now, Enumerable.Range(0, 1100).Select(i => Entry(now.AddSeconds(-i), false)));
        await using var store = new LogStore(root);
        Assert.DoesNotContain(old, store.Snapshot());
        Assert.Equal(old, Assert.Single(await store.SearchAsync(new() { Kind = 1 }, default)));
        var denied = await store.SearchAsync(new() { Kind = 2 }, default);
        Assert.Equal(1000, denied.Count);
        Assert.Equal(now, denied[0].TimeUtc);
        Assert.Equal(now.AddSeconds(-999), denied[^1].TimeUtc);
    }

    [Fact]
    public async Task AppliesSearchDatesAndKindsAndSkipsMalformedLines()
    {
        var match = Entry(now.AddDays(-2), true, "target.example");
        Write(now.AddDays(-2), [match, Entry(now.AddDays(-2).AddMinutes(1), false, "target.example"),
            Entry(now.AddDays(-2).AddMinutes(2), true, "target.example", "監査")]);
        File.AppendAllText(Directory.GetFiles(Path.Combine(root, "logs")).Single(), "invalid json\n");
        await using var store = new LogStore(root);
        var query = new LogQuery { Search = "TARGET", Kind = 1, FromUtc = match.TimeUtc, UntilUtc = match.TimeUtc.AddMinutes(1) };
        Assert.Equal(match, Assert.Single(await store.SearchAsync(query, default)));
        query.UntilUtc = match.TimeUtc;
        Assert.Empty(await store.SearchAsync(query, default));
        Assert.Single(await store.SearchAsync(new() { Kind = 3 }, default));
    }

    [Fact]
    public async Task RecentAndPersistedEntriesAreMergedWithoutLosingIdenticalEvents()
    {
        var entry = Entry(now, true);
        await using (var store = new LogStore(root))
        {
            Assert.True(store.Add(entry)); Assert.True(store.Add(entry));
            Assert.Equal(2, (await store.SearchAsync(new() { Kind = 1 }, default)).Count);
        }
        await using var reopened = new LogStore(root);
        Assert.Equal(2, (await reopened.SearchAsync(new() { Kind = 1 }, default)).Count);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reopened.SearchAsync(new(), cancelled.Token));
    }

    public void Dispose() => Directory.Delete(root, true);
}
