using System.Collections.ObjectModel;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Media;
using RPM.App.Services;
using RPM.Core.Config;
using RPM.Core.Diagnostics;
using RPM.Core.Models;
using RPM.Printing;

namespace RPM.App.Views;

public partial class MachineDiagnosticsDialog : Window
{
    private readonly AppSettings _settings;
    private readonly ServerService? _serverService;
    private readonly ClientService? _clientService;

    public ObservableCollection<DiagnosticRow> Rows { get; } = new();

    public MachineDiagnosticsDialog(AppSettings settings, ServerService? serverService, ClientService? clientService)
    {
        _settings = settings;
        _serverService = serverService;
        _clientService = clientService;
        DataContext = this;
        InitializeComponent();
        Loaded += async (_, _) => await RunDiagnosticsAsync();
    }

    private async void BtnRunDiagnostics_Click(object sender, RoutedEventArgs e) => await RunDiagnosticsAsync();

    private async Task RunDiagnosticsAsync()
    {
        BtnRunDiagnostics.IsEnabled = false;
        TxtSummary.Text = "Đang kiểm tra dữ liệu thực tế...";
        Rows.Clear();

        var result = await Task.Run(BuildReport);
        foreach (var row in result.Rows) Rows.Add(row);
        TxtSummary.Text = result.Summary;
        BtnRunDiagnostics.IsEnabled = true;
    }

    private DiagnosticsResult BuildReport()
    {
        var printerService = new PrinterService();
        var printers = printerService.GetInstalledPrinters();
        var report = CompatibilityChecker.RunDiagnostics(_settings, printers);
        var rows = new List<DiagnosticRow>();

        foreach (var item in report.Items)
        {
            rows.Add(new DiagnosticRow(item.Name, item.Detail, ToStatus(item.Level)));
        }

        bool internet = NetworkInterface.GetIsNetworkAvailable();
        rows.Add(new DiagnosticRow("Internet", internet ? "Windows báo có kết nối mạng." : "Không phát hiện kết nối mạng.", internet ? DiagnosticState.Success : DiagnosticState.Warning));

        bool relayConnected = _serverService?.Relay.IsConnected ?? _clientService?.Relay.IsConnected ?? false;
        rows.Add(new DiagnosticRow("Kênh kết nối ứng dụng", relayConnected ? "Relay đang kết nối thực tế." : "Relay chưa kết nối; chưa xác nhận được máy ở xa.", relayConnected ? DiagnosticState.Success : DiagnosticState.Warning));

        bool endpointValid = RPM.Core.Networking.RelayEndpointPolicy.TryValidateForRole(_settings.Role, _settings.RelayServerUrl, out var endpointReason);
        string endpointType = _settings.Role == AppRole.Server ? "Relay nội bộ của Host" : "Relay từ xa của Client";
        rows.Add(new DiagnosticRow(endpointType, endpointValid ? _settings.RelayServerUrl : $"{_settings.RelayServerUrl} • {endpointReason}", endpointValid ? DiagnosticState.Success : DiagnosticState.Warning));
        rows.Add(new DiagnosticRow("Cổng nhận lệnh in cục bộ", $"127.0.0.1:{_settings.LocalIppPort} (chỉ dùng trên máy này)", DiagnosticState.Success));

        bool paired = _serverService != null || (_clientService?.IsApproved == true);
        rows.Add(new DiagnosticRow("Quyền ghép nối", paired ? "Vai trò hoặc quyền ghép nối hiện có." : "Chưa được máy chủ cấp quyền.", paired ? DiagnosticState.Success : DiagnosticState.Warning));
        rows.Add(new DiagnosticRow("Phiên bản phần mềm", $"V{_settings.AppVersion}", DiagnosticState.Success));

        foreach (var printer in printers)
        {
            string type = GetPrinterType(printer);
            string detail = $"{type} • Cổng: {printer.PortName ?? "chưa rõ"} • Trạng thái: {printer.Status}";
            var state = string.Equals(printer.Status, "Ready", StringComparison.OrdinalIgnoreCase)
                ? DiagnosticState.Success
                : DiagnosticState.Warning;
            rows.Add(new DiagnosticRow($"Máy in: {printer.Name}", detail, state));
        }

        if (printers.Count == 0)
        {
            rows.Add(new DiagnosticRow("Máy in thực tế", "Chưa phát hiện máy in cài trên Windows.", DiagnosticState.Warning));
        }

        string summary = report.OverallLevel switch
        {
            CompatibilityLevel.Compatible => $"Đã kiểm tra {rows.Count} mục. Không phát hiện lỗi hệ thống chính.",
            CompatibilityLevel.NeedsSetup => $"Đã kiểm tra {rows.Count} mục. Có mục cần kiểm tra hoặc thiết lập thêm.",
            _ => $"Đã kiểm tra {rows.Count} mục. Có lỗi cần xử lý trước khi vận hành."
        };

        return new DiagnosticsResult(summary, rows);
    }

    private static DiagnosticState ToStatus(CompatibilityLevel level) => level switch
    {
        CompatibilityLevel.Compatible => DiagnosticState.Success,
        CompatibilityLevel.NeedsSetup => DiagnosticState.Warning,
        _ => DiagnosticState.Error
    };

    private static string GetPrinterType(SharedPrinterInfo printer)
    {
        string port = printer.PortName ?? string.Empty;
        string name = printer.Name ?? string.Empty;
        if (port.StartsWith("USB", StringComparison.OrdinalIgnoreCase)) return "USB vật lý";
        if (port.StartsWith("IP_", StringComparison.OrdinalIgnoreCase) || port.Contains("WSD", StringComparison.OrdinalIgnoreCase) || port.Contains("TCP", StringComparison.OrdinalIgnoreCase)) return "Máy in mạng";
        if (port.Equals("nul:", StringComparison.OrdinalIgnoreCase) || name.Contains("OneNote", StringComparison.OrdinalIgnoreCase) || name.Contains("PDF", StringComparison.OrdinalIgnoreCase) || name.Contains("XPS", StringComparison.OrdinalIgnoreCase)) return "Máy in ảo";
        return "Chưa xác định loại kết nối";
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private sealed record DiagnosticsResult(string Summary, List<DiagnosticRow> Rows);
}

public enum DiagnosticState
{
    Success,
    Warning,
    Error,
    NotTested
}

public sealed class DiagnosticRow
{
    public string Name { get; }
    public string Detail { get; }
    public string StatusText { get; }
    public SolidColorBrush StatusBrush { get; }

    public DiagnosticRow(string name, string detail, DiagnosticState state)
    {
        Name = name;
        Detail = detail;
        (StatusText, StatusBrush) = state switch
        {
            DiagnosticState.Success => ("Thành công", new SolidColorBrush(Color.FromRgb(21, 184, 74))),
            DiagnosticState.Warning => ("Cảnh báo", new SolidColorBrush(Color.FromRgb(242, 169, 0))),
            DiagnosticState.Error => ("Lỗi", new SolidColorBrush(Color.FromRgb(197, 61, 50))),
            _ => ("Chưa kiểm tra", new SolidColorBrush(Color.FromRgb(100, 116, 139)))
        };
    }
}
