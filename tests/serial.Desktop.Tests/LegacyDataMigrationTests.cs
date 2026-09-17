using System.Text.Json;
using serial.Core;
using serial.Desktop;
using Xunit;

namespace serial.Desktop.Tests;

public sealed class LegacyDataMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "serial-migration-" + Guid.NewGuid());
    private string Legacy => LegacyDataMigration.SettingsPath(_root);
    private string Target => Path.Combine(_root, "serial", "Data", "serial.json");

    public LegacyDataMigrationTests() => Directory.CreateDirectory(Path.GetDirectoryName(Legacy)!);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task ImportsExistingPreferencesPresetsAndHistoryWithoutChangingOriginal()
    {
        var document = new AppDocument { Presets = [new() { Id = "p", Name = "saved", Content = "AT" }], History = ["AT+PING"] };
        var original = JsonSerializer.Serialize(document, AppService.Json);
        await File.WriteAllTextAsync(Legacy, original);
        await using var service = new AppService(Path.GetDirectoryName(Target)!, new FakeConnection(), new FakeCatalog(), Legacy);
        await service.InitializeAsync();
        Assert.Equal("saved", service.Document.Presets.Single().Name);
        Assert.Equal("AT+PING", service.Document.History.Single());
        Assert.Equal(original, await File.ReadAllTextAsync(Legacy));
        Assert.True(File.Exists(Target));
    }

    [Fact]
    public async Task ExistingNewConfigurationWinsOverLegacyData()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
        await File.WriteAllTextAsync(Target, "existing config");
        await File.WriteAllTextAsync(Legacy, "legacy config");
        await LegacyDataMigration.MigrateAsync(Legacy, Target);
        Assert.Equal("existing config", await File.ReadAllTextAsync(Target));
    }

    [Fact]
    public async Task RecoversLegacyBackupAndDoesNotPublishCorruptData()
    {
        await File.WriteAllTextAsync(Legacy, "{");
        await Assert.ThrowsAsync<JsonFileStoreException>(() => LegacyDataMigration.MigrateAsync(Legacy, Target));
        Assert.False(File.Exists(Target));
        await File.WriteAllTextAsync(Legacy + ".bak", JsonSerializer.Serialize(new AppDocument { History = ["backup"] }, AppService.Json));
        await LegacyDataMigration.MigrateAsync(Legacy, Target);
        var saved = await new JsonFileStore<AppDocument>(AppService.Json).LoadAsync(Target);
        Assert.Equal("backup", saved!.History.Single());
    }
}
