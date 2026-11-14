using Windows.Devices.Geolocation;
using RPM.Core.Cloud;

namespace RPM.App.Services;

public sealed class WindowsLocationService
{
    public async Task<CloudLocationEvidence?> TryGetCurrentAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            var access = await Geolocator.RequestAccessAsync().AsTask(cancellationToken);
            if (access != GeolocationAccessStatus.Allowed)
                return null;

            var locator = new Geolocator { DesiredAccuracyInMeters = 100 };
            var position = await locator.GetGeopositionAsync(
                TimeSpan.Zero,
                TimeSpan.FromSeconds(10)).AsTask(cancellationToken);
            var coordinate = position.Coordinate;
            var point = coordinate.Point.Position;
            var accuracy = coordinate.Accuracy;
            if (accuracy <= 0) return null;

            return new CloudLocationEvidence(
                point.Latitude,
                point.Longitude,
                accuracy,
                position.Coordinate.Timestamp.UtcDateTime);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
