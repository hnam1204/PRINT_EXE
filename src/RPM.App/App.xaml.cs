using System.Windows;

namespace RPM.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    public App()
    {
        DispatcherUnhandledException += (s, e) =>
        {
            MessageBox.Show(
                $"Phát hiện ngoại lệ ứng dụng:\n{e.Exception.Message}\n\nChi tiết:\n{e.Exception.StackTrace}",
                "Lỗi hệ thống",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            e.Handled = true;
        };
    }
}

