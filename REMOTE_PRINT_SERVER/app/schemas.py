from datetime import datetime
from typing import Literal

from pydantic import BaseModel, Field


class DeviceRegistration(BaseModel):
    deviceId: str = Field(min_length=1, max_length=128, pattern=r"^[A-Za-z0-9._:-]+$")
    name: str = Field(min_length=1, max_length=160)
    role: Literal["HOST", "CLIENT"]
    appVersion: str = Field(min_length=1, max_length=32)


class DeviceRegistrationResponse(BaseModel):
    deviceId: str
    deviceToken: str
    role: str
    expiresAt: datetime | None = None


class DeviceProfile(BaseModel):
    deviceId: str
    name: str
    role: str
    appVersion: str
    isRevoked: bool
    lastSeenAt: datetime


class HeartbeatResponse(BaseModel):
    deviceId: str
    status: Literal["ONLINE"]
    lastSeenAt: datetime


class PairingRequest(BaseModel):
    hostId: str = Field(min_length=1, max_length=128)


class PairingResponse(BaseModel):
    pairingId: str
    hostId: str
    clientId: str
    code: str
    status: str
    expiresAt: datetime


class PairingDecision(BaseModel):
    approve: bool
    allowedPrinters: list[str] = Field(default_factory=list, max_length=100)


class PendingPairing(BaseModel):
    pairingId: str
    clientId: str
    clientName: str
    code: str
    expiresAt: datetime


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


class PrintJobResponse(BaseModel):
    jobId: str
    idempotencyKey: str
    clientId: str
    hostId: str
    printerId: str
    status: str
    metadata: dict


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


class PairingCodeResolveResponse(BaseModel):
    pairingId: str
    hostId: str
    clientId: str
    status: str
