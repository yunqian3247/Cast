using System.IO.Ports;
using cast.Core;

namespace cast.Serial;

public sealed class SerialPortConnection : ISerialConnection, ISerialPinControl
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private SerialPort? _serialPort;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    private SerialConnectionState _state = SerialConnectionState.Closed;
    private bool _disposed;

    public SerialConnectionState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
        private set
        {
            lock (_sync)
            {
                _state = value;
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public PortInfo? Port { get; private set; }

    public event EventHandler<SerialDataReceivedEventArgs>? DataReceived;
    public event EventHandler<SerialConnectionErrorEventArgs>? Error;
    public event EventHandler? StateChanged;

    public SerialPinState ReadPins()
    {
        lock (_sync)
        {
            var port = _serialPort;
            if (port is null || !port.IsOpen) throw new InvalidOperationException("串口未打开");
            var hardwareFlow = port.Handshake is Handshake.RequestToSend or Handshake.RequestToSendXOnXOff;
            return new(port.DtrEnable, hardwareFlow ? false : port.RtsEnable, port.CtsHolding, port.DsrHolding);
        }
    }

    public void SetPins(bool dtr, bool rts)
    {
        lock (_sync)
        {
            var port = _serialPort;
            if (port is null || !port.IsOpen) throw new InvalidOperationException("串口未打开");
            port.DtrEnable = dtr;
            if (port.Handshake is not (Handshake.RequestToSend or Handshake.RequestToSendXOnXOff)) port.RtsEnable = rts;
        }
    }

    public async Task OpenAsync(SerialProfile profile, CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await OpenCoreAsync(profile, cancellationToken).ConfigureAwait(false); }
        finally { _lifecycleGate.Release(); }
    }

    private async Task OpenCoreAsync(SerialProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ThrowIfDisposed();

        if (profile.BaudRate <= 0 || profile.DataBits is < 5 or > 8
            || !Enum.IsDefined(profile.Parity) || !Enum.IsDefined(profile.StopBits) || !Enum.IsDefined(profile.FlowControl))
            throw new PortOpenException(ErrorCodes.OpenFailed, "串口参数无效");
        await CloseCoreAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(profile.PortName))
        {
            throw new PortOpenException(ErrorCodes.OpenFailed, "未选择串口");
        }

        State = SerialConnectionState.Opening;
        SerialPort? port = null;
        try
        {
            port = new SerialPort(profile.PortName, profile.BaudRate, ToParity(profile.Parity), profile.DataBits, ToStopBits(profile.StopBits))
            {
                Handshake = ToHandshake(profile.FlowControl), ReadTimeout = 250,
                WriteTimeout = 5000, DtrEnable = profile.DtrEnable, RtsEnable = profile.RtsEnable
            };
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(port.Open, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var readCts = new CancellationTokenSource();
            lock (_sync)
            {
                _serialPort = port;
                _readCts = readCts;
                Port = new PortInfo(profile.PortName, profile.PortName);
            }

            State = SerialConnectionState.Open;
            _readTask = Task.Run(() => ReadLoopAsync(port, readCts.Token), CancellationToken.None);
        }
        catch (UnauthorizedAccessException ex)
        {
            port?.Dispose();
            State = SerialConnectionState.Faulted;
            throw new PortOpenException(ErrorCodes.PortBusy, $"串口 {profile.PortName} 被占用或无权访问", ex);
        }
        catch (IOException ex)
        {
            port?.Dispose();
            State = SerialConnectionState.Faulted;
            throw new PortOpenException(ErrorCodes.OpenFailed, $"无法打开串口 {profile.PortName}", ex);
        }
        catch
        {
            port?.Dispose();
            State = SerialConnectionState.Faulted;
            throw;
        }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        SerialPort? port;
        lock (_sync)
        {
            port = _serialPort;
        }

        if (port is null || !port.IsOpen || State != SerialConnectionState.Open)
        {
            throw new SerialWriteException(ErrorCodes.WriteFailed, "串口未打开");
        }

        try
        {
            var write = port.BaseStream.WriteAsync(bytes, cancellationToken).AsTask();
            try
            {
                var timeout = TimeSpan.FromMilliseconds(port.WriteTimeout + bytes.Length * 12000d / port.BaudRate);
                await write.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // Some Windows drivers retain an overlapped write after cancellation.
                // Close the transport before allowing the caller to start another task.
                await CloseAsync().ConfigureAwait(false);
                _ = write.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                if (ex is OperationCanceledException) RaiseError(ErrorCodes.PortGone, "写入已取消，串口连接已关闭");
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or ObjectDisposedException)
        {
            RaiseError(ErrorCodes.WriteFailed, "串口写入失败", ex);
            throw new SerialWriteException(ErrorCodes.WriteFailed, "串口写入失败", ex);
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await CloseCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _lifecycleGate.Release(); }
    }

    private async Task CloseCoreAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? readCts;
        Task? readTask;
        SerialPort? port;
        lock (_sync)
        {
            readCts = _readCts;
            readTask = _readTask;
            port = _serialPort;
            _readCts = null;
            _readTask = null;
            _serialPort = null;
            Port = null;
        }

        readCts?.Cancel();
        if (port is not null)
        {
            try
            {
                if (port.IsOpen)
                {
                    await Task.Run(port.Close, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                RaiseError(ErrorCodes.PortGone, "关闭串口时发生异常", ex);
            }
            finally
            {
                port.Dispose();
            }
        }

        if (readTask is not null)
        {
            try
            {
                await readTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Closing the SerialPort interrupts the read loop; the state can still close deterministically.
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Read-loop errors are already surfaced through Error.
            }
        }

        readCts?.Dispose();
        if (State != SerialConnectionState.Closed)
        {
            State = SerialConnectionState.Closed;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await CloseAsync().ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(SerialPort port, CancellationToken cancellationToken)
    {
        try
        {
            await ReceiveAsync(port.BaseStream, () =>
            {
                if (!port.IsOpen) throw new IOException("串口句柄已关闭");
                // Query the driver as well: a removed device can retain a managed open handle.
                _ = port.BytesToRead;
            }, bytes => DataReceived?.Invoke(this, new SerialDataReceivedEventArgs { Bytes = bytes }), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or ObjectDisposedException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                State = SerialConnectionState.Disconnected;
                RaiseError(ErrorCodes.PortGone, $"串口连接已断开：{ex.Message}", ex);
            }
        }
    }

    internal static async Task ReceiveAsync(Stream stream, Action checkConnection, Action<byte[]> receive, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            checkConnection();
            int count;
            try { count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { count = 0; }
            if (count == 0)
            {
                // CH340 can complete idle reads immediately. A finite driver timeout avoids
                // SerialStream.EndRead treating this as an aborted infinite read.
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                continue;
            }
            receive(buffer[..count].ToArray());
        }
    }

    private void RaiseError(string code, string message, Exception? exception = null)
    {
        Error?.Invoke(this, new SerialConnectionErrorEventArgs
        {
            ErrorCode = code,
            Message = message,
            Exception = exception
        });
    }

    private static Parity ToParity(ParitySetting parity) => parity switch
    {
        ParitySetting.Odd => Parity.Odd,
        ParitySetting.Even => Parity.Even,
        ParitySetting.Mark => Parity.Mark,
        ParitySetting.Space => Parity.Space,
        _ => Parity.None
    };

    private static StopBits ToStopBits(StopBitsSetting stopBits) => stopBits switch
    {
        StopBitsSetting.OnePointFive => StopBits.OnePointFive,
        StopBitsSetting.Two => StopBits.Two,
        _ => StopBits.One
    };

    private static Handshake ToHandshake(FlowControlSetting flowControl) => flowControl switch
    {
        FlowControlSetting.RtsCts => Handshake.RequestToSend,
        FlowControlSetting.XOnXOff => Handshake.XOnXOff,
        FlowControlSetting.RtsCtsAndXOnXOff => Handshake.RequestToSendXOnXOff,
        _ => Handshake.None
    };

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
