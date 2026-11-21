using System.Text.Json;
using WhiteProxy.Core;
using WhiteProxy.Shared;

namespace WhiteProxy.Tests;

public class ProtocolTests
{
    [Theory]
    [InlineData("status")]
    [InlineData("logs")]
    [InlineData("log-search-support")]
    public async Task RequestWithoutQueryRemainsReadableByServiceWithoutLogQuerySupport(string operation)
    {
        var resolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type != typeof(IpcRequest)) return;
            var property = info.Properties.Single(p => p.Name == "logQuery");
            info.Properties.Remove(property);
        });
        var legacyOptions = new JsonSerializerOptions(JsonDefaults.Options) { TypeInfoResolver = resolver };
        using var stream = new MemoryStream();
        await Protocol.WriteAsync(stream, new IpcRequest { Operation = operation }, default);
        var json = stream.ToArray().AsSpan(4).ToArray();
        Assert.Equal(operation, JsonSerializer.Deserialize<IpcRequest>(json, legacyOptions)!.Operation);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<IpcRequest>(
            "{\"operation\":\"status\",\"logQuery\":null}", legacyOptions));
    }

    [Fact]
    public async Task LogSearchRequestStillIncludesQuery()
    {
        using var stream = new MemoryStream();
        await Protocol.WriteAsync(stream, new IpcRequest { Operation = "logs", LogQuery = new() { Kind = 1, Search = "example" } }, default);
        stream.Position = 0;
        var request = await Protocol.ReadAsync<IpcRequest>(stream, default);
        Assert.Equal(1, request.LogQuery!.Kind);
        Assert.Equal("example", request.LogQuery.Search);
    }

    [Fact]
    public async Task LengthFramingRoundTrip()
    {
        using var stream = new MemoryStream();
        await Protocol.WriteAsync(stream, new IpcRequest { Operation = "status" }, default); stream.Position = 0;
        Assert.Equal("status", (await Protocol.ReadAsync<IpcRequest>(stream, default)).Operation);
    }
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(4194305)]
    public async Task InvalidLengthIsRejectedBeforeAllocation(int length)
    {
        using var stream = new MemoryStream(BitConverter.GetBytes(length));
        await Assert.ThrowsAsync<InvalidDataException>(() => Protocol.ReadAsync<IpcRequest>(stream, default));
    }
    [Fact]
    public void SchemaRejectsUnknownFieldsAndNullRequiredLists()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Configuration>("{\"allowAll\":true}", JsonDefaults.Options));
        var c = JsonSerializer.Deserialize<Configuration>("{\"devices\":null}", JsonDefaults.Options)!;
        Assert.NotEmpty(ConfigurationValidator.Validate(c));
    }
}
