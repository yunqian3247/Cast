using System.Text;
using cast.Core;
using Xunit;

namespace cast.Tests;

public sealed class ReceiveFramerTests
{
    [Fact]
    public void AutoExtractsModbusAcrossEverySplitAndConcatenatedFrames()
    {
        var frame = HexCodec.Parse("01 03 00 00 00 02 C4 0B");
        for (var split = 1; split < frame.Length; split++)
        {
            var framer = new ReceiveFramer(new("auto"));
            Assert.Empty(framer.Feed(frame.AsSpan(0, split)));
            Assert.Equal(frame, Assert.Single(framer.Feed(frame.AsSpan(split))));
        }
        var together = new ReceiveFramer(new("auto")).Feed([.. frame, .. frame]);
        Assert.Equal(2, together.Count);
        Assert.All(together, actual => Assert.Equal(frame, actual));
    }

    [Fact]
    public void AutoHandlesKnownFramesNewlinesInvalidCrcAndResidual()
    {
        var known = HexCodec.Parse("A5 FA 81 00 60 80 FB");
        var framer = new ReceiveFramer(new("auto"), [known]);
        Assert.Empty(framer.Feed(known.AsSpan(0, 3)));
        Assert.Equal(known, Assert.Single(framer.Feed(known.AsSpan(3))));
        var lines = framer.Feed(Encoding.UTF8.GetBytes("你好\r\nOK\nrest"));
        Assert.Equal(2, lines.Count);
        Assert.Equal("你好\r\n", Encoding.UTF8.GetString(lines[0]));
        Assert.Equal("rest", Encoding.UTF8.GetString(framer.Flush()));
        var invalid = HexCodec.Parse("01 03 00 00 00 02 00 00");
        Assert.Empty(framer.Feed(invalid));
        Assert.Equal(invalid, framer.Flush());
    }

    [Theory]
    [InlineData("fixed")]
    [InlineData("delimiter")]
    public void FixedAndDelimiterExtractMultipleAndRetainPartial(string mode)
    {
        var framer = new ReceiveFramer(new(mode, Length: 3, Delimiter: "03"));
        var frames = framer.Feed([1, 2, 3, 1, 2, 3, 9]);
        Assert.Equal(2, frames.Count);
        Assert.All(frames, frame => Assert.Equal(new byte[] { 1, 2, 3 }, frame));
        Assert.Equal(new byte[] { 9 }, framer.Flush());
        Assert.Empty(framer.Flush());
    }

    [Fact]
    public void IdleBufferIsBoundedAndRawPreservesReads()
    {
        var source = Enumerable.Repeat((byte)65, ReceiveFramer.MaxBufferedBytes + 2).ToArray();
        var idle = new ReceiveFramer(new("idle"));
        Assert.Equal(ReceiveFramer.MaxBufferedBytes, Assert.Single(idle.Feed(source)).Length);
        Assert.Equal(2, idle.BufferedBytes);
        Assert.Equal(new byte[] { 65, 65 }, idle.Flush());
        Assert.Equal(source, Assert.Single(new ReceiveFramer(new()).Feed(source)));
    }

    [Theory]
    [InlineData(TextEncodingKind.Ascii, "你好")]
    [InlineData(TextEncodingKind.Gbk, "😀")]
    public void LossySendEncodingIsRejected(TextEncodingKind kind, string text)
    {
        var error = Assert.Throws<PayloadEncodeException>(() => TextCodec.Encode(text, kind));
        Assert.Equal("TX-INVALID-ENCODING", error.ErrorCode);
        Assert.Contains("第 1 个字符", error.Message);
        Assert.Contains("UTF-8", error.Message);
    }

    [Fact]
    public void InvalidSurrogateIsRejectedAndReceiveStillUsesReplacement()
    {
        Assert.Contains("非法 Unicode 代理项", Assert.Throws<PayloadEncodeException>(() => TextCodec.Encode("\ud800", TextEncodingKind.Utf8)).Message);
        Assert.Equal("�", TextCodec.Decode([255], TextEncodingKind.Utf8));
        Assert.Equal("串口", TextCodec.Decode(TextCodec.Encode("串口", TextEncodingKind.Gbk), TextEncodingKind.Gbk));
    }
}
