using System.IO;
using System.Text.Json;
using TrayStart.Models;

namespace TrayStart.Services;

/// <summary>Loads and saves settings.json in %APPDATA%\TrayStart (survives app updates).</summary>
public class SettingsService
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrayStart");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AppSettings Settings { get; private set; } = new();

    private static string BackupPath => SettingsPath + ".bak";

    public void Load()
    {
        // Fall back to the last-known-good backup if the main file is missing or corrupt.
        Settings = TryLoad(SettingsPath) ?? TryLoad(BackupPath) ?? new AppSettings();
    }

    private static AppSettings? TryLoad(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
            }
        }
        catch
        {
            // Missing or corrupt — caller falls through to the next candidate.
        }
        return null;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            string json = JsonSerializer.Serialize(Settings, JsonOptions);

            // Atomic write: never leave a half-written settings.json, and keep the
            // previous version as .bak so a bad write can't lose the watch list.
            string tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(SettingsPath))
            {
                File.Replace(tmp, SettingsPath, BackupPath);
            }
            else
            {
                File.Move(tmp, SettingsPath);
            }
        }
        catch
        {
            // Non-fatal: the app keeps working with in-memory settings.
        }
    }
}
