using System.Windows;

namespace RPM.App.Views;

public enum ExitAction
{
    Cancel,
    MinimizeToTray,
    Exit
}

public partial class ExitConfirmationDialog : Window
{
    public ExitAction UserAction { get; private set; } = ExitAction.Cancel;

    public ExitConfirmationDialog()
    {
        InitializeComponent();
    }

    private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        UserAction = ExitAction.Cancel;
        DialogResult = false;
        Close();
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
    {
        UserAction = ExitAction.MinimizeToTray;
        DialogResult = false;
        Close();
    }

    private void BtnExit_Click(object sender, RoutedEventArgs e)
    {
        UserAction = ExitAction.Exit;
        DialogResult = true;
        Close();
    }
}
