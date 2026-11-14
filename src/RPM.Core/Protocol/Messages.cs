using System.Text.Json.Serialization;
using RPM.Core.Models;

namespace RPM.Core.Protocol;

public enum MessageType
{
    // Pairing & Handshake
    RegisterServer = 1,
    RegisterServerAck = 2,
    ClientJoin = 3,
    ClientJoinResponse = 4,
    DeviceApproved = 5,
    DeviceRevoked = 6,

    // Status & Ping
    Heartbeat = 10,
    HeartbeatAck = 11,
    PrinterStatusUpdate = 12,
    PrintersListUpdate = 13,

    // Print Workflow
    SubmitPrintJob = 20,
    SubmitPrintJobAck = 21,
    PrintJobProgress = 22,
    CancelPrintJob = 23,

    // Generic
    Error = 99
}

public class NetworkPacket
{
    public string PacketId { get; set; } = Guid.NewGuid().ToString("N");
    public MessageType Type { get; set; }
    public string SenderId { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public string PairingCode { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Payload encrypted with AES-256-GCM or plain JSON if during initial handshake.
    /// </summary>
    public string PayloadJson { get; set; } = string.Empty;
    public string? EncryptedPayloadBase64 { get; set; }
    public string? NonceBase64 { get; set; }
    public string? TagBase64 { get; set; }
    public string? AuthTagBase64 { get; set; }
}

public class RegisterServerPayload
{
    public string ServerDeviceId { get; set; } = string.Empty;
    public string ServerDeviceName { get; set; } = string.Empty;
    public string PairingCode { get; set; } = string.Empty;
    public SharedPrinterInfo? SharedPrinter { get; set; }
    public List<SharedPrinterInfo> SharedPrinters { get; set; } = new();
}

public class ClientJoinPayload
{
    public DeviceInfo ClientDevice { get; set; } = new();
    public string PairingCode { get; set; } = string.Empty;
}

public class ClientJoinResponsePayload
{
    public bool Approved { get; set; }
    public bool IsPending { get; set; }
    public string Message { get; set; } = string.Empty;
    public string EncryptionSecret { get; set; } = string.Empty;
    public SharedPrinterInfo? SharedPrinter { get; set; }
    public List<SharedPrinterInfo> AvailablePrinters { get; set; } = new();
}

public class PrintersListUpdatePayload
{
    public List<SharedPrinterInfo> Printers { get; set; } = new();
}

public class PrintJobPayload
{
    public PrintJob JobInfo { get; set; } = new();
    /// <summary>
    /// Base64 encoded file data (PDF / PostScript / RAW).
    /// If encrypted, this field holds the encrypted ciphertext.
    /// </summary>
    public string DataBase64 { get; set; } = string.Empty;
    public string FileExtension { get; set; } = ".pdf";
}

public class PrintJobProgressPayload
{
    public string JobId { get; set; } = string.Empty;
    public PrintJobStatus Status { get; set; }
    public string Message { get; set; } = string.Empty;
}

