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
        Icon? appIcon = null;
        try
        {
            var iconUri = new Uri("pack://application:,,,/RPM.App;component/Assets/app.ico");
            var sri = System.Windows.Application.GetResourceStream(iconUri);
            if (sri?.Stream != null)
            {
                using var ms = new MemoryStream();
                sri.Stream.CopyTo(ms);
                ms.Position = 0;

                // Load native small icon resolution matching Windows system metrics
                int smallW = SystemInformation.SmallIconSize.Width;
                int smallH = SystemInformation.SmallIconSize.Height;
                try
                {
                    appIcon = new Icon(ms, smallW, smallH);
                }
                catch
                {
                    ms.Position = 0;
                    appIcon = new Icon(ms);
                }
            }
        }
        catch { }

        _notifyIcon = new NotifyIcon
        {
            Icon = appIcon ?? SystemIcons.Application,
            Text = "Remote Print Manager",
            Visible = true
        };

        var contextMenu = new ContextMenuStrip();
        var openItem = new ToolStripMenuItem("Mở ứng dụng", null, (s, e) => ShowMainWindow());
        var hideItem = new ToolStripMenuItem("Ẩn cửa sổ", null, (s, e) => HideMainWindow());
        var exitItem = new ToolStripMenuItem("Thoát", null, (s, e) => ExitApp());

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
        if (_mainWindow is MainWindow mw)
        {
            mw.ExitApplication();
        }
        else
        {
            Dispose();
            System.Windows.Application.Current.Shutdown();
        }
    }

    public void Dispose()
    {
        try
        {
            if (_notifyIcon != null)
            {
                var icon = _notifyIcon;
                _notifyIcon = null;
                try { icon.Visible = false; } catch { }
                try { icon.ContextMenuStrip?.Dispose(); } catch { }
                try { icon.Dispose(); } catch { }
            }
        }
        catch
        {
            // Ignore native WinForms tray icon cleanup exceptions
        }
        finally
        {
            _notifyIcon = null;
        }
    }
}
