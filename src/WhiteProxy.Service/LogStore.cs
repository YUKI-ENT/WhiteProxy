using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using WhiteProxy.Shared;

namespace WhiteProxy.Service;

public sealed class LogStore : IAsyncDisposable
{
    private readonly string directory;
    private readonly Channel<LogEntry> queue = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly ConcurrentQueue<LogEntry> recent = new();
    private readonly Task writer;
    private long dropped;
    private volatile bool healthy = true;
    public bool Healthy => healthy && Dropped == 0;
    public long Dropped => Interlocked.Read(ref dropped);
    public int RetentionDays { get; set; } = 30;
    public LogStore(string root)
    {
        directory = Path.Combine(root, "logs"); Directory.CreateDirectory(directory);
        // GUIには再起動前の直近ログも表示する。1ファイルずつ、上限付きで読む。
        foreach (var path in Directory.GetFiles(directory, "*.jsonl").OrderDescending().Take(2).Reverse())
        {
            try
            {
                foreach (var line in File.ReadLines(path).TakeLast(500))
                {
                    var e = JsonSerializer.Deserialize<LogEntry>(line);
                    if (e is not null) { recent.Enqueue(e); while (recent.Count > 1000) recent.TryDequeue(out _); }
                }
            }
            catch { healthy = false; }
        }
        writer = WriteLoop();
    }
    public List<LogEntry> Snapshot() => recent.Reverse().Take(1000).ToList();
    public bool Add(LogEntry entry)
    {
        recent.Enqueue(entry); while (recent.Count > 1000) recent.TryDequeue(out _);
        if (queue.Writer.TryWrite(entry)) return true;
        Interlocked.Increment(ref dropped); healthy = false; return false;
    }
    private async Task WriteLoop()
    {
        await foreach (var entry in queue.Reader.ReadAllAsync())
        {
            try
            {
                var prefix = entry.TimeUtc.UtcDateTime.ToString("yyyyMMdd");
                var files = Directory.GetFiles(directory, prefix + "-*.jsonl").Order().ToArray();
                var path = files.LastOrDefault();
                if (path is null || new FileInfo(path).Length >= 10 * 1024 * 1024)
                    path = Path.Combine(directory, prefix + "-" + files.Length.ToString("D4") + ".jsonl");
                await File.AppendAllTextAsync(path, JsonSerializer.Serialize(entry) + "\n", new System.Text.UTF8Encoding(false));
                foreach (var file in Directory.GetFiles(directory, "*.jsonl"))
                    if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-RetentionDays)) File.Delete(file);
            }
            catch { healthy = false; Interlocked.Increment(ref dropped); }
        }
    }
    public async ValueTask DisposeAsync() { queue.Writer.TryComplete(); await writer; }
}
