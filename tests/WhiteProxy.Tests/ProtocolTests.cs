using System.Text.Json;
using WhiteProxy.Core;
using WhiteProxy.Shared;

namespace WhiteProxy.Tests;

public class ProtocolTests
{
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
