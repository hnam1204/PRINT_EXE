using System.Collections.ObjectModel;
using System.Windows;
using RPM.Core.Diagnostics;
using RPM.Core.Models;

namespace RPM.App.Views;

public partial class PrintQueueDialog : Window
{
    private readonly ObservableCollection<PrintJob> _jobs;
    private readonly Action? _onClearCompleted;

    public PrintQueueDialog(ObservableCollection<PrintJob> jobs, Action? onClearCompleted = null)
    {
        _jobs = jobs;
        _onClearCompleted = onClearCompleted;
        InitializeComponent();

        DgQueue.ItemsSource = _jobs;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int total = _jobs.Count;
        int completed = _jobs.Count(j => j.Status == PrintJobStatus.Completed);
        int pending = _jobs.Count(j => j.Status == PrintJobStatus.Pending || j.Status == PrintJobStatus.Printing);
        int failed = _jobs.Count(j => j.Status == PrintJobStatus.Failed);

        TxtQueueSummary.Text = $"Tổng số: {total} lệnh in • Đang chờ/xử lý: {pending} • Hoàn tất: {completed} • Lỗi: {failed}";
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        DgQueue.Items.Refresh();
        UpdateSummary();
    }

    private void BtnRestartSpooler_Click(object sender, RoutedEventArgs e)
    {
        bool restarted = CompatibilityChecker.RestartSpoolerService(out string err);
        if (restarted)
        {
            MessageBox.Show("Đã khởi động lại dịch vụ Windows Print Spooler thành công! Hàng đợi in ấn đã được giải phóng.", "Thành công", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show($"Không thể tự động khởi động dịch vụ Spooler: {err}\nVui lòng chạy ứng dụng với quyền 'Run as administrator'.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnClearCompleted_Click(object sender, RoutedEventArgs e)
    {
        if (_onClearCompleted != null)
        {
            _onClearCompleted.Invoke();
        }
        else
        {
            var completed = _jobs.Where(j => j.Status == PrintJobStatus.Completed || j.Status == PrintJobStatus.Failed).ToList();
            foreach (var j in completed) _jobs.Remove(j);
        }
        DgQueue.Items.Refresh();
        UpdateSummary();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
