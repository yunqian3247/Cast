using System.Threading.Channels;
using cast.Core;
using cast.Serial;

namespace cast.Tests;

public sealed class SerialConnectionTests
{
    [Theory]
    [InlineData(0, 8)]
    [InlineData(115200, 9)]
    public async Task InvalidParametersFailBeforeOpeningDriver(int baudRate, int dataBits)
    {
        await using var connection = new SerialPortConnection();
        var error = await Assert.ThrowsAsync<PortOpenException>(() => connection.OpenAsync(new SerialProfile { PortName = "COM9999", BaudRate = baudRate, DataBits = dataBits }));
        Assert.Equal(ErrorCodes.OpenFailed, error.ErrorCode);
        Assert.Equal(SerialConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task ClosedConnectionRejectsSendAndClosesIdempotently()
    {
        await using var connection = new SerialPortConnection();
        await Assert.ThrowsAsync<SerialWriteException>(() => connection.SendAsync(new byte[] { 1 }));
        await Task.WhenAll(connection.CloseAsync(), connection.CloseAsync());
        Assert.Equal(SerialConnectionState.Closed, connection.State);
        Assert.Null(connection.Port);
    }

    [SerialPairFact]
    public async Task ConfiguredSerialPairTransfersBothDirectionsAndReopens()
    {
        var firstPort = Environment.GetEnvironmentVariable("CAST_TEST_PORT_A")!;
        var secondPort = Environment.GetEnvironmentVariable("CAST_TEST_PORT_B")!;
        Assert.False(firstPort.Equals(secondPort, StringComparison.OrdinalIgnoreCase));
        await using var first = new SerialPortConnection();
        await using var second = new SerialPortConnection();
        var firstReceived = Channel.CreateUnbounded<byte>();
        var secondReceived = Channel.CreateUnbounded<byte>();
        first.DataReceived += (_, e) => { foreach (var value in e.Bytes) firstReceived.Writer.TryWrite(value); };
        second.DataReceived += (_, e) => { foreach (var value in e.Bytes) secondReceived.Writer.TryWrite(value); };
        await first.OpenAsync(new SerialProfile { PortName = firstPort });
        await second.OpenAsync(new SerialProfile { PortName = secondPort });
        var payload = Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray();
        await first.SendAsync(payload);
        Assert.Equal(payload, await ReadBytes(secondReceived.Reader, payload.Length));
        await second.SendAsync(new byte[] { 0x41, 0, 0xFF });
        Assert.Equal(new byte[] { 0x41, 0, 0xFF }, await ReadBytes(firstReceived.Reader, 3));
        await first.CloseAsync();
        await first.OpenAsync(new SerialProfile { PortName = firstPort });
        await first.SendAsync(new byte[] { 0x55, 0xAA });
        Assert.Equal(new byte[] { 0x55, 0xAA }, await ReadBytes(secondReceived.Reader, 2));
    }

    private static async Task<byte[]> ReadBytes(ChannelReader<byte> reader, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = new byte[count];
        for (var i = 0; i < count; i++) result[i] = await reader.ReadAsync(timeout.Token);
        return result;
    }
}

public sealed class SerialPairFactAttribute : FactAttribute
{
    public SerialPairFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CAST_TEST_PORT_A"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CAST_TEST_PORT_B")))
            Skip = "Set CAST_TEST_PORT_A and CAST_TEST_PORT_B to a connected cast pair.";
    }
}
