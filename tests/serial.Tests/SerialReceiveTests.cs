using System.Collections.Concurrent;
using serial.Core;
using serial.Serial;

namespace serial.Tests;

public sealed class SerialReceiveTests
{
    [Fact]
    public async Task IdleReadsAndTimeoutsAllowLaterData()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var stream = new ReadScript(new byte[0], new TimeoutException(), new byte[] { 0, 0x55, 0xFF }, new byte[] { 0x41 });
        var received = new List<byte[]>();
        var checks = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SerialPortConnection.ReceiveAsync(stream, () => checks++, bytes =>
        {
            received.Add(bytes);
            if (received.Count == 2) stop.Cancel();
        }, stop.Token));
        Assert.Equal(4, checks);
        Assert.Equal(new byte[] { 0, 0x55, 0xFF }, received[0]);
        Assert.Equal(new byte[] { 0x41 }, received[1]);
    }

    [Fact]
    public async Task RepeatedEmptyReadsYieldAndCancel()
    {
        using var stop = new CancellationTokenSource();
        using var stream = new ReadScript();
        var received = 0;
        var running = SerialPortConnection.ReceiveAsync(stream, () => { }, _ => received++, stop.Token);
        Assert.Equal(1, stream.ReadCount);
        Assert.False(running.IsCompleted);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(0, received);
    }

    [Fact]
    public async Task ReadFailureKeepsUnderlyingError()
    {
        var failure = new IOException("device removed");
        using var stream = new ReadScript(failure);
        var actual = await Assert.ThrowsAsync<IOException>(() => SerialPortConnection.ReceiveAsync(stream, () => { }, _ => { }, default));
        Assert.Same(failure, actual);
        Assert.Equal(1, stream.ReadCount);
    }

    [Fact]
    public async Task DriverHealthFailureStopsIdleReceive()
    {
        using var stream = new ReadScript();
        var checks = 0;
        await Assert.ThrowsAsync<IOException>(() => SerialPortConnection.ReceiveAsync(stream, () =>
        {
            if (++checks == 2) throw new IOException("invalid handle");
        }, _ => { }, default));
        Assert.Equal(1, stream.ReadCount);
    }

    [SerialIdleFact]
    public async Task ConfiguredSerialPortStaysOpenWhileIdleAndReopens()
    {
        await using var connection = new SerialPortConnection();
        var errors = new ConcurrentQueue<SerialConnectionErrorEventArgs>();
        connection.Error += (_, e) => errors.Enqueue(e);
        var profile = new SerialProfile { PortName = Environment.GetEnvironmentVariable("SERIAL_IDLE_TEST_PORT")!, BaudRate = 9600 };
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await connection.OpenAsync(profile).WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(TimeSpan.FromSeconds(5));
            Assert.Equal(SerialConnectionState.Open, connection.State);
            Assert.Empty(errors);
            _ = connection.ReadPins();
            await connection.CloseAsync().WaitAsync(TimeSpan.FromSeconds(4));
            Assert.Equal(SerialConnectionState.Closed, connection.State);
        }
    }

    private sealed class ReadScript(params object[] results) : Stream
    {
        private readonly Queue<object> _results = new(results);
        public int ReadCount { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            var result = _results.Count > 0 ? _results.Dequeue() : Array.Empty<byte>();
            if (result is Exception exception) return ValueTask.FromException<int>(exception);
            var bytes = (byte[])result;
            bytes.CopyTo(buffer);
            return ValueTask.FromResult(bytes.Length);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed class SerialIdleFactAttribute : FactAttribute
{
    public SerialIdleFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SERIAL_IDLE_TEST_PORT")))
            Skip = "Set SERIAL_IDLE_TEST_PORT to a serial port available for receive-only checks.";
    }
}
