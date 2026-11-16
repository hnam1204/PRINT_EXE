from datetime import datetime
from typing import Literal

from pydantic import BaseModel, Field, FiniteFloat


class DeviceRegistration(BaseModel):
    deviceId: str = Field(min_length=1, max_length=128, pattern=r"^[A-Za-z0-9._:-]+$")
    name: str = Field(min_length=1, max_length=160)
    role: Literal["HOST", "CLIENT"]
    appVersion: str = Field(min_length=1, max_length=32)
    publicKey: str | None = Field(default=None, max_length=8192)
    keyAlgorithm: Literal["RSA-OAEP-SHA256"] = "RSA-OAEP-SHA256"


class DeviceRegistrationResponse(BaseModel):
    deviceId: str
    deviceToken: str
    role: str
    isActivationCode: bool = False
    expiresAt: datetime | None = None
    activationLabel: str | None = None
    activationExpiresAt: datetime | None = None
    activationMaxDevices: int | None = None
    activationUsedDevices: int | None = None
    activationGeofencePolicy: Literal["LIMITED", "UNLIMITED"] | None = None
    activationGeofenceRadiusMeters: int | None = None


class RealtimeSessionResponse(BaseModel):
    sessionToken: str
    gatewayUrl: str
    expiresAt: datetime


class RealtimeEventEnvelope(BaseModel):
    eventId: int
    eventType: str
    recipientDeviceId: str
    schemaVersion: int = 1
    hostId: str | None = None
    clientId: str | None = None
    pairingId: str | None = None
    jobId: str | None = None
    serverTimestamp: datetime
    sequence: int
    cursor: int
    correlationId: str
    payload: dict = Field(default_factory=dict)


class DeviceProfile(BaseModel):
    deviceId: str
    name: str
    role: str
    appVersion: str
    isRevoked: bool
    lastSeenAt: datetime
    publicKey: str | None = None
    keyAlgorithm: str = "RSA-OAEP-SHA256"
    activationLabel: str | None = None
    activationExpiresAt: datetime | None = None
    activationMaxDevices: int | None = None
    activationUsedDevices: int | None = None
    activationGeofencePolicy: Literal["LIMITED", "UNLIMITED"] | None = None
    activationGeofenceRadiusMeters: int | None = None


class HeartbeatResponse(BaseModel):
    deviceId: str
    status: Literal["ONLINE"]
    lastSeenAt: datetime


class PairingRequest(BaseModel):
    hostId: str = Field(min_length=1, max_length=128)
    latitude: float | None = Field(default=None, ge=-90, le=90)
    longitude: float | None = Field(default=None, ge=-180, le=180)
    accuracyMeters: float | None = Field(default=None, ge=0, le=100000)
    locationTimestamp: datetime | None = None


class PairingResponse(BaseModel):
    pairingId: str
    hostId: str
    clientId: str
    code: str
    status: str
    expiresAt: datetime
    locationStatus: str = "UNVERIFIED"


class PairingDecision(BaseModel):
    approve: bool
    allowedPrinters: list[str] = Field(default_factory=list, max_length=100)


class PendingPairing(BaseModel):
    pairingId: str
    clientId: str
    clientName: str
    code: str
    expiresAt: datetime
    locationStatus: str = "UNVERIFIED"
    distanceMeters: float | None = None


class PairingSummary(BaseModel):
    hostId: str
    approvedCount: int
    onlineCount: int
    maxClients: int


class GeofenceSettings(BaseModel):
    enabled: bool = False
    latitude: float | None = Field(default=None, ge=-90, le=90)
    longitude: float | None = Field(default=None, ge=-180, le=180)
    # Keep legacy 1..1000 m activation policies valid while allowing new 5 km policies.
    radiusMeters: FiniteFloat | None = Field(default=1000, gt=0, le=5000)
    policy: Literal["LIMITED", "UNLIMITED", "OPTIONAL"] = "LIMITED"


class GeofenceResponse(GeofenceSettings):
    hostId: str
    maxRadiusMeters: int | None = None


class PrinterRegistration(BaseModel):
    printerId: str = Field(min_length=1, max_length=128, pattern=r"^[A-Za-z0-9._:-]+$")
    displayName: str = Field(min_length=1, max_length=200)
    reportedStatus: str = Field(default="UNKNOWN", min_length=1, max_length=32)
    isShared: bool = False
    metadata: dict = Field(default_factory=dict)


