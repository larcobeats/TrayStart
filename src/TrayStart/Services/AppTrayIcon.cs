using System.Drawing;
using System.Windows.Forms;

namespace TrayStart.Services;

/// <summary>TrayStart's own tray icon: double-click opens settings, right-click for the menu.</summary>
public sealed class AppTrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public AppTrayIcon(App app)
    {
        _icon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "TrayStart",
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => app.ShowSettings();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Settings", null, (_, _) => app.ShowSettings());
        menu.Items.Add("Restore All Windows", null, (_, _) => app.Minimizer.RestoreAll());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Check for Updates", null, async (_, _) => await app.CheckForUpdatesFromTrayAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => app.ExitApp());
        _icon.ContextMenuStrip = menu;
    }

    public void ShowBalloon(string title, string text) =>
        _icon.ShowBalloonTip(3000, title, text, ToolTipIcon.Info);

    private static Icon LoadAppIcon()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe != null)
            {
                var icon = Icon.ExtractAssociatedIcon(exe);
                if (icon != null) return icon;
            }
        }
        catch
        {
        }
        return (Icon)SystemIcons.Application.Clone();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
