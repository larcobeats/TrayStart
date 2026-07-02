using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using Microsoft.Win32;

namespace TrayStart.Services;

/// <summary>
/// Manages "start at sign-in" via a per-user Scheduled Task launched with --tray
/// (so TrayStart starts hidden in the tray). A scheduled task is more robust than the
/// legacy HKCU\Run value: it isn't silently switched off by Task Manager's Startup tab,
/// and it can start the app early enough to catch other sign-in programs.
/// Uses the Task Scheduler COM API via late binding — no extra package needed.
/// </summary>
public static class StartupService
{
    private const string TaskName = "TrayStart";

    // Legacy autostart mechanism (pre-0.5) — removed whenever we touch the setting.
    private const string LegacyRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyRunValueName = "TrayStart";

    // Task Scheduler COM constants.
    private const int TASK_TRIGGER_LOGON = 9;
    private const int TASK_ACTION_EXEC = 0;
    private const int TASK_CREATE_OR_UPDATE = 6;
    private const int TASK_LOGON_INTERACTIVE_TOKEN = 3;
    private const int TASK_INSTANCES_IGNORE_NEW = 2;

    public static bool IsEnabled()
    {
        try
        {
            dynamic folder = GetRootFolder();
            folder.GetTask(TaskName); // throws if the task doesn't exist
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        // Always drop the legacy Run-key entry so the two mechanisms never double-launch.
        RemoveLegacyRunKey();

        if (enabled)
        {
            CreateTask();
        }
        else
        {
            DeleteTask();
        }
    }

    private static void CreateTask()
    {
        string exePath = GetLaunchTarget();

        dynamic service = GetService();
        dynamic folder = service.GetFolder("\\");
        dynamic task = service.NewTask(0);

        task.RegistrationInfo.Description = "Starts TrayStart minimized to the tray when you sign in.";
        task.RegistrationInfo.Author = "TrayStart";

        dynamic settings = task.Settings;
        settings.Enabled = true;
        settings.StartWhenAvailable = true;
        settings.DisallowStartIfOnBatteries = false;        // still start on laptops
        settings.StopIfGoingOnBatteries = false;
        settings.ExecutionTimeLimit = "PT0S";               // never auto-kill this long-running app
        settings.MultipleInstances = TASK_INSTANCES_IGNORE_NEW; // single-instance mutex handles dupes

        // Fire for the current user at sign-in. No start delay: TrayStart should be up
        // as early as possible so it can catch other programs that also launch at sign-in.
        dynamic trigger = task.Triggers.Create(TASK_TRIGGER_LOGON);
        trigger.UserId = WindowsIdentity.GetCurrent().Name;

        dynamic action = task.Actions.Create(TASK_ACTION_EXEC);
        action.Path = exePath;
        action.Arguments = "--tray";                        // start hidden in the tray
        action.WorkingDirectory = Path.GetDirectoryName(exePath);

        folder.RegisterTaskDefinition(
            TaskName, task, TASK_CREATE_OR_UPDATE, null, null, TASK_LOGON_INTERACTIVE_TOKEN, null);
    }

    private static void DeleteTask()
    {
        try
        {
            dynamic folder = GetRootFolder();
            folder.DeleteTask(TaskName, 0);
        }
        catch
        {
            // Task already absent — nothing to do.
        }
    }

    private static dynamic GetService()
    {
        Type? type = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("Task Scheduler is unavailable on this system.");
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }

    private static dynamic GetRootFolder() => GetService().GetFolder("\\");

    /// <summary>
    /// Prefer Velopack's stable root stub (…\TrayStart\TrayStart.exe) over the versioned
    /// …\current\TrayStart.exe so the task keeps working across updates. Falls back to the
    /// running executable (e.g. dev builds, where no stub exists).
    /// </summary>
    private static string GetLaunchTarget()
    {
        string current = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule!.FileName!;
        try
        {
            string? currentDir = Path.GetDirectoryName(current);   // …\current
            string? appRoot = Path.GetDirectoryName(currentDir);   // …\TrayStart
            if (appRoot != null)
            {
                string stub = Path.Combine(appRoot, "TrayStart.exe");
                if (File.Exists(stub) &&
                    !string.Equals(stub, current, StringComparison.OrdinalIgnoreCase))
                {
                    return stub;
                }
            }
        }
        catch
        {
            // Fall through to the running exe path.
        }
        return current;
    }

    private static void RemoveLegacyRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKeyPath, writable: true);
            if (key?.GetValue(LegacyRunValueName) != null)
            {
                key.DeleteValue(LegacyRunValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Non-fatal.
        }
    }
}
