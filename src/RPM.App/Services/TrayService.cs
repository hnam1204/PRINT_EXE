using System.Drawing;
using System.Windows;
using System.Windows.Forms;

namespace RPM.App.Services;

public class TrayService : IDisposable
{
    private NotifyIcon? _notifyIcon;
    private readonly Window _mainWindow;

    public TrayService(Window mainWindow)
    {
        _mainWindow = mainWindow;
        InitializeTray();
    }

    private void InitializeTray()
    {
        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Remote Print Manager",
            Visible = true
        };

        var contextMenu = new ContextMenuStrip();
        var openItem = new ToolStripMenuItem("Mở Remote Print Manager", null, (s, e) => ShowMainWindow());
        var hideItem = new ToolStripMenuItem("Ẩn cửa sổ", null, (s, e) => HideMainWindow());
        var exitItem = new ToolStripMenuItem("Thoát hoàn toàn", null, (s, e) => ExitApp());

        contextMenu.Items.Add(openItem);
        contextMenu.Items.Add(hideItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(exitItem);

        _notifyIcon.ContextMenuStrip = contextMenu;
        _notifyIcon.DoubleClick += (s, e) => ShowMainWindow();
    }

    public void ShowNotification(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        _notifyIcon?.ShowBalloonTip(3000, title, message, icon);
    }

    public void ShowMainWindow()
    {
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    public void HideMainWindow()
    {
        _mainWindow.Hide();
    }

    private void ExitApp()
    {
        _notifyIcon?.Dispose();
        if (_mainWindow is MainWindow mw)
        {
            mw.ExitApplication();
        }
        else
        {
            Application.Current.Shutdown();
        }
    }

    public void Dispose()
    {
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
    }
}
