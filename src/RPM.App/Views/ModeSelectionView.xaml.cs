using System.Windows;
using RPM.Core.Models;

namespace RPM.App.Views;

public partial class ModeSelectionView : UserControl
{
    public event EventHandler<AppRole>? OnRoleSelected;

    public ModeSelectionView()
    {
        InitializeComponent();
    }

    private void BtnSelectServer_Click(object sender, RoutedEventArgs e)
    {
        OnRoleSelected?.Invoke(this, AppRole.Server);
    }

    private void BtnSelectClient_Click(object sender, RoutedEventArgs e)
    {
        OnRoleSelected?.Invoke(this, AppRole.Client);
    }
}
