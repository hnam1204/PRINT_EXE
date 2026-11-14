using System.Windows;
using RPM.Core.Config;

namespace RPM.App.Views;

public partial class UpdateDetailsDialog : Window
{
    private readonly UpdateControl _control;

    public UpdateDetailsDialog(AppSettings settings, Func<bool>? hasActivePrintJobsCheck)
    {
        InitializeComponent();
        _control = UpdateDetailsControl;
        _control.Initialize(settings);
        _control.HasActivePrintJobsCheck = hasActivePrintJobsCheck;
    }

    public Task CheckNowAsync() => _control.CheckNowAsync();

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
