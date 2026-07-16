using System.IO;

namespace TrayStart.Services;

/// <summary>
/// Minimal diagnostic log (%APPDATA%\TrayStart\traystart.log). Records every hide,
/// re-show, re-hide, and release decision with its reason, so boot-time behavior can
/// be diagnosed from evidence instead of reproduction. Self-truncates at 512 KB.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TrayStart", "traystart.log");

    private const long MaxBytes = 512 * 1024;

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.WriteAllText(LogPath, string.Empty);
                }
                File.AppendAllText(LogPath,
                    $"{DateTime.Now:MM-dd HH:mm:ss.fff} [up {Environment.TickCount64 / 1000}s] {message}\r\n");
            }
        }
        catch
        {
            // Logging must never break the app.
        }
    }
}
