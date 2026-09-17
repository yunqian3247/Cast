using cast.Core;

namespace cast.Serial;

public sealed record PortInfo(string PortName, string DisplayName, string? DeviceInstanceId = null);

public enum SerialConnectionState
{
    Closed,
    Opening,
    Open,
    Disconnected,
    Faulted
}

public sealed class SerialDataReceivedEventArgs : EventArgs
{
    public required byte[] Bytes { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class SerialConnectionErrorEventArgs : EventArgs
{
    public required string ErrorCode { get; init; }
    public required string Message { get; init; }
    public Exception? Exception { get; init; }
}

public interface IPortCatalog
{
    IReadOnlyList<PortInfo> Enumerate();
}

public interface ISerialConnection : IAsyncDisposable
{
    SerialConnectionState State { get; }
    PortInfo? Port { get; }
    event EventHandler<SerialDataReceivedEventArgs>? DataReceived;
    event EventHandler<SerialConnectionErrorEventArgs>? Error;
    event EventHandler? StateChanged;
    Task OpenAsync(SerialProfile profile, CancellationToken cancellationToken = default);
    Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default);
    Task CloseAsync(CancellationToken cancellationToken = default);
}

public sealed record SerialPinState(bool Dtr, bool Rts, bool Cts, bool Dsr);

public interface ISerialPinControl
{
    SerialPinState ReadPins();
    void SetPins(bool dtr, bool rts);
}

public sealed class PortOpenException : IOException
{
    public string ErrorCode { get; }

    public PortOpenException(string errorCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }
}

public sealed class SerialWriteException : IOException
{
    public string ErrorCode { get; }

    public SerialWriteException(string errorCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }
}
