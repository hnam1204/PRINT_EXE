using RPM.Core.Models;

namespace RPM.Core.Printing;

public static class DirectPrintAuthorization
{
    public static bool TryResolveTargetPrinter(
        DeviceInfo sender,
        IEnumerable<SharedPrinterInfo> sharedPrinters,
        string? requestedPrinter,
        out string targetPrinter,
        out string error)
    {
        targetPrinter = string.Empty;
        error = string.Empty;

        var shared = sharedPrinters
            .Where(printer => printer.IsShared && !string.IsNullOrWhiteSpace(printer.Name))
            .ToList();
        if (shared.Count == 0)
        {
            error = "Máy chủ chưa chia sẻ máy in nào.";
            return false;
        }

        var selected = string.IsNullOrWhiteSpace(requestedPrinter)
            ? shared.FirstOrDefault()
            : shared.FirstOrDefault(printer => string.Equals(printer.Name, requestedPrinter, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            error = "Máy in yêu cầu không khả dụng trên máy chủ.";
            return false;
        }

        if (sender.AllowedPrinters is { Count: > 0 } &&
            !sender.AllowedPrinters.Contains(selected.Name, StringComparer.OrdinalIgnoreCase))
        {
            error = $"Thiết bị không có quyền in trên máy in '{selected.Name}'.";
            return false;
        }

        targetPrinter = selected.Name;
        return true;
    }
}
