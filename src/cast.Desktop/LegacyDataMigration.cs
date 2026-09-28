using cast.Core;

namespace cast.Desktop;

public static class LegacyDataMigration
{
    // Prefer the most recent product's data; older names are migration inputs only.
    public static string SettingsPath(string localAppData)
    {
        var serialPath = Path.Combine(localAppData, "serial", "Data", "serial.json");
        return File.Exists(serialPath) || File.Exists(serialPath + ".bak")
            ? serialPath
            : Path.Combine(localAppData, "SerialDebugTool", "Pebrel", "workbench.json");
    }

    public static async Task MigrateAsync(string legacyPath, string targetPath)
    {
        if (File.Exists(targetPath) || File.Exists(targetPath + ".bak")) return;
        var sourcePath = File.Exists(legacyPath) ? legacyPath : legacyPath + ".bak";
        if (!File.Exists(sourcePath)) return;
        var store = new JsonFileStore<AppDocument>(AppService.Json, static document => document.Validate());
        var document = await store.LoadAsync(sourcePath);
        if (document is null) return;
        document.Validate();
        await store.SaveAsync(targetPath, document);
    }
}
