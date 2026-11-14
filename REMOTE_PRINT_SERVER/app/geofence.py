from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from math import asin, cos, isfinite, radians, sin, sqrt


@dataclass(frozen=True)
class GeofenceResult:
    status: str
    distance_meters: float | None = None
    reason: str | None = None


def verify_location(
    *,
    host_latitude: float,
    host_longitude: float,
    radius_meters: float,
    client_latitude: float | None,
    client_longitude: float | None,
    accuracy_meters: float | None,
    location_timestamp: datetime | None,
    now: datetime,
    max_accuracy_meters: float = 200.0,
    max_age_seconds: int = 300,
) -> GeofenceResult:
    values = (host_latitude, host_longitude, radius_meters, max_accuracy_meters)
    if not all(isfinite(value) for value in values):
        return GeofenceResult("UNVERIFIED", reason="Invalid geofence configuration")
    if client_latitude is None or client_longitude is None or accuracy_meters is None or location_timestamp is None:
        return GeofenceResult("UNVERIFIED", reason="Location evidence is incomplete")
    if not all(isfinite(value) for value in (client_latitude, client_longitude, accuracy_meters)):
        return GeofenceResult("UNVERIFIED", reason="Location evidence is invalid")
    if not (-90 <= host_latitude <= 90 and -180 <= host_longitude <= 180):
        return GeofenceResult("UNVERIFIED", reason="Host location is invalid")
    if not (-90 <= client_latitude <= 90 and -180 <= client_longitude <= 180):
        return GeofenceResult("UNVERIFIED", reason="Client location is invalid")
    if radius_meters <= 0 or accuracy_meters < 0 or accuracy_meters > max_accuracy_meters:
        return GeofenceResult("UNVERIFIED", reason="Location accuracy is insufficient")

    normalized_timestamp = location_timestamp.astimezone(timezone.utc) if location_timestamp.tzinfo else location_timestamp.replace(tzinfo=timezone.utc)
    normalized_now = now.astimezone(timezone.utc) if now.tzinfo else now.replace(tzinfo=timezone.utc)
    age = normalized_now - normalized_timestamp
    if age < timedelta(seconds=-30) or age > timedelta(seconds=max_age_seconds):
        return GeofenceResult("UNVERIFIED", reason="Location evidence is stale")

    distance = haversine_meters(host_latitude, host_longitude, client_latitude, client_longitude)
    if distance - accuracy_meters > radius_meters:
        return GeofenceResult("OUT_OF_RANGE", distance, "Client is outside the allowed radius")
    if distance + accuracy_meters > radius_meters:
        return GeofenceResult("UNVERIFIED", distance, "Location accuracy cannot prove the client is inside the radius")
    return GeofenceResult("VERIFIED", distance)


def haversine_meters(latitude_a: float, longitude_a: float, latitude_b: float, longitude_b: float) -> float:
    earth_radius_meters = 6_371_000.0
    delta_latitude = radians(latitude_b - latitude_a)
    delta_longitude = radians(longitude_b - longitude_a)
    a = sin(delta_latitude / 2) ** 2 + cos(radians(latitude_a)) * cos(radians(latitude_b)) * sin(delta_longitude / 2) ** 2
    return earth_radius_meters * 2 * asin(sqrt(a))
