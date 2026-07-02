using Velopack;

namespace TrayStart;

public static class Program
{
    private static Mutex? _singleInstanceMutex;

    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack hooks: must run first, handles install/update/uninstall events
        // and exits early when invoked by the updater.
        VelopackApp.Build().Run();

        _singleInstanceMutex = new Mutex(true, @"Local\TrayStart_SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            return;
        }

        var app = new App();
        app.InitializeComponent();
        app.Run();

        GC.KeepAlive(_singleInstanceMutex);
    }
}
