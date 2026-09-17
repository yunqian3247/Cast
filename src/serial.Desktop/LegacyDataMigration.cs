using serial.Core;

namespace serial.Desktop;

public static class LegacyDataMigration
{
    // These names identify the previous on-disk format for one-time migration.
    public static string SettingsPath(string localAppData) =>
        Path.Combine(localAppData, "SerialDebugTool", "Pebrel", "workbench.json");

    public static async Task MigrateAsync(string legacyPath, string targetPath)
    {
        if (File.Exists(targetPath) || !File.Exists(legacyPath)) return;
        var store = new JsonFileStore<AppDocument>(AppService.Json);
        var document = await store.LoadAsync(legacyPath);
        if (document is null) return;
        document.Validate();
        await store.SaveAsync(targetPath, document);
    }
}
