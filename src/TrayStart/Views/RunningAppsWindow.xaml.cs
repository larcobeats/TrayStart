using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace TrayStart.Views;

public partial class RunningAppsWindow : Window
{
    public sealed class RunningAppEntry
    {
        public required string Title { get; init; }
        public required string ExeName { get; init; }
        public required string DisplayName { get; init; }
    }

    public RunningAppEntry? SelectedEntry { get; private set; }

    public RunningAppsWindow()
    {
        InitializeComponent();
        Populate();
    }

    private void Populate()
    {
        int ownPid = Environment.ProcessId;
        var entries = new List<RunningAppEntry>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == ownPid) continue;
                if (process.MainWindowHandle == IntPtr.Zero) continue;
                if (string.IsNullOrWhiteSpace(process.MainWindowTitle)) continue;

                entries.Add(new RunningAppEntry
                {
                    Title = process.MainWindowTitle,
                    ExeName = process.ProcessName + ".exe",
                    DisplayName = GetDisplayName(process) ?? process.ProcessName,
                });
            }
            catch
            {
                // Inaccessible process — skip.
            }
            finally
            {
                process.Dispose();
            }
        }
        WindowList.ItemsSource = entries.OrderBy(e => e.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? GetDisplayName(Process process)
    {
        try
        {
            var info = process.MainModule?.FileVersionInfo;
            if (info == null) return null;
            return string.IsNullOrWhiteSpace(info.ProductName) ? info.FileDescription : info.ProductName;
        }
        catch
        {
            return null; // Elevated processes deny MainModule access.
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Populate();

    private void Add_Click(object sender, RoutedEventArgs e) => Confirm();

    private void WindowList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Confirm();

    private void Confirm()
    {
        if (WindowList.SelectedItem is RunningAppEntry entry)
        {
            SelectedEntry = entry;
            DialogResult = true;
        }
    }
}
