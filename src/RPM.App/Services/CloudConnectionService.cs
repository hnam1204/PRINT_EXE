using RPM.Core.Cloud;
using RPM.Core.Config;
using RPM.Core.Models;
using System.Net;
using System.Net.Http;

namespace RPM.App.Services;

/// <summary>
/// Owns Cloud enrollment and liveness. Cloud is opt-in; Relay remains the fallback.
/// </summary>
public sealed class CloudConnectionService : IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly CloudDeviceTokenStore _tokenStore;
    private readonly CloudDeviceKeyStore _keyStore = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private Task? _heartbeatTask;
    private bool _disabledByServer;

    public CloudApiClient? Client { get; private set; }
    public CloudActivationInfo? ActivationInfo { get; private set; }
    public bool IsOnline { get; private set; }
    public bool HasStoredToken => _tokenStore.HasToken;
    public event EventHandler<string>? OnLog;
    public event EventHandler? OnStateChanged;
    public event EventHandler? OnEnrollmentRevoked;

    public CloudConnectionService(AppSettings settings)
    {
        _settings = settings;
        _tokenStore = new CloudDeviceTokenStore(scope: $"{settings.DeviceId}-{settings.Role}");
        ActivationInfo = new CloudActivationInfo(
            string.IsNullOrWhiteSpace(settings.CloudActivationLabel) ? null : settings.CloudActivationLabel,
            settings.CloudActivationExpiresAt,
            settings.CloudActivationMaxDevices,
            settings.CloudActivationUsedDevices,
            settings.CloudActivationGeofenceRadiusMeters);
    }

    public async Task<bool> EnrollAsync(string enrollmentSecret, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(enrollmentSecret)) throw new ArgumentException("Enrollment secret is required.", nameof(enrollmentSecret));
        _disabledByServer = false;
        var client = GetOrCreateClient();
        var registration = await client.RegisterDeviceAsync(_settings, enrollmentSecret, cancellationToken);
        ActivationInfo = new CloudActivationInfo(registration.ActivationLabel, registration.ActivationExpiresAt, registration.ActivationMaxDevices, registration.ActivationUsedDevices, registration.ActivationGeofenceRadiusMeters);
        SaveActivationInfo();
        _settings.CloudHostId = _settings.Role == AppRole.Server ? registration.DeviceId : _settings.CloudHostId;
        ConfigManager.Save(_settings);
        return await ProbeAsync(cancellationToken);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        // A DPAPI-protected token is the durable enrollment proof. Restore the
        // Cloud flag after restart so the app revalidates it automatically.
        if (!_settings.CloudTransportEnabled && !_tokenStore.HasToken) return;
        if (!_settings.CloudTransportEnabled)
        {
            _settings.CloudTransportEnabled = true;
            ConfigManager.Save(_settings);
            OnLog?.Invoke(this, "Đã khôi phục kết nối máy chủ từ xa từ token đã lưu; đang kiểm tra lại máy chủ.");
        }
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            if (_heartbeatTask is not null) return;
            if (!_tokenStore.HasToken)
            {
                SetOffline("Kết nối máy chủ từ xa chưa được kích hoạt; tiếp tục dùng kết nối nội bộ.");
                return;
            }

            _heartbeatTask = Task.Run(HeartbeatLoopAsync);
            await ProbeAsync(cancellationToken);
        }
        finally { _lifecycleLock.Release(); }
    }

    public async Task<bool> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (_disabledByServer) return false;
        try
        {
            var profile = await GetOrCreateClient().GetProfileAsync(cancellationToken);
            if (profile is null || profile.IsRevoked)
            {
                SetOffline("Thiết bị trên máy chủ từ xa đã bị thu hồi hoặc chưa sẵn sàng.");
                return false;
            }
            string expectedRole = _settings.Role == AppRole.Server ? "HOST" : "CLIENT";
            if (!string.Equals(profile.Role, expectedRole, StringComparison.OrdinalIgnoreCase))
            {
                SetOffline($"Mã xác thực máy chủ từ xa thuộc vai trò {profile.Role}; cần kích hoạt lại cho vai trò {expectedRole}.");
                return false;
            }
            ActivationInfo = new CloudActivationInfo(profile.ActivationLabel, profile.ActivationExpiresAt, profile.ActivationMaxDevices, profile.ActivationUsedDevices, profile.ActivationGeofenceRadiusMeters);
            SaveActivationInfo();
            var heartbeat = await GetOrCreateClient().HeartbeatAsync(cancellationToken);
            if (heartbeat is null || !string.Equals(heartbeat.Status, "ONLINE", StringComparison.OrdinalIgnoreCase))
            {
                SetOffline("Máy chủ từ xa chưa xác nhận thiết bị trực tuyến.");
                return false;
            }
            IsOnline = true;
            OnStateChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            InvalidateEnrollment("Mã kích hoạt đã bị thu hồi hoặc không còn quyền sử dụng.");
            return false;
        }
        catch (Exception ex)
        {
            SetOffline($"Máy chủ từ xa chưa kết nối: {ex.Message}");
            return false;
        }
    }

    private async Task HeartbeatLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                if (!await ProbeAsync(_stop.Token))
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), _stop.Token);
                }
                else
                {
                    await Task.Delay(TimeSpan.FromSeconds(45), _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                SetOffline($"Lỗi kiểm tra kết nối máy chủ từ xa: {ex.Message}");
                try { await Task.Delay(TimeSpan.FromSeconds(15), _stop.Token); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private CloudApiClient GetOrCreateClient() => Client ??= new CloudApiClient(
        string.IsNullOrWhiteSpace(_settings.CloudApiBaseUrl)
            ? CloudApiClient.ProductionEndpoint
            : new Uri(_settings.CloudApiBaseUrl),
        _tokenStore,
        _keyStore);

    private void SetOffline(string message)
    {
        IsOnline = false;
        OnLog?.Invoke(this, message);
        OnStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void InvalidateEnrollment(string reason)
    {
        _disabledByServer = true;
        _tokenStore.Clear();
        ActivationInfo = null;
        _settings.CloudTransportEnabled = false;
        _settings.CloudEnrollmentKeyPrefix = string.Empty;
        _settings.CloudActivationLabel = string.Empty;
        _settings.CloudActivationExpiresAt = null;
        _settings.CloudActivationMaxDevices = null;
        _settings.CloudActivationUsedDevices = null;
        _settings.CloudActivationGeofenceRadiusMeters = null;
        _settings.CloudHostId = string.Empty;
        _settings.CloudHostPublicKey = string.Empty;
        _settings.CloudPairingCode = string.Empty;
        _settings.CloudPairingExpiresAt = null;
        _settings.CloudPairingSessionId = string.Empty;
        _settings.CloudPairingId = string.Empty;
        ConfigManager.Save(_settings);
        IsOnline = false;
        OnLog?.Invoke(this, reason);
        OnEnrollmentRevoked?.Invoke(this, EventArgs.Empty);
        OnStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SaveActivationInfo()
    {
        if (ActivationInfo is null) return;
        string label = ActivationInfo.Label ?? string.Empty;
        if (string.Equals(_settings.CloudActivationLabel, label, StringComparison.Ordinal) &&
            _settings.CloudActivationExpiresAt == ActivationInfo.ExpiresAt &&
            _settings.CloudActivationMaxDevices == ActivationInfo.MaxDevices &&
            _settings.CloudActivationUsedDevices == ActivationInfo.UsedDevices &&
            _settings.CloudActivationGeofenceRadiusMeters == ActivationInfo.GeofenceRadiusMeters)
        {
            return;
        }

        _settings.CloudActivationLabel = label;
        _settings.CloudActivationExpiresAt = ActivationInfo.ExpiresAt;
        _settings.CloudActivationMaxDevices = ActivationInfo.MaxDevices;
        _settings.CloudActivationUsedDevices = ActivationInfo.UsedDevices;
        _settings.CloudActivationGeofenceRadiusMeters = ActivationInfo.GeofenceRadiusMeters;
        ConfigManager.Save(_settings);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_heartbeatTask is not null)
        {
            try { await _heartbeatTask; } catch (OperationCanceledException) { }
        }
        Client?.Dispose();
        _lifecycleLock.Dispose();
        _stop.Dispose();
    }
}
