namespace cast.Serial;

public sealed class PortPresenceMonitor : IDisposable
{
    private readonly IPortCatalog _catalog;
    private readonly Timer _timer;
    private readonly object _sync = new();
    private IReadOnlyList<PortInfo> _ports = [];
    private bool _disposed;
    private int _polling;

    public event EventHandler<PortsChangedEventArgs>? Changed;
    public event EventHandler<Exception>? EnumerationFailed;

    public PortPresenceMonitor(IPortCatalog catalog, TimeSpan? interval = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        try { _ports = catalog.Enumerate().ToArray(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Startup reports enumeration errors in the UI; the monitor retries on its next poll.
        }
        _timer = new Timer(Poll, null, interval ?? TimeSpan.FromSeconds(1), interval ?? TimeSpan.FromSeconds(1));
    }

    public IReadOnlyList<PortInfo> GetCurrent() => _catalog.Enumerate();

    private void Poll(object? state)
    {
        if (_disposed || Interlocked.Exchange(ref _polling, 1) != 0)
        {
            return;
        }

        try
        {
            var current = _catalog.Enumerate();
            var names = current.Select(static p => p.PortName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string[] added;
            string[] removed;
            lock (_sync)
            {
                if (_ports.OrderBy(static p => p.PortName).SequenceEqual(current.OrderBy(static p => p.PortName)))
                {
                    return;
                }

                var previousNames = _ports.Select(static p => p.PortName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                added = names.Except(previousNames, StringComparer.OrdinalIgnoreCase).ToArray();
                removed = previousNames.Except(names, StringComparer.OrdinalIgnoreCase).ToArray();
                _ports = current.ToArray();
            }

            Changed?.Invoke(this, new PortsChangedEventArgs(current, added, removed));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            EnumerationFailed?.Invoke(this, ex);
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }
}

public sealed class PortsChangedEventArgs : EventArgs
{
    public IReadOnlyList<PortInfo> Current { get; }
    public IReadOnlyList<string> Added { get; }
    public IReadOnlyList<string> Removed { get; }

    public PortsChangedEventArgs(IReadOnlyList<PortInfo> current, IReadOnlyList<string> added, IReadOnlyList<string> removed)
    {
        Current = current;
        Added = added;
        Removed = removed;
    }
}
