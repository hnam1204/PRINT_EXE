from dataclasses import dataclass
import os
from urllib.parse import urlparse


class ConfigurationError(RuntimeError):
    pass


def _csv(value: str) -> tuple[str, ...]:
    return tuple(item.strip() for item in value.split(",") if item.strip())


@dataclass(frozen=True)
class Settings:
    app_name: str
    api_version: str
    environment: str
    database_url: str
    enrollment_secret: str
    admin_api_key: str
    cron_secret: str
    allowed_origins: tuple[str, ...]
    pairing_ttl_minutes: int
    max_pairing_attempts: int
    heartbeat_timeout_seconds: int
    rate_limit_window_seconds: int
    rate_limit_max_attempts: int
    max_document_bytes: int
    max_clients_per_host: int
    geofence_max_accuracy_meters: float
    geofence_max_age_seconds: int
    blob_store_id: str
    blob_read_write_token: str

    @classmethod
    def from_env(cls) -> "Settings":
        environment = os.getenv("RPM_SERVER_ENV", "development").lower()
        database_url = os.getenv("DATABASE_URL", "sqlite:///./remote_print_server.db")
        origins = _csv(os.getenv("ALLOWED_ORIGINS", "http://localhost:3000"))
        enrollment_secret = os.getenv("ENROLLMENT_SECRET", "")
        admin_api_key = os.getenv("ADMIN_API_KEY", "")
        cron_secret = os.getenv("CRON_SECRET", "")
        blob_store_id = os.getenv("BLOB_STORE_ID", "")
        blob_read_write_token = os.getenv("BLOB_READ_WRITE_TOKEN", os.getenv("VERCEL_BLOB_READ_WRITE_TOKEN", ""))

        if environment in {"production", "preview"}:
            if not enrollment_secret or not admin_api_key or not cron_secret:
                raise ConfigurationError("ENROLLMENT_SECRET, ADMIN_API_KEY and CRON_SECRET are required outside development.")
            if not origins or "*" in origins:
                raise ConfigurationError("ALLOWED_ORIGINS must explicitly list HTTPS origins.")
            if any(not origin.startswith("https://") for origin in origins):
                raise ConfigurationError("Production CORS origins must use HTTPS.")
            if database_url.startswith("sqlite"):
                raise ConfigurationError("Production requires PostgreSQL persistence.")
            if not blob_store_id and not blob_read_write_token:
                raise ConfigurationError("Production requires BLOB_STORE_ID or BLOB_READ_WRITE_TOKEN for document delivery.")

        parsed = urlparse(database_url)
        if parsed.scheme not in {"sqlite", "postgresql", "postgres", "postgresql+psycopg"}:
            raise ConfigurationError("DATABASE_URL must be SQLite for local development or PostgreSQL for deployment.")

        return cls(
            app_name="Remote Print Server",
            api_version="v1",
            environment=environment,
            database_url=database_url,
            enrollment_secret=enrollment_secret,
            admin_api_key=admin_api_key,
            cron_secret=cron_secret,
            allowed_origins=origins,
            pairing_ttl_minutes=int(os.getenv("PAIRING_TTL_MINUTES", "10")),
            max_pairing_attempts=int(os.getenv("MAX_PAIRING_ATTEMPTS", "5")),
            heartbeat_timeout_seconds=int(os.getenv("HEARTBEAT_TIMEOUT_SECONDS", "120")),
            rate_limit_window_seconds=int(os.getenv("RATE_LIMIT_WINDOW_SECONDS", "900")),
            rate_limit_max_attempts=int(os.getenv("RATE_LIMIT_MAX_ATTEMPTS", "10")),
            max_document_bytes=int(os.getenv("MAX_DOCUMENT_BYTES", str(4 * 1024 * 1024))),
            max_clients_per_host=int(os.getenv("MAX_CLIENTS_PER_HOST", "15")),
            geofence_max_accuracy_meters=float(os.getenv("GEOFENCE_MAX_ACCURACY_METERS", "200")),
            geofence_max_age_seconds=int(os.getenv("GEOFENCE_MAX_AGE_SECONDS", "300")),
            blob_store_id=blob_store_id,
            blob_read_write_token=blob_read_write_token,
        )
