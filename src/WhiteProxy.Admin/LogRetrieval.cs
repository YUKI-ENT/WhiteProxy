using WhiteProxy.Shared;

namespace WhiteProxy.Admin;

internal sealed record LogRetrievalResult(IpcResponse Response, bool SavedHistory);

internal static class LogRetrieval
{
    internal static async Task<LogRetrievalResult> ReadAsync(
        Func<IpcRequest, CancellationToken, Task<IpcResponse>> send, LogQuery query, CancellationToken ct)
    {
        // 旧サービスは未知のJSONプロパティを拒否する。追加項目なしの要求で対応を確認する。
        var support = await send(new() { Operation = "log-search-support" }, ct);
        var savedHistory = support.Success && support.Message == Protocol.LogSearchCapability;
        var response = await send(new() { Operation = "logs", LogQuery = savedHistory ? query : null }, ct);
        return new(response, savedHistory);
    }
}
