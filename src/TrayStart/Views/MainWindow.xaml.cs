using System.ComponentModel;
using System.Windows;
using TrayStart.ViewModels;

namespace TrayStart.Views;

public partial class MainWindow : Window
{
    private readonly App _app;

    public MainViewModel ViewModel { get; }

    public MainWindow(App app)
    {
        _app = app;
        ViewModel = new MainViewModel(app);
        DataContext = ViewModel;
        InitializeComponent();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing the settings window hides it; TrayStart keeps running in the tray.
        if (!_app.IsExiting)
        {
            e.Cancel = true;
            Hide();

            if (!_app.SettingsService.Settings.ShownCloseToTrayTip)
            {
                _app.SettingsService.Settings.ShownCloseToTrayTip = true;
                _app.SettingsService.Save();
                _app.TrayIcon?.ShowBalloon("TrayStart is still running",
                    "It's watching from the tray. Right-click the tray icon to exit.");
            }
        }
        base.OnClosing(e);
    }
}
