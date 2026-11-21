using System.Text.Json;
using WhiteProxy.Admin;
using WhiteProxy.Core;
using WhiteProxy.Shared;

namespace WhiteProxy.Windows.Tests;

public class LogRetrievalTests
{
    [Fact]
    public async Task LegacyServiceReceivesNoQueryAndRecentLogsRemainVisible()
    {
        var requests = new List<string>();
        var entry = new LogEntry(DateTimeOffset.UtcNow, "通信", "pc", "example.com", 443, true, "allowed");
        Task<IpcResponse> LegacyService(IpcRequest request, CancellationToken ct)
        {
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(request, JsonDefaults.Options));
            Assert.False(json.RootElement.TryGetProperty("logQuery", out _));
            requests.Add(request.Operation);
            return Task.FromResult(request.Operation == "logs"
                ? new IpcResponse { Success = true, Logs = [entry] }
                : new IpcResponse { Success = false, Message = "管理者として起動してください。" });
        }
        var result = await LogRetrieval.ReadAsync(LegacyService, new() { Kind = 1 }, default);
        Assert.False(result.SavedHistory);
        Assert.True(result.Response.Success);
        Assert.Equal(entry, Assert.Single(result.Response.Logs));
        Assert.Equal(new[] { "log-search-support", "logs" }, requests);
    }

    [Fact]
    public async Task SupportingServiceReceivesFullSearchConditions()
    {
        var query = new LogQuery { Kind = 2, Search = "example", FromUtc = DateTimeOffset.UtcNow.AddDays(-30) };
        Task<IpcResponse> Service(IpcRequest request, CancellationToken ct)
        {
            if (request.Operation == "log-search-support")
            {
                Assert.Null(request.LogQuery);
                return Task.FromResult(new IpcResponse { Success = true, Message = Protocol.LogSearchCapability });
            }
            Assert.Equal("logs", request.Operation);
            Assert.Same(query, request.LogQuery);
            return Task.FromResult(new IpcResponse { Success = true, Message = "保存ログ検索" });
        }
        var result = await LogRetrieval.ReadAsync(Service, query, default);
        Assert.True(result.SavedHistory); Assert.True(result.Response.Success);
    }

    [Fact]
    public async Task ConnectionFailureIsNotReportedAsSuccessfulEmptyHistory()
    {
        await Assert.ThrowsAsync<IOException>(() => LogRetrieval.ReadAsync(
            (_, _) => throw new IOException("disconnected"), new(), default));
    }
}
