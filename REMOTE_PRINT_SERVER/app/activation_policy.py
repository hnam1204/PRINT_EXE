from typing import Final


GEOFENCE_POLICY_LIMITED: Final[str] = "LIMITED"
GEOFENCE_POLICY_UNLIMITED: Final[str] = "UNLIMITED"
MIN_GEOFENCE_RADIUS_METERS: Final[int] = 100
MAX_GEOFENCE_RADIUS_METERS: Final[int] = 5000


def normalize_geofence_policy(policy: str | None, radius_meters: int | None) -> tuple[str, int | None]:
    normalized = (policy or GEOFENCE_POLICY_LIMITED).strip().upper()
    if normalized == GEOFENCE_POLICY_UNLIMITED:
        return GEOFENCE_POLICY_UNLIMITED, None
    if normalized != GEOFENCE_POLICY_LIMITED:
        raise ValueError("Geofence policy must be LIMITED or UNLIMITED")
    if radius_meters is None or not MIN_GEOFENCE_RADIUS_METERS <= radius_meters <= MAX_GEOFENCE_RADIUS_METERS:
        raise ValueError(
            f"Phạm vi phải từ {MIN_GEOFENCE_RADIUS_METERS} đến {MAX_GEOFENCE_RADIUS_METERS} mét."
        )
    return GEOFENCE_POLICY_LIMITED, int(radius_meters)
