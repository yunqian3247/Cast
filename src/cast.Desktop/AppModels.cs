using System.Text.Json;
using System.Text.Json.Nodes;
using cast.Core;

namespace cast.Desktop;

public sealed class AppDocument
{
    public const int MaxSerializedChars = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions CapacityJson = new(AppService.Json) { WriteIndented = false };
    public int Version { get; set; } = 1;
    public SerialProfile Profile { get; set; } = new() { LineEnding = LineEndingMode.CrLf };
    public List<CommandPreset> Presets { get; set; } = [];
    public Dictionary<string, CommandWorkflow> Workflows { get; set; } = [];
    public JsonObject Ui { get; set; } = new();
    public List<string> History { get; set; } = [];

    public void Validate()
    {
        if (Version != 1 || Profile is null || Presets is null || Workflows is null || Ui is null || History is null)
            throw new FormatException("应用数据格式无效");
        if (Presets.Count > 1000 || Workflows.Count > 100 || History.Count > 50) throw new FormatException("数据数量超出限制");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var preset in Presets)
        {
            if (preset is null) throw new FormatException("预设格式无效");
            preset.Validate();
            if (!ids.Add(preset.Id)) throw new FormatException("预设 ID 重复");
        }
        foreach (var (id, workflow) in Workflows)
        {
            if (string.IsNullOrWhiteSpace(id) || workflow is null || string.IsNullOrWhiteSpace(workflow.Name)
                || workflow.Name.Length > 120 || workflow.Steps is null || workflow.Steps.Count > 1000)
                throw new FormatException("工作流格式无效");
            foreach (var step in workflow.Steps)
                if (step is null || string.IsNullOrWhiteSpace(step.PresetId) || step.Wait is < 0 or > 86_400_000)
                    throw new FormatException("工作流步骤无效");
        }
        if (History.Any(value => value is null || value.Length > 1_048_576)) throw new FormatException("发送历史无效");
        _ = MonitorSettings.FromUi(Ui);
        AppService.ValidateProfile(Profile, requirePort: false);
        if (JsonSerializer.Serialize(this, CapacityJson).Length > MaxSerializedChars)
            throw new FormatException("应用配置超过 64 Mi 字符，请减少预设内容或发送历史");
    }
}

public sealed record MonitorSettings(int MaxLogCount = 10000, int RefreshIntervalMs = 50)
{
    public static MonitorSettings FromUi(JsonObject ui) => new(
        ReadInteger(ui, "maxLogCount", 10000, 100, 100000, "最大保留记录数"),
        ReadInteger(ui, "refreshIntervalMs", 50, 20, 1000, "界面刷新间隔"));

    private static int ReadInteger(JsonObject ui, string key, int fallback, int min, int max, string label)
    {
        if (!ui.TryGetPropertyValue(key, out var node)) return fallback;
        if (node is JsonValue value && value.TryGetValue<int>(out var number) && number >= min && number <= max)
            return number;
        throw new FormatException($"{label}须为 {min}～{max} 的整数");
    }
}

public sealed class CommandPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Content { get; set; } = "";
    public string Format { get; set; } = "text";
    public string Desc { get; set; } = "";
    public string RxMatch { get; set; } = "";
    public string RxDesc { get; set; } = "";

    public static List<CommandPreset> ParseImport(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new FormatException("预设文件最外层须为数组 [...]。请导入包含 id、name、content、format 字段的预设文件。");
            var presets = document.RootElement.Deserialize<List<CommandPreset>>(AppService.Json)!;
            new AppDocument { Presets = presets }.Validate();
            return presets;
        }
        catch (JsonException ex)
        {
            throw new FormatException("预设 JSON 格式或字段类型错误，请检查文件内容。", ex);
        }
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 128 || string.IsNullOrWhiteSpace(Name) || Name.Length > 120
            || string.IsNullOrEmpty(Content) || Content.Length > 1_048_576 || Format is not ("text" or "hex")
            || Desc is null || RxMatch is null || RxDesc is null || Desc.Length > 4096 || RxMatch.Length > 4096 || RxDesc.Length > 4096)
            throw new FormatException("预设字段无效");
        if (Format == "hex") _ = HexCodec.Parse(Content);
    }
}

public sealed class CommandWorkflow
{
    public string Name { get; set; } = "";
    public List<CommandStep> Steps { get; set; } = [];
}

public sealed class CommandStep
{
    public string PresetId { get; set; } = "";
    public int Wait { get; set; } = 500;
}

public sealed record SendRequest(string Text, bool Hex = false, string Ending = "crlf", string CustomEnding = "", bool Lines = false);
public sealed record SendPreview(int LineCount, long ByteCount);
public sealed record AppLog(long Id, DateTimeOffset Timestamp, string Dir, string Text, string Hex, int ByteCount, string Source);
public sealed record RunStatus(string Kind, bool Paused, int Step, int Round, string Name);
public sealed record AppStatus(bool Connected, string Port, long Tx, long Rx, RunStatus Run, Serial.SerialPinState? Pins, string? PinError);
