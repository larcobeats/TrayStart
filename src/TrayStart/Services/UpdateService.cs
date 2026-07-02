using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace TrayStart.Services;

/// <summary>
/// Update checks against GitHub Releases via Velopack. Checks run only on launch or when
/// the user clicks "Check for updates" — there is no background updater or timer.
/// </summary>
public class UpdateService
{
    public const string RepoUrl = "https://github.com/larcobeats/TrayStart";

    private readonly UpdateManager _manager = new(new GithubSource(RepoUrl, null, prerelease: false));

    /// <summary>False when running unpackaged (dev build / loose exe) — updates need the installed app.</summary>
    public bool IsSupported => _manager.IsInstalled;

    public string CurrentVersion
    {
        get
        {
            if (_manager.IsInstalled && _manager.CurrentVersion != null)
            {
                return _manager.CurrentVersion.ToString();
            }
            var info = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            int plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
    }

    public Task<UpdateInfo?> CheckForUpdatesAsync() => _manager.CheckForUpdatesAsync();

    public async Task DownloadAndApplyAsync(UpdateInfo update)
    {
        await _manager.DownloadUpdatesAsync(update);
        _manager.ApplyUpdatesAndRestart(update);
    }
}
