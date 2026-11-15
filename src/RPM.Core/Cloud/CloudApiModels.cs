using System.Text.Json.Serialization;

namespace RPM.Core.Cloud;

public sealed record CloudDeviceRegistrationRequest(
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("appVersion")] string AppVersion,
    [property: JsonPropertyName("publicKey")] string? PublicKey = null,
    [property: JsonPropertyName("keyAlgorithm")] string KeyAlgorithm = "RSA-OAEP-SHA256");

public sealed record CloudDeviceRegistrationResponse(
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("deviceToken")] string DeviceToken,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("activationLabel")] string? ActivationLabel = null,
    [property: JsonPropertyName("activationExpiresAt")] DateTime? ActivationExpiresAt = null,
    [property: JsonPropertyName("activationMaxDevices")] int? ActivationMaxDevices = null,
    [property: JsonPropertyName("activationUsedDevices")] int? ActivationUsedDevices = null,
    [property: JsonPropertyName("activationGeofenceRadiusMeters")] int? ActivationGeofenceRadiusMeters = null);

public sealed record CloudDeviceProfile(
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("appVersion")] string AppVersion,
    [property: JsonPropertyName("isRevoked")] bool IsRevoked,
    [property: JsonPropertyName("lastSeenAt")] DateTime LastSeenAt,
    [property: JsonPropertyName("publicKey")] string? PublicKey = null,
    [property: JsonPropertyName("keyAlgorithm")] string KeyAlgorithm = "RSA-OAEP-SHA256",
    [property: JsonPropertyName("activationLabel")] string? ActivationLabel = null,
    [property: JsonPropertyName("activationExpiresAt")] DateTime? ActivationExpiresAt = null,
    [property: JsonPropertyName("activationMaxDevices")] int? ActivationMaxDevices = null,
    [property: JsonPropertyName("activationUsedDevices")] int? ActivationUsedDevices = null,
    [property: JsonPropertyName("activationGeofenceRadiusMeters")] int? ActivationGeofenceRadiusMeters = null);

public sealed record CloudActivationInfo(
    string? Label,
    DateTime? ExpiresAt,
    int? MaxDevices,
    int? UsedDevices,
    int? GeofenceRadiusMeters);

public sealed record CloudHeartbeatResponse(
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("lastSeenAt")] DateTime LastSeenAt);

public sealed record CloudPairingCodeResponse(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("issuedAt")] DateTime IssuedAt,
    [property: JsonPropertyName("expiresAt")] DateTime ExpiresAt,
    [property: JsonPropertyName("status")] string Status);

public sealed record CloudPairingResolution(
    [property: JsonPropertyName("pairingId")] string PairingId,
    [property: JsonPropertyName("hostId")] string HostId,
    [property: JsonPropertyName("clientId")] string ClientId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("locationStatus")] string LocationStatus = "UNVERIFIED");

public sealed record CloudLocationEvidence(
    [property: JsonPropertyName("latitude")] double Latitude,
    [property: JsonPropertyName("longitude")] double Longitude,
    [property: JsonPropertyName("accuracyMeters")] double AccuracyMeters,
    [property: JsonPropertyName("locationTimestamp")] DateTime LocationTimestamp);

public sealed record CloudGeofenceSettings(
    [property: JsonPropertyName("hostId")] string HostId,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("latitude")] double? Latitude,
    [property: JsonPropertyName("longitude")] double? Longitude,
    [property: JsonPropertyName("radiusMeters")] double RadiusMeters);

public sealed record CloudPendingPairing(
    [property: JsonPropertyName("pairingId")] string PairingId,
    [property: JsonPropertyName("clientId")] string ClientId,
    [property: JsonPropertyName("clientName")] string ClientName,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("expiresAt")] DateTime ExpiresAt);

public sealed record CloudPairingSummary(
    [property: JsonPropertyName("hostId")] string HostId,
    [property: JsonPropertyName("approvedCount")] int ApprovedCount,
    [property: JsonPropertyName("onlineCount")] int OnlineCount,
    [property: JsonPropertyName("maxClients")] int MaxClients);

public sealed record CloudPairingDecision(
    [property: JsonPropertyName("approve")] bool Approve,
    [property: JsonPropertyName("allowedPrinters")] IReadOnlyList<string> AllowedPrinters);

public sealed record CloudPairingResult(
    [property: JsonPropertyName("pairingId")] string PairingId,
    [property: JsonPropertyName("hostId")] string HostId,
    [property: JsonPropertyName("clientId")] string ClientId,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("expiresAt")] DateTime ExpiresAt);

public sealed record CloudPrinter(
    [property: JsonPropertyName("printerId")] string PrinterId,
    [property: JsonPropertyName("hostId")] string HostId,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("reportedStatus")] string ReportedStatus,
    [property: JsonPropertyName("isShared")] bool IsShared,
    [property: JsonPropertyName("metadata")] Dictionary<string, object?> Metadata);

public sealed record CloudPrinterRegistration(
    [property: JsonPropertyName("printerId")] string PrinterId,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("reportedStatus")] string ReportedStatus,
    [property: JsonPropertyName("isShared")] bool IsShared,
    [property: JsonPropertyName("metadata")] Dictionary<string, object?> Metadata);

public sealed record CloudPrintJobRequest(
    [property: JsonPropertyName("hostId")] string HostId,
    [property: JsonPropertyName("printerId")] string PrinterId,
    [property: JsonPropertyName("metadata")] Dictionary<string, object?> Metadata,
    [property: JsonPropertyName("dataRef")] string? DataRef = null,
    [property: JsonPropertyName("contentSha256")] string? ContentSha256 = null,
    [property: JsonPropertyName("contentSize")] long? ContentSize = null,
    [property: JsonPropertyName("contentType")] string? ContentType = null,
    [property: JsonPropertyName("latitude")] double? Latitude = null,
    [property: JsonPropertyName("longitude")] double? Longitude = null,
    [property: JsonPropertyName("accuracyMeters")] double? AccuracyMeters = null,
    [property: JsonPropertyName("locationTimestamp")] DateTime? LocationTimestamp = null);

public sealed record CloudPrintJob(
    [property: JsonPropertyName("jobId")] string JobId,
    [property: JsonPropertyName("idempotencyKey")] string IdempotencyKey,
    [property: JsonPropertyName("clientId")] string ClientId,
    [property: JsonPropertyName("hostId")] string HostId,
    [property: JsonPropertyName("printerId")] string PrinterId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("metadata")] Dictionary<string, object?> Metadata,
    [property: JsonPropertyName("dataRef")] string? DataRef,
    [property: JsonPropertyName("contentSha256")] string? ContentSha256,
    [property: JsonPropertyName("contentSize")] long? ContentSize,
    [property: JsonPropertyName("contentType")] string? ContentType,
    [property: JsonPropertyName("claimExpiresAt")] DateTime? ClaimExpiresAt,
    [property: JsonPropertyName("spoolerJobId")] string? SpoolerJobId,
    [property: JsonPropertyName("failureCode")] string? FailureCode,
    [property: JsonPropertyName("failureMessage")] string? FailureMessage,
    [property: JsonPropertyName("completedAt")] DateTime? CompletedAt);

public sealed record CloudJobClaim(
    [property: JsonPropertyName("jobId")] string JobId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("claimToken")] string ClaimToken,
    [property: JsonPropertyName("claimExpiresAt")] DateTime? ClaimExpiresAt);

public sealed record CloudJobStatusUpdate(
    string ClaimToken,
    string Status,
    string? SpoolerJobId = null,
    string? FailureCode = null,
    string? FailureMessage = null);
