using System;
using System.Collections.Generic;

namespace RPM.Core.Diagnostics;

public enum CompatibilityLevel
{
    Compatible = 0,    // Tương thích hoàn toàn (Màu xanh lá)
    NeedsSetup = 1,    // Cần thiết lập bổ sung (Màu vàng cam)
    Incompatible = 2   // Không tương thích (Màu đỏ)
}

public class DiagnosticItem
{
    public string Category { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public CompatibilityLevel Level { get; set; } = CompatibilityLevel.Compatible;
    public string? Recommendation { get; set; }
}

public class SystemCompatibilityReport
{
    public CompatibilityLevel OverallLevel { get; set; } = CompatibilityLevel.Compatible;
    public string SummaryMessage { get; set; } = "Hệ thống đáp ứng tốt các yêu cầu vận hành.";
    
    public string OperatingSystem { get; set; } = string.Empty;
    public string WindowsBuild { get; set; } = string.Empty;
    public bool Is64Bit { get; set; } = true;
    public string Architecture { get; set; } = "x64";
    public string DeviceFormFactor { get; set; } = "Desktop / Laptop";
    public string MachineName { get; set; } = Environment.MachineName;
    public bool IsAdministrator { get; set; }
    public bool IsSpoolerRunning { get; set; }
    
    public List<DiagnosticItem> Items { get; set; } = new();
    
    public bool CanRunAsServer => OverallLevel != CompatibilityLevel.Incompatible;
    public bool CanRunAsClient => OverallLevel != CompatibilityLevel.Incompatible;
}

