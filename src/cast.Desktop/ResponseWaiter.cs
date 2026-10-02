using cast.Core;

namespace cast.Desktop;

internal sealed class ResponseWaiter
{
    private readonly byte[] _match;
    private readonly int[] _prefix;
    private int _matched;
    public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ResponseWaiter(string match, string format, TextEncodingKind encoding)
    {
        _match = format == "hex" ? HexCodec.Parse(match) : TextCodec.Encode(match, encoding);
        if (_match.Length == 0) throw new FormatException("应答规则为空");
        _prefix = new int[_match.Length];
        for (var i = 1; i < _match.Length; i++)
        {
            var prefix = _prefix[i - 1];
            while (prefix > 0 && _match[i] != _match[prefix]) prefix = _prefix[prefix - 1];
            if (_match[i] == _match[prefix]) prefix++;
            _prefix[i] = prefix;
        }
    }

    public void Feed(byte[] bytes)
    {
        if (Completion.Task.IsCompleted) return;
        foreach (var value in bytes)
        {
            while (_matched > 0 && value != _match[_matched]) _matched = _prefix[_matched - 1];
            if (value == _match[_matched]) _matched++;
            if (_matched == _match.Length) { Completion.TrySetResult(); return; }
        }
    }
}
