using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RPM.Core.Config;

namespace RPM.Core.Cloud;

/// <summary>
/// Typed HTTPS client for the coordination API. It does not contain server secrets.
/// </summary>
public sealed class CloudApiClient : IDisposable
{
    public static readonly Uri ProductionEndpoint = new("https://print-exe.vercel.app/");

    private readonly HttpClient _httpClient;
    private readonly CloudDeviceTokenStore _tokenStore;
    private readonly CloudDeviceKeyStore _keyStore;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public CloudApiClient(Uri endpoint, CloudDeviceTokenStore? tokenStore = null, CloudDeviceKeyStore? keyStore = null, HttpMessageHandler? handler = null)
    {
        if (endpoint.Scheme != Uri.UriSchemeHttps && !string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Cloud API must use HTTPS outside localhost.", nameof(endpoint));
        }

        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        _httpClient.BaseAddress = endpoint.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? endpoint
            : new Uri(endpoint + "/");
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _tokenStore = tokenStore ?? new CloudDeviceTokenStore();
        _keyStore = keyStore ?? new CloudDeviceKeyStore();
    }

    public async Task<CloudDeviceRegistrationResponse> RegisterDeviceAsync(
        AppSettings settings,
        string enrollmentSecret,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(enrollmentSecret)) throw new ArgumentException("Enrollment secret is required.", nameof(enrollmentSecret));
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/devices/register")
        {
            Content = JsonContent.Create(new CloudDeviceRegistrationRequest(
                settings.DeviceId,
                settings.DeviceName,
                settings.Role == RPM.Core.Models.AppRole.Server ? "HOST" : "CLIENT",
                settings.AppVersion,
                _keyStore.GetOrCreatePublicKey()), options: _jsonOptions)
        };
        request.Headers.Add("X-Enrollment-Secret", enrollmentSecret);
        var response = await SendAsync<CloudDeviceRegistrationResponse>(request, cancellationToken, authenticate: false)
            ?? throw new InvalidOperationException("Cloud registration returned an empty response.");
        _tokenStore.Save(response.DeviceToken);
        return response;
    }

    public Task<CloudDeviceProfile?> GetProfileAsync(CancellationToken cancellationToken = default) =>
        GetAsync<CloudDeviceProfile>("api/v1/devices/me", cancellationToken);

    public Task<CloudDeviceProfile?> GetDeviceProfileAsync(string deviceId, CancellationToken cancellationToken = default) =>
        GetAsync<CloudDeviceProfile>($"api/v1/devices/{Uri.EscapeDataString(deviceId)}", cancellationToken);

    public Task<CloudHeartbeatResponse?> HeartbeatAsync(CancellationToken cancellationToken = default) =>
        PostAsync<CloudHeartbeatResponse>("api/v1/devices/heartbeat", null, cancellationToken);

    public Task<CloudPairingCodeResponse?> IssuePairingCodeAsync(string sessionId, CancellationToken cancellationToken = default) =>
        PostAsync<CloudPairingCodeResponse>("api/v1/pairing-codes/issue", new { sessionId }, cancellationToken);

    public Task<CloudPairingCodeResponse?> GetCurrentPairingCodeAsync(CancellationToken cancellationToken = default) =>
        GetAsync<CloudPairingCodeResponse>("api/v1/pairing-codes/current", cancellationToken);

    public Task<CloudPairingCodeResponse?> RevokePairingCodeAsync(string sessionId, CancellationToken cancellationToken = default) =>
        PostAsync<CloudPairingCodeResponse>("api/v1/pairing-codes/revoke", new { sessionId }, cancellationToken);

    public Task<CloudPairingResolution?> ResolvePairingCodeAsync(string code, CloudLocationEvidence? location = null, CancellationToken cancellationToken = default) =>
        PostAsync<CloudPairingResolution>("api/v1/pairing-codes/resolve", new
        {
            code,
            latitude = location?.Latitude,
            longitude = location?.Longitude,
            accuracyMeters = location?.AccuracyMeters,
            locationTimestamp = location?.LocationTimestamp
        }, cancellationToken);

    public Task<CloudGeofenceSettings?> GetGeofenceAsync(CancellationToken cancellationToken = default) =>
        GetAsync<CloudGeofenceSettings>("api/v1/devices/geofence", cancellationToken);

    public Task<IReadOnlyList<CloudPendingPairing>?> ListPendingPairingsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<CloudPendingPairing>>("api/v1/pairings/pending", cancellationToken);

    public Task<CloudPairingSummary?> GetPairingSummaryAsync(CancellationToken cancellationToken = default) =>
        GetAsync<CloudPairingSummary>("api/v1/pairings/summary", cancellationToken);

    public Task<CloudPairingResult?> DecidePairingAsync(string pairingId, bool approve, IReadOnlyList<string> allowedPrinters, CancellationToken cancellationToken = default) =>
        PostAsync<CloudPairingResult>($"api/v1/pairings/{Uri.EscapeDataString(pairingId)}/decision", new CloudPairingDecision(approve, allowedPrinters), cancellationToken);

    public Task<CloudPairingResult?> GetPairingAsync(string pairingId, CancellationToken cancellationToken = default) =>
        GetAsync<CloudPairingResult>($"api/v1/pairings/{Uri.EscapeDataString(pairingId)}", cancellationToken);

    public Task<IReadOnlyList<CloudPrinter>?> ListPrintersAsync(string hostId, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<CloudPrinter>>($"api/v1/printers?hostId={Uri.EscapeDataString(hostId)}", cancellationToken);

    public Task<CloudPrinter?> RegisterPrinterAsync(CloudPrinterRegistration payload, CancellationToken cancellationToken = default) =>
        PostAsync<CloudPrinter>("api/v1/printers", payload, cancellationToken);

    public Task<CloudPrintJob?> CreateJobAsync(CloudPrintJobRequest payload, string idempotencyKey, CancellationToken cancellationToken = default) =>
        PostAsync<CloudPrintJob>("api/v1/jobs", payload, cancellationToken, new Dictionary<string, string> { ["Idempotency-Key"] = idempotencyKey });

    public Task<IReadOnlyList<CloudPrintJob>?> ListHostJobsAsync(string status = "QUEUED", int limit = 50, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<CloudPrintJob>>($"api/v1/jobs?limit={limit}&status={Uri.EscapeDataString(status ?? string.Empty)}", cancellationToken);

    public Task<CloudPrintJob?> GetJobAsync(string jobId, CancellationToken cancellationToken = default) =>
        GetAsync<CloudPrintJob>($"api/v1/jobs/{Uri.EscapeDataString(jobId)}", cancellationToken);

    public Task<CloudJobClaim?> ClaimJobAsync(string jobId, int leaseSeconds = 120, CancellationToken cancellationToken = default) =>
        PostAsync<CloudJobClaim>($"api/v1/jobs/{Uri.EscapeDataString(jobId)}/claim", new { leaseSeconds }, cancellationToken);

    public Task<CloudPrintJob?> RenewClaimAsync(string jobId, string claimToken, int leaseSeconds = 120, CancellationToken cancellationToken = default) =>
        PostAsync<CloudPrintJob>($"api/v1/jobs/{Uri.EscapeDataString(jobId)}/renew", new { claimToken, leaseSeconds }, cancellationToken);

    public Task<CloudPrintJob?> AcknowledgeJobAsync(string jobId, string claimToken, CancellationToken cancellationToken = default) =>
        PostAsync<CloudPrintJob>($"api/v1/jobs/{Uri.EscapeDataString(jobId)}/ack", new { claimToken }, cancellationToken);

    public Task<CloudPrintJob?> UpdateJobStatusAsync(string jobId, CloudJobStatusUpdate update, CancellationToken cancellationToken = default) =>
        PatchAsync<CloudPrintJob>($"api/v1/jobs/{Uri.EscapeDataString(jobId)}/status", new
        {
            claimToken = update.ClaimToken,
            status = update.Status,
            spoolerJobId = update.SpoolerJobId,
            failureCode = update.FailureCode,
            failureMessage = update.FailureMessage
        }, cancellationToken);

    public Task<CloudPrintJob?> CancelJobAsync(string jobId, string? reason = null, CancellationToken cancellationToken = default) =>
        PostAsync<CloudPrintJob>($"api/v1/jobs/{Uri.EscapeDataString(jobId)}/cancel", new { reason }, cancellationToken);

    public async Task<CloudPrintJob?> UploadDocumentAsync(string jobId, byte[] encryptedDocument, string contentType = "application/octet-stream", CancellationToken cancellationToken = default)
    {
        if (encryptedDocument.Length == 0) throw new ArgumentException("Encrypted document is empty.", nameof(encryptedDocument));
        using var request = new HttpRequestMessage(HttpMethod.Put, $"api/v1/jobs/{Uri.EscapeDataString(jobId)}/document");
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(encryptedDocument);
        content.Headers.ContentType = new(contentType);
        form.Add(content, "document", "document.bin");
        request.Content = form;
        return await SendAsync<CloudPrintJob>(request, cancellationToken);
    }

    public async Task<byte[]> DownloadDocumentAsync(string jobId, string claimToken, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/v1/jobs/{Uri.EscapeDataString(jobId)}/document");
        request.Headers.Add("X-Claim-Token", claimToken);
        return await SendBytesAsync(request, cancellationToken);
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        return await SendAsync<T>(request, cancellationToken);
    }

    private async Task<T?> PostAsync<T>(string path, object? payload, CancellationToken cancellationToken, Dictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (payload is not null) request.Content = JsonContent.Create(payload, options: _jsonOptions);
        if (headers is not null)
        {
            foreach (var header in headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return await SendAsync<T>(request, cancellationToken);
    }

    private async Task<T?> PatchAsync<T>(string path, object payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, path)
        {
            Content = JsonContent.Create(payload, options: _jsonOptions)
        };
        return await SendAsync<T>(request, cancellationToken);
    }

    private async Task<T?> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken, bool authenticate = true)
    {
        if (authenticate)
        {
            var token = _tokenStore.Load();
            if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Cloud device is not enrolled.");
            request.Headers.Authorization = new("Bearer", token);
        }
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Cloud API returned {(int)response.StatusCode}: {RedactDetail(detail)}", null, response.StatusCode);
        }
        if (response.Content.Headers.ContentLength == 0) return default;
        return await response.Content.ReadFromJsonAsync<T>(_jsonOptions, cancellationToken);
    }

    private async Task<byte[]> SendBytesAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = _tokenStore.Load();
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Cloud device is not enrolled.");
        request.Headers.Authorization = new("Bearer", token);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Cloud API returned {(int)response.StatusCode}: {RedactDetail(detail)}", null, response.StatusCode);
        }
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static string RedactDetail(string detail) => detail.Length > 240 ? detail[..240] : detail;

    public void Dispose() => _httpClient.Dispose();
}

