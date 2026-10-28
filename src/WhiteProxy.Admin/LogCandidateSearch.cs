using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using WhiteProxy.Core;
using WhiteProxy.Shared;

namespace WhiteProxy.Admin;

internal sealed record LogCandidateQuery(string Peer, DateTimeOffset FromUtc, DateTimeOffset UntilUtc);
internal sealed record HostCandidate(string Host, long Count, DateTimeOffset FirstUtc, DateTimeOffset LastUtc, List<int> Ports, string Reasons);
internal sealed record LogCandidateResult(List<HostCandidate> Candidates, int Files, int UnreadableFiles, int InvalidLines, bool Truncated);

internal static class LogCandidateSearch
{
    internal const long MaximumBytes = 256L * 1024 * 1024;
    internal const int MaximumHosts = 2000;

    internal static void Validate(LogCandidateQuery query)
    {
        if (query.FromUtc >= query.UntilUtc) throw new ArgumentException("終了を開始より後の日時に指定してください。");
        if (!string.IsNullOrWhiteSpace(query.Peer)) _ = Ipv4Network.Parse(query.Peer.Trim());
    }

    internal static async Task<LogCandidateResult> SearchAsync(string root, LogCandidateQuery query, CancellationToken ct,
        IProgress<int>? progress = null, long maximumBytes = MaximumBytes)
    {
        Validate(query);
        ct.ThrowIfCancellationRequested();
        var peer = string.IsNullOrWhiteSpace(query.Peer) ? (Ipv4Network?)null : Ipv4Network.Parse(query.Peer.Trim());
        var directory = Path.Combine(root, "logs");
        if (!Directory.Exists(directory)) return new([], 0, 0, 0, false);
        var candidates = new Dictionary<string, CandidateAccumulator>(StringComparer.Ordinal);
        var files = 0; var unreadable = 0; var invalid = 0; var truncated = false; var remaining = maximumBytes;
        foreach (var path in Directory.EnumerateFiles(directory, "*.jsonl").OrderDescending())
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            if (name.Length < 14 || !DateOnly.TryParseExact(name.AsSpan(0, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                date < DateOnly.FromDateTime(query.FromUtc.UtcDateTime) || date > DateOnly.FromDateTime(query.UntilUtc.UtcDateTime)) continue;
            if (remaining <= 0) { truncated = true; break; }
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var length = Math.Min(stream.Length, remaining); truncated |= stream.Length > remaining; remaining -= length;
                await using var snapshot = new SnapshotStream(stream, length);
                using var reader = new StreamReader(snapshot, Encoding.UTF8, true, 8192, leaveOpen: true);
                files++; progress?.Report(files);
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    ct.ThrowIfCancellationRequested();
                    LogEntry? entry;
                    try { entry = JsonSerializer.Deserialize<LogEntry>(line); }
                    catch (JsonException) { invalid++; continue; }
                    if (entry is null || entry.Kind != "通信" || entry.Allowed || entry.TimeUtc < query.FromUtc || entry.TimeUtc >= query.UntilUtc || entry.Port is < 1 or > 65535) continue;
                    if (peer is { } network && (!IPAddress.TryParse(entry.Peer, out var address) || !network.Contains(address))) continue;
                    string host;
                    try { host = HostRules.Normalize(entry.Host); } catch (ArgumentException) { invalid++; continue; }
                    if (!candidates.TryGetValue(host, out var accumulator))
                    {
                        if (candidates.Count >= MaximumHosts) { truncated = true; break; }
                        candidates.Add(host, accumulator = new(host, entry.TimeUtc));
                    }
                    accumulator.Add(entry);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { unreadable++; }
            if (truncated) break;
        }
        return new(candidates.Values.Select(c => c.Build()).OrderByDescending(c => c.Count).ThenBy(c => c.Host).ToList(), files, unreadable, invalid, truncated);
    }

    internal static bool IsRegistered(string host, IEnumerable<string> patterns) => patterns.Any(pattern =>
    {
        try { return HostRules.Matches(host, pattern.Trim()); } catch (ArgumentException) { return false; }
    });

    internal static List<string> Merge(IEnumerable<string> existing, IEnumerable<string> selected)
    {
        var result = existing.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var raw in selected)
        {
            var host = HostRules.Normalize(raw);
            if (!IsRegistered(host, result)) result.Add(host);
        }
        return result;
    }

    private sealed class CandidateAccumulator(string host, DateTimeOffset time)
    {
        private long count;
        private DateTimeOffset first = time, last = time;
        private readonly SortedSet<int> ports = [];
        private readonly SortedSet<string> reasons = [];
        internal void Add(LogEntry entry)
        {
            count++; first = entry.TimeUtc < first ? entry.TimeUtc : first; last = entry.TimeUtc > last ? entry.TimeUtc : last;
            ports.Add(entry.Port); if (reasons.Count < 3 && !string.IsNullOrWhiteSpace(entry.Reason)) reasons.Add(entry.Reason);
        }
        internal HostCandidate Build() => new(host, count, first, last, ports.ToList(), string.Join(" / ", reasons));
    }

    // 読み始めた時点の長さだけを読む。サービスが追記中でも検索を引き延ばさない。
    private sealed class SnapshotStream(Stream source, long remaining) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, (int)Math.Min(count, remaining)); remaining -= read; return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await source.ReadAsync(buffer[..(int)Math.Min(buffer.Length, remaining)], cancellationToken); remaining -= read; return read;
        }
        protected override void Dispose(bool disposing) { if (disposing) source.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await source.DisposeAsync(); GC.SuppressFinalize(this); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
