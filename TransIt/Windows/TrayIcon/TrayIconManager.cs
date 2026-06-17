using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;

namespace TransIt.Windows.TrayIcon;

public class TrayIconManager : IDisposable
{
    private TaskbarIcon? _trayIcon;

    public event EventHandler? RegionRequested;
    public event EventHandler? SettingsRequested;

    public void Initialize()
    {
        _trayIcon = new TaskbarIcon
        {
            IconSource = new System.Windows.Media.Imaging.BitmapImage(
                new Uri("pack://application:,,,/Resources/tray_icon.ico")),
            ToolTipText = "TransIt — Screen Translator"
        };

        var menu = new ContextMenu();
        menu.Items.Add(MakeItem("✂ Region (Ctrl+2)",     (_, _) => RegionRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("⚙ Settings",            (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("✖ Exit",                (_, _) => Application.Current.Shutdown()));

        _trayIcon.ContextMenu = menu;
        _trayIcon.TrayMouseDoubleClick += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    public void ShowBalloon(string title, string message, BalloonIcon icon = BalloonIcon.Info) =>
        _trayIcon?.ShowBalloonTip(title, message, icon);

    private static MenuItem MakeItem(string header, RoutedEventHandler handler)
    {
        var item = new MenuItem { Header = header };
        item.Click += handler;
        return item;
    }

    public void Dispose() => _trayIcon?.Dispose();
}
