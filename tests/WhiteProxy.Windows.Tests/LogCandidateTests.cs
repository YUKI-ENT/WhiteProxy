using System.Reflection;
using System.Text;
using System.Text.Json;
using WhiteProxy.Admin;
using WhiteProxy.Core;
using WhiteProxy.Service;
using WhiteProxy.Shared;

namespace WhiteProxy.Windows.Tests;

public class LogCandidateTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "WhiteProxy-candidates-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-28T03:00:00Z");
    public LogCandidateTests() => Directory.CreateDirectory(Path.Combine(root, "logs"));
    private static LogEntry Entry(string host = "example.com", string peer = "192.168.10.21", int port = 443, DateTimeOffset? time = null,
        string kind = "通信", bool allowed = false) => new(time ?? Now, kind, peer, host, port, allowed, "未許可ホスト・ポート");
    private static string Line(LogEntry entry) => JsonSerializer.Serialize(entry) + "\n";
    private string Write(string name, IEnumerable<LogEntry> entries)
    {
        var path = Path.Combine(root, "logs", name);
        File.WriteAllText(path, string.Concat(entries.Select(Line)), new UTF8Encoding(false)); return path;
    }

    [Fact]
    public async Task ReadsPersistedArchiveBeyondRecentThousandEntries()
    {
        await using (var store = new LogStore(root))
        {
            Assert.True(store.Add(Entry("first.example")));
            for (var i = 0; i < 1004; i++) Assert.True(store.Add(Entry("recent.example")));
            Assert.DoesNotContain(store.Snapshot(), e => e.Host == "first.example");
        }
        var result = await LogCandidateSearch.SearchAsync(root, new("192.168.10.21", Now, Now.AddHours(1)), default);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal(1, result.Candidates.Single(c => c.Host == "first.example").Count);
        Assert.Equal(1004, result.Candidates.Single(c => c.Host == "recent.example").Count);
    }

    [Fact]
    public async Task FiltersIpAndHalfOpenPeriodAndIgnoresAllowedOrAuditEntries()
    {
        Write("20261028-0000.jsonl", [Entry("EXAMPLE.COM."), Entry(peer: "::ffff:192.168.10.21", port: 80, time: Now.AddMinutes(1)),
            Entry(peer: "192.168.10.22"), Entry("other.example", "192.168.11.22"), Entry(time: Now.AddSeconds(-1)),
            Entry(time: Now.AddMinutes(10)), Entry(allowed: true), Entry(kind: "監査")]);
        var result = await LogCandidateSearch.SearchAsync(root, new("192.168.10.21", Now, Now.AddMinutes(10)), default);
        var host = Assert.Single(result.Candidates);
        Assert.Equal("example.com", host.Host); Assert.Equal(2, host.Count); Assert.Equal(new[] { 80, 443 }, host.Ports);
        Assert.Equal(Now, host.FirstUtc); Assert.Equal(Now.AddMinutes(1), host.LastUtc);
        var subnet = await LogCandidateSearch.SearchAsync(root, new("192.168.10.0/24", Now, Now.AddMinutes(10)), default);
        Assert.Equal(3, Assert.Single(subnet.Candidates).Count);
        var all = await LogCandidateSearch.SearchAsync(root, new("", Now, Now.AddMinutes(10)), default);
        Assert.Equal(2, all.Candidates.Count);
    }

    [Fact]
    public async Task UsesUtcFileDatesAcrossLocalDateBoundaries()
    {
        var beforeMidnight = DateTimeOffset.Parse("2026-10-28T23:59:00Z");
        Write("20261028-0000.jsonl", [Entry(time: beforeMidnight)]);
        Write("20261029-0000.jsonl", [Entry(time: beforeMidnight.AddMinutes(1))]);
        Write("20261027-0000.jsonl", [Entry(time: beforeMidnight.AddDays(-1))]);
        var result = await LogCandidateSearch.SearchAsync(root, new("", beforeMidnight, beforeMidnight.AddMinutes(2)), default);
        Assert.Equal(2, result.Files); Assert.Equal(2, Assert.Single(result.Candidates).Count);
    }

    [Fact]
    public async Task ReportsMalformedLinesAndUnavailableFileWithoutHidingValidCandidates()
    {
        var path = Write("20261028-0000.jsonl", [Entry(), Entry("https://bad.example/path")]);
        await File.AppendAllTextAsync(path, "{bad\n{\n");
        var lockedPath = Write("20261028-0001.jsonl", [Entry("locked.example")]);
        using var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = await LogCandidateSearch.SearchAsync(root, new("", Now, Now.AddHours(1)), default);
        Assert.Equal("example.com", Assert.Single(result.Candidates).Host);
        Assert.Equal(3, result.InvalidLines); Assert.Equal(1, result.UnreadableFiles);
    }

    [Fact]
    public async Task PartialSearchIsExplicitAndCancellationDoesNotReturnCompleteResult()
    {
        var first = Entry("first.example"); Write("20261028-0000.jsonl", [first, Entry("last.example")]);
        var result = await LogCandidateSearch.SearchAsync(root, new("", Now, Now.AddHours(1)), default, maximumBytes: Encoding.UTF8.GetByteCount(Line(first)));
        Assert.True(result.Truncated); Assert.Equal("first.example", Assert.Single(result.Candidates).Host);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LogCandidateSearch.SearchAsync(root, new("", Now, Now.AddHours(1)), stop.Token));
    }

    [Fact]
    public void MergingKeepsExactHostsAndDoesNotExpandWildcardOrPorts()
    {
        var merged = LogCandidateSearch.Merge(["*.example.com"], ["a.example.com", "example.com", "EXAMPLE.COM.", "other.test"]);
        Assert.Equal(new[] { "*.example.com", "example.com", "other.test" }, merged);
        Assert.Throws<ArgumentException>(() => LogCandidateSearch.Validate(new("not-an-ip", Now, Now.AddHours(1))));
        Assert.Throws<ArgumentException>(() => LogCandidateSearch.Validate(new("", Now, Now)));
    }

    [Fact]
    public async Task CandidateButtonOnlyChangesDraftHostTextUntilParentIsSaved()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var service = new ServiceDefinition { Hosts = ["*.example.com"], AllowedPorts = [443] };
                using var form = Editors.CreateServiceDialog(service, chooseHosts: (_, existing) => ["a.example.com", "new.test"]);
                var controls = Descendants(form).ToList();
                var button = controls.OfType<Button>().Single(b => b.Name == "ExtractLogHosts");
                typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, [EventArgs.Empty]);
                Assert.Equal(new[] { "*.example.com", "new.test" }, controls.OfType<TextBox>().Single(b => b.Name == "ServiceHosts").Lines);
                Assert.Equal(new[] { "*.example.com" }, service.Hosts); Assert.Equal(new[] { 443 }, service.AllowedPorts);
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static IEnumerable<Control> Descendants(Control control)
    {
        foreach (Control child in control.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    public void Dispose()
    {
        // このテストが作成した一意の一時ディレクトリだけを削除する。
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
