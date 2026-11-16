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
    private enum RoleReenrollmentResult
    {
        NoStoredActivation,
        Succeeded,
        TemporarilyUnavailable,
        Rejected
    }

    private readonly AppSettings _settings;
    private readonly CloudDeviceTokenStore _tokenStore;
    private readonly CloudActivationSecretStore _activationSecretStore;
    private readonly CloudDeviceKeyStore _keyStore = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _roleReenrollmentLock = new(1, 1);
    private Task? _heartbeatTask;
    private RealtimeGatewayClient? _realtimeGateway;
    private bool _disabledByServer;

    public CloudApiClient? Client { get; private set; }
    public CloudActivationInfo? ActivationInfo { get; private set; }
    public bool IsOnline { get; private set; }
    public bool IsCustomerActivation => _settings.CloudUsesCustomerActivation || _activationSecretStore.HasSecret;
    public bool HasStoredToken => _tokenStore.HasToken;
    public event EventHandler<string>? OnLog;
    public event EventHandler? OnStateChanged;
    public event EventHandler? OnEnrollmentRevoked;
    public event EventHandler<CloudRealtimeEvent>? OnRealtimeEvent;

    public CloudConnectionService(AppSettings settings)
    {
        _settings = settings;
        _tokenStore = CloudDeviceTokenStore.ForDevice(settings.DeviceId);
        _activationSecretStore = CloudActivationSecretStore.ForDevice(settings.DeviceId);
        MigrateLegacyRoleToken(settings.DeviceId);
        ActivationInfo = new CloudActivationInfo(
            string.IsNullOrWhiteSpace(settings.CloudActivationLabel) ? null : settings.CloudActivationLabel,
            settings.CloudActivationExpiresAt,
            settings.CloudActivationMaxDevices,
            settings.CloudActivationUsedDevices,
            settings.CloudActivationGeofenceRadiusMeters,
            string.IsNullOrWhiteSpace(settings.CloudActivationGeofencePolicy) ? "LIMITED" : settings.CloudActivationGeofencePolicy);
    }

    public async Task<bool> EnrollAsync(string enrollmentSecret, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(enrollmentSecret)) throw new ArgumentException("Enrollment secret is required.", nameof(enrollmentSecret));
        _disabledByServer = false;
        var client = GetOrCreateClient();
        var registration = await client.RegisterDeviceAsync(_settings, enrollmentSecret, cancellationToken);
        bool isCustomerActivation = registration.IsActivationCode ||
            (!string.IsNullOrWhiteSpace(registration.ActivationLabel) &&
             !string.Equals(registration.ActivationLabel, "Quản trị viên", StringComparison.OrdinalIgnoreCase));
        _settings.CloudUsesCustomerActivation = isCustomerActivation;
        if (isCustomerActivation)
        {
            _activationSecretStore.Save(enrollmentSecret);
        }
        else
        {
            _activationSecretStore.Clear();
        }
        ActivationInfo = new CloudActivationInfo(registration.ActivationLabel, registration.ActivationExpiresAt, registration.ActivationMaxDevices, registration.ActivationUsedDevices, registration.ActivationGeofenceRadiusMeters, registration.ActivationGeofencePolicy);
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
            if (profile is null)
            {
                SetOffline("Thiết bị trên máy chủ từ xa chưa sẵn sàng.");
                return false;
            }
            bool activationExpired = profile.ActivationExpiresAt is DateTime expiresAt &&
                AsUtc(expiresAt) <= DateTime.UtcNow;
            if (profile.IsRevoked || activationExpired)
            {
                InvalidateEnrollment(profile.IsRevoked
                    ? "Mã kích hoạt đã bị thu hồi; kết nối máy chủ từ xa đã bị vô hiệu hóa."
                    : activationExpired
                        ? "Mã kích hoạt đã hết hạn; kết nối máy chủ từ xa đã bị vô hiệu hóa."
                        : "Mã kích hoạt không còn hợp lệ; kết nối máy chủ từ xa đã bị vô hiệu hóa.");
                return false;
            }
            string expectedRole = _settings.Role == AppRole.Server ? "HOST" : "CLIENT";
            if (!string.Equals(profile.Role, expectedRole, StringComparison.OrdinalIgnoreCase))
            {
                var reenrollment = await TryReenrollForRoleAsync(cancellationToken);
                if (reenrollment != RoleReenrollmentResult.Succeeded)
                {
                    SetOffline(reenrollment switch
                    {
                        RoleReenrollmentResult.NoStoredActivation =>
                            $"Mã xác thực máy chủ từ xa thuộc vai trò {profile.Role}; cần nhập lại mã kích hoạt để chuyển sang vai trò {expectedRole}.",
                        RoleReenrollmentResult.Rejected =>
                            "Mã kích hoạt không được máy chủ từ xa chấp nhận để đổi vai trò.",
                        _ =>
                            "Chưa thể đồng bộ vai trò với máy chủ từ xa. Vui lòng kiểm tra Internet rồi thử lại."
                    });
                    return false;
                }

                profile = await GetOrCreateClient().GetProfileAsync(cancellationToken);
                if (profile is null || profile.IsRevoked || !string.Equals(profile.Role, expectedRole, StringComparison.OrdinalIgnoreCase))
                {
                    SetOffline($"Chưa thể chuyển thiết bị sang vai trò {expectedRole} trên máy chủ từ xa.");
                    return false;
                }
            }
            ActivationInfo = new CloudActivationInfo(profile.ActivationLabel, profile.ActivationExpiresAt, profile.ActivationMaxDevices, profile.ActivationUsedDevices, profile.ActivationGeofenceRadiusMeters, profile.ActivationGeofencePolicy);
            SaveActivationInfo();
            var heartbeat = await GetOrCreateClient().HeartbeatAsync(cancellationToken);
            if (heartbeat is null || !string.Equals(heartbeat.Status, "ONLINE", StringComparison.OrdinalIgnoreCase))
            {
                SetOffline("Máy chủ từ xa chưa xác nhận thiết bị trực tuyến.");
                return false;
            }
            IsOnline = true;
            await EnsureRealtimeGatewayAsync(cancellationToken);
            OnStateChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            InvalidateEnrollment("Mã kích hoạt đã bị thu hồi hoặc không còn quyền sử dụng.");
            return false;
        }
        catch (Exception)
        {
            SetOffline("Không thể kiểm tra dịch vụ từ xa. Vui lòng kiểm tra kết nối Internet rồi thử lại.");
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
            catch (Exception)
            {
                SetOffline("Không thể kiểm tra dịch vụ từ xa. Vui lòng kiểm tra kết nối Internet rồi thử lại.");
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
        CloudDeviceTokenStore.ClearAllForDevice(_settings.DeviceId);
        _activationSecretStore.Clear();
        ActivationInfo = null;
        _settings.CloudTransportEnabled = false;
        _settings.CloudUsesCustomerActivation = false;
        _settings.CloudEnrollmentKeyPrefix = string.Empty;
        _settings.CloudActivationLabel = string.Empty;
        _settings.CloudActivationExpiresAt = null;
        _settings.CloudActivationMaxDevices = null;
        _settings.CloudActivationUsedDevices = null;
        _settings.CloudActivationGeofencePolicy = "LIMITED";
        _settings.CloudActivationGeofenceRadiusMeters = null;
        _settings.CloudHostId = string.Empty;
        _settings.CloudHostPublicKey = string.Empty;
        _settings.CloudHostPublicKeyFingerprint = string.Empty;
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

    private async Task EnsureRealtimeGatewayAsync(CancellationToken cancellationToken)
    {
        if (!_settings.RealtimeEnabled || _realtimeGateway is not null || Client is null) return;
        try
        {
            _realtimeGateway = new RealtimeGatewayClient(
                _settings.DeviceId,
                token => Client.CreateRealtimeSessionAsync(token));
            _realtimeGateway.OnEvent += (sender, realtimeEvent) => OnRealtimeEvent?.Invoke(this, realtimeEvent);
            _realtimeGateway.OnLog += (sender, message) => OnLog?.Invoke(this, message);
            await _realtimeGateway.StartAsync();
        }
        catch (Exception ex)
        {
            _realtimeGateway = null;
            OnLog?.Invoke(this, $"[Realtime] Chưa khởi động được Gateway; dùng đối soát dự phòng: {ex.Message}");
        }
    }

    private void SaveActivationInfo()
    {
        if (ActivationInfo is null) return;
        string label = ActivationInfo.Label ?? string.Empty;
        if (string.Equals(_settings.CloudActivationLabel, label, StringComparison.Ordinal) &&
            _settings.CloudActivationExpiresAt == ActivationInfo.ExpiresAt &&
            _settings.CloudActivationMaxDevices == ActivationInfo.MaxDevices &&
            _settings.CloudActivationUsedDevices == ActivationInfo.UsedDevices &&
            string.Equals(_settings.CloudActivationGeofencePolicy, ActivationInfo.GeofencePolicy, StringComparison.Ordinal) &&
            _settings.CloudActivationGeofenceRadiusMeters == ActivationInfo.GeofenceRadiusMeters)
        {
            return;
        }

        _settings.CloudActivationLabel = label;
        _settings.CloudActivationExpiresAt = ActivationInfo.ExpiresAt;
        _settings.CloudActivationMaxDevices = ActivationInfo.MaxDevices;
        _settings.CloudActivationUsedDevices = ActivationInfo.UsedDevices;
        _settings.CloudActivationGeofencePolicy = ActivationInfo.GeofencePolicy ?? "LIMITED";
        _settings.CloudActivationGeofenceRadiusMeters = ActivationInfo.GeofenceRadiusMeters;
        ConfigManager.Save(_settings);
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private async Task<RoleReenrollmentResult> TryReenrollForRoleAsync(CancellationToken cancellationToken)
    {
        await _roleReenrollmentLock.WaitAsync(cancellationToken);
        try
        {
            string? activationSecret = _activationSecretStore.Load();
            if (string.IsNullOrWhiteSpace(activationSecret)) return RoleReenrollmentResult.NoStoredActivation;

            try
            {
                var registration = await GetOrCreateClient().RegisterDeviceAsync(_settings, activationSecret, cancellationToken);
                ActivationInfo = new CloudActivationInfo(
                    registration.ActivationLabel,
                    registration.ActivationExpiresAt,
                    registration.ActivationMaxDevices,
                    registration.ActivationUsedDevices,
                    registration.ActivationGeofenceRadiusMeters,
                    registration.ActivationGeofencePolicy);
                _settings.CloudUsesCustomerActivation = true;
                SaveActivationInfo();
                _settings.CloudHostId = _settings.Role == AppRole.Server ? registration.DeviceId : _settings.CloudHostId;
                ConfigManager.Save(_settings);
                return RoleReenrollmentResult.Succeeded;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                InvalidateEnrollment("Mã kích hoạt không còn quyền đăng ký lại thiết bị.");
                return RoleReenrollmentResult.Rejected;
            }
            catch (HttpRequestException)
            {
                return RoleReenrollmentResult.TemporarilyUnavailable;
            }
        }
        finally
        {
            _roleReenrollmentLock.Release();
        }
    }

    private void MigrateLegacyRoleToken(string deviceId)
    {
        if (_tokenStore.HasToken)
        {
            DeleteLegacyRoleTokens(deviceId);
            return;
        }

        string currentRole = _settings.Role == AppRole.Server ? AppRole.Server.ToString() : AppRole.Client.ToString();
        string otherRole = _settings.Role == AppRole.Server ? AppRole.Client.ToString() : AppRole.Server.ToString();
        foreach (string role in new[] { currentRole, otherRole })
        {
            var legacyStore = CloudDeviceTokenStore.ForRole(deviceId, role);
            string? token = legacyStore.Load();
            if (string.IsNullOrWhiteSpace(token)) continue;

            _tokenStore.Save(token);
            DeleteLegacyRoleTokens(deviceId);
            OnLog?.Invoke(this, "Đã chuyển thông tin kết nối máy chủ từ xa sang kho dùng chung cho thiết bị.");
            return;
        }
    }

    private static void DeleteLegacyRoleTokens(string deviceId)
    {
        foreach (string role in new[] { "Server", "Client", "HOST", "CLIENT" })
        {
            CloudDeviceTokenStore.ForRole(deviceId, role).Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_heartbeatTask is not null)
        {
            try { await _heartbeatTask; } catch (OperationCanceledException) { }
        }
        Client?.Dispose();
        if (_realtimeGateway is not null)
        {
            try { await _realtimeGateway.DisposeAsync(); } catch { }
        }
        _lifecycleLock.Dispose();
        _roleReenrollmentLock.Dispose();
        _stop.Dispose();
    }
}
