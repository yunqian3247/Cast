using serial.Core;
using serial.Desktop;
using Xunit;

namespace serial.Desktop.Tests;

public sealed class PresetImportTests
{
    [Fact]
    public void ImportsPresetArrayAndPreservesPayloadAndDescriptions()
    {
        var presets = CommandPreset.ParseImport("""
            [{"id":"sample","name":"查询状态","content":"01 03 00 00 00 02 C4 0B","format":"hex","desc":"读取保持寄存器","rxMatch":"01 03 04","rxDesc":"寄存器应答"}]
            """);
        var preset = Assert.Single(presets);
        Assert.Equal("sample", preset.Id);
        Assert.Equal("查询状态", preset.Name);
        Assert.Equal("读取保持寄存器", preset.Desc);
        Assert.Equal("01 03 04", preset.RxMatch);
        Assert.Equal("寄存器应答", preset.RxDesc);
        Assert.Equal(new byte[] { 1, 3, 0, 0, 0, 2, 0xC4, 0x0B }, HexCodec.Parse(preset.Content));
    }

    [Theory]
    [InlineData("{\"frame\":{\"check\":null},\"commands\":[{\"data1\":\"60\"}]}")]
    [InlineData("null")]
    [InlineData("\"presets\"")]
    public void RejectsWrongRootShapeWithActionableMessage(string json)
    {
        var error = Assert.Throws<FormatException>(() => CommandPreset.ParseImport(json));
        Assert.Contains("最外层须为数组", error.Message);
        Assert.DoesNotContain("System.Collections", error.Message);
    }

    [Theory]
    [InlineData("[")]
    [InlineData("[{\"name\":42,\"content\":\"01\",\"format\":\"hex\"}]")]
    public void ReportsInvalidJsonOrFieldTypesInChinese(string json)
    {
        var error = Assert.Throws<FormatException>(() => CommandPreset.ParseImport(json));
        Assert.Equal("预设 JSON 格式或字段类型错误，请检查文件内容。", error.Message);
    }

    [Fact]
    public void RejectsPlaceholderChecksumAndDuplicateIds()
    {
        Assert.Throws<HexParseException>(() => CommandPreset.ParseImport("""
            [{"id":"p","name":"待补齐","content":"A5 FA 81 00 60 xx FB","format":"hex"}]
            """));
        Assert.Throws<FormatException>(() => CommandPreset.ParseImport("""
            [{"id":"p","name":"A","content":"01","format":"hex"},{"id":"p","name":"B","content":"02","format":"hex"}]
            """));
    }

    [Theory]
    [InlineData("main-controller-voice-presets.json")]
    [InlineData("source.json")]
    public void ImportsAllVoicePresetsWithCompleteChecksums(string fileName)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../samples/presets/main-controller-voice", fileName));
        var presets = CommandPreset.ParseImport(File.ReadAllText(path));
        Assert.Equal(60, presets.Count);
        Assert.Equal(60, presets.Select(p => p.Id).Distinct().Count());
        Assert.Equal("欢迎使用晾霸智能晾衣机，我叫小E", presets[0].Desc);
        Assert.All(presets, preset =>
        {
            Assert.Matches(@"^A5 FA 81 00 [0-9A-F]{2} [0-9A-F]{2} FB$", preset.Content);
            var bytes = HexCodec.Parse(preset.Content);
            Assert.Equal(7, bytes.Length);
            Assert.Equal((byte)(bytes.Take(5).Sum(value => value) & 0xFF), bytes[5]);
        });
        Assert.Equal("A5 FA 81 00 05 25 FB", presets.Single(p => p.Id == "main-voice-81-00-05").Content);
        Assert.Equal("A5 FA 81 00 60 80 FB", presets[0].Content);
    }

    [Theory]
    [InlineData("A5 ??60 FB")]
    [InlineData("A5 6?? FB")]
    [InlineData("A5 ???? FB")]
    [InlineData("A5 GG ?? FB")]
    public void PresetsRequireCompleteHexBytes(string content)
    {
        Assert.Throws<HexParseException>(() => new CommandPreset { Name = "Draft", Content = content, Format = "hex" }.Validate());
    }
}