public sealed class CloudDeviceTokenStore
{
    private readonly string _path;

    public CloudDeviceTokenStore(string? path = null, string? scope = null)
    {
        string fileName = string.IsNullOrWhiteSpace(scope)
            ? "cloud-device-token.bin"
            : $"cloud-device-token-{SanitizeScope(scope)}.bin";
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RemotePrintManager",
            fileName);
    }

    private static string SanitizeScope(string scope) =>
        string.Concat(scope.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));

    public void Save(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Token is required.", nameof(token));
        var folder = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("Token path has no parent directory.");
        Directory.CreateDirectory(folder);
#pragma warning disable CA1416
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser);
#pragma warning restore CA1416
        var tempPath = _path + ".tmp";
        File.WriteAllBytes(tempPath, protectedBytes);
        File.Move(tempPath, _path, true);
    }

    public bool HasToken => !string.IsNullOrWhiteSpace(Load());

    public string? Load()
    {
        if (!File.Exists(_path)) return null;
        try
        {
#pragma warning disable CA1416
            var token = ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser);
#pragma warning restore CA1416
            return Encoding.UTF8.GetString(token);
        }
        catch (CryptographicException) { return null; }
        catch (IOException) { return null; }
    }

    public void Clear()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}

public sealed class CloudDeviceKeyStore
{
    private readonly string _path;

    public CloudDeviceKeyStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RemotePrintManager",
            "cloud-device-private-key.bin");
    }

    public string GetOrCreatePublicKey()
    {
        using var rsa = LoadOrCreate();
        return Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
    }

    public RSA LoadOrCreate() 
    {
        if (File.Exists(_path))
        {
            try
            {
#pragma warning disable CA1416
                var privateKey = ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser);
#pragma warning restore CA1416
                var existing = RSA.Create();
                existing.ImportPkcs8PrivateKey(privateKey, out _);
                return existing;
            }
            catch (CryptographicException) { }
            catch (IOException) { }
        }

        using var rsa = RSA.Create(3072);
        var exported = rsa.ExportPkcs8PrivateKey();
#pragma warning disable CA1416
        var protectedBytes = ProtectedData.Protect(exported, null, DataProtectionScope.CurrentUser);
#pragma warning restore CA1416
        var folder = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("Key path has no parent directory.");
        Directory.CreateDirectory(folder);
        var tempPath = _path + ".tmp";
        File.WriteAllBytes(tempPath, protectedBytes);
        File.Move(tempPath, _path, true);
        return RSA.Create(rsa.ExportParameters(true));
    }
}