class PrinterResponse(BaseModel):
    printerId: str
    hostId: str
    displayName: str
    reportedStatus: str
    isShared: bool
    metadata: dict


class PrintJobRequest(BaseModel):
    hostId: str = Field(min_length=1, max_length=128)
    printerId: str = Field(min_length=1, max_length=128)
    metadata: dict = Field(default_factory=dict)
    dataRef: str | None = Field(default=None, max_length=512)
    contentSha256: str | None = Field(default=None, min_length=64, max_length=64, pattern=r"^[a-fA-F0-9]{64}$")
    contentSize: int | None = Field(default=None, ge=0, le=52428800)
    contentType: str | None = Field(default=None, max_length=120)
    latitude: float | None = Field(default=None, ge=-90, le=90)
    longitude: float | None = Field(default=None, ge=-180, le=180)
    accuracyMeters: float | None = Field(default=None, ge=0, le=100000)
    locationTimestamp: datetime | None = None


class PrintJobResponse(BaseModel):
    jobId: str
    idempotencyKey: str
    clientId: str
    hostId: str
    printerId: str
    status: str
    metadata: dict
    dataRef: str | None = None
    contentSha256: str | None = None
    contentSize: int | None = None
    contentType: str | None = None
    claimExpiresAt: datetime | None = None
    spoolerJobId: str | None = None
    failureCode: str | None = None
    failureMessage: str | None = None
    completedAt: datetime | None = None


class PrintAuthorizationRequest(BaseModel):
    jobId: str = Field(min_length=1, max_length=200)
    hostId: str = Field(min_length=1, max_length=128)
    printerId: str = Field(min_length=1, max_length=128)
    latitude: float | None = Field(default=None, ge=-90, le=90)
    longitude: float | None = Field(default=None, ge=-180, le=180)
    accuracyMeters: float | None = Field(default=None, ge=0, le=100000)
    locationTimestamp: datetime | None = None


class PrintAuthorizationResponse(BaseModel):
    authorizationId: str
    authorizationToken: str
    jobId: str
    printerId: str
    expiresAt: datetime


class PrintAuthorizationConsumeRequest(BaseModel):
    authorizationToken: str = Field(min_length=20, max_length=256)


class PrintAuthorizationConsumeResponse(BaseModel):
    authorized: bool
    jobId: str
    clientId: str
    hostId: str
    printerId: str


class JobClaimRequest(BaseModel):
    leaseSeconds: int = Field(default=120, ge=30, le=900)


class JobClaimResponse(PrintJobResponse):
    claimToken: str


class JobRenewRequest(BaseModel):
    claimToken: str = Field(min_length=20, max_length=256)
    leaseSeconds: int = Field(default=120, ge=30, le=900)


class JobAcknowledgeRequest(BaseModel):
    claimToken: str = Field(min_length=20, max_length=256)


class JobStatusUpdateRequest(BaseModel):
    claimToken: str = Field(min_length=20, max_length=256)
    status: Literal["DOWNLOADING", "SPOOLING", "SPOOLER_ACCEPTED", "COMPLETED", "FAILED", "UNKNOWN"]
    spoolerJobId: str | None = Field(default=None, max_length=128)
    failureCode: str | None = Field(default=None, max_length=64)
    failureMessage: str | None = Field(default=None, max_length=500)


class JobCancelRequest(BaseModel):
    reason: str | None = Field(default=None, max_length=500)


class PairingCodeIssueRequest(BaseModel):
    sessionId: str = Field(min_length=1, max_length=128, pattern=r"^[A-Za-z0-9._:-]+$")


class PairingCodeResponse(BaseModel):
    sessionId: str
    code: str | None
    issuedAt: datetime
    expiresAt: datetime
    status: Literal["ACTIVE", "EXPIRED", "REVOKED"]


class PairingCodeResolveRequest(BaseModel):
    code: str = Field(min_length=6, max_length=6, pattern=r"^[A-Z0-9]{6}$")
    latitude: float | None = Field(default=None, ge=-90, le=90)
    longitude: float | None = Field(default=None, ge=-180, le=180)
    accuracyMeters: float | None = Field(default=None, ge=0, le=100000)
    locationTimestamp: datetime | None = None


class PairingCodeResolveResponse(BaseModel):
    pairingId: str
    hostId: str
    clientId: str
    status: str
    locationStatus: str = "UNVERIFIED"
