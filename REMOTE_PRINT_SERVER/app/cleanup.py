from datetime import datetime, timedelta, timezone
import threading
import uuid

from sqlalchemy import delete, func, or_, select, text

from .database import (
    AuditEvent,
    CleanupRun,
    Pairing,
    PairingCode,
    PrintAuthorization,
    PrintJob,
    PrintJobTombstone,
    RateLimitEvent,
    RealtimeEvent,
    TelegramMessage,
)

CLEANUP_AFTER_HOURS = 72
BATCH_SIZE = 250
DEFAULT_REALTIME_EVENT_RETENTION_SECONDS = 7 * 24 * 60 * 60
DEFAULT_PAIRING_CODE_RETENTION_SECONDS = CLEANUP_AFTER_HOURS * 60 * 60
DEFAULT_TELEGRAM_MESSAGE_RETENTION_SECONDS = 30 * 24 * 60 * 60
DEFAULT_PRINT_JOB_TOMBSTONE_RETENTION_SECONDS = 30 * 24 * 60 * 60
_sqlite_cleanup_lock = threading.Lock()
TERMINAL_JOB_STATUSES = {"COMPLETED", "CANCELED", "CANCELLED", "FAILED"}


def _utc(value: datetime) -> datetime:
    return value.replace(tzinfo=timezone.utc) if value.tzinfo is None else value.astimezone(timezone.utc)


def _delete_ids(session, model, ids: list[str]) -> int:
    if not ids:
        return 0
    return session.execute(delete(model).where(model.id.in_(ids))).rowcount or 0


def _delete_in_batches(session, model, condition, now: datetime) -> tuple[int, int]:
    deleted = 0
    inspected = 0
    while True:
        ids = list(session.scalars(select(model.id).where(condition).limit(BATCH_SIZE)))
        inspected += len(ids)
        if not ids:
            break
        deleted += _delete_ids(session, model, ids)
        session.flush()
    return deleted, inspected


def _try_lock(session) -> bool:
    if session.bind.dialect.name == "postgresql":
        return bool(session.scalar(text("SELECT pg_try_advisory_xact_lock(hashtext('remote-print-server-cleanup'))")))
    return _sqlite_cleanup_lock.acquire(blocking=False)


def _unlock(session) -> None:
    if session.bind.dialect.name != "postgresql" and _sqlite_cleanup_lock.locked():
        _sqlite_cleanup_lock.release()


def run_cleanup(
    session_factory,
    engine,
    blob_store=None,
    now: datetime | None = None,
    dry_run: bool = False,
    *,
    realtime_event_retention_seconds: int = DEFAULT_REALTIME_EVENT_RETENTION_SECONDS,
    pairing_code_retention_seconds: int = DEFAULT_PAIRING_CODE_RETENTION_SECONDS,
    telegram_message_retention_seconds: int = DEFAULT_TELEGRAM_MESSAGE_RETENTION_SECONDS,
    print_job_tombstone_retention_seconds: int = DEFAULT_PRINT_JOB_TOMBSTONE_RETENTION_SECONDS,
) -> dict:
    started_at = now or datetime.now(timezone.utc)
    cutoff = started_at - timedelta(hours=CLEANUP_AFTER_HOURS)
    realtime_event_cutoff = started_at - timedelta(seconds=realtime_event_retention_seconds)
    pairing_code_cutoff = started_at - timedelta(seconds=pairing_code_retention_seconds)
    telegram_message_cutoff = started_at - timedelta(seconds=telegram_message_retention_seconds)
    tombstone_cutoff = started_at - timedelta(seconds=print_job_tombstone_retention_seconds)
    result = {
        "status": "SUCCEEDED",
        "dryRun": dry_run,
        "startedAt": started_at.isoformat(),
        "finishedAt": None,
        "cutoff": cutoff.isoformat(),
        "inspected": {
            "pairings": 0,
            "printJobs": 0,
            "printAuthorizations": 0,
            "auditEvents": 0,
            "rateLimitEvents": 0,
            "pairingCodes": 0,
            "realtimeEvents": 0,
            "telegramMessages": 0,
            "printJobTombstones": 0,
        },
        "deleted": {
            "pairings": 0,
            "printJobs": 0,
            "printAuthorizations": 0,
            "auditEvents": 0,
            "rateLimitEvents": 0,
            "pairingCodes": 0,
            "realtimeEvents": 0,
            "telegramMessages": 0,
            "printJobTombstones": 0,
        },
        "retained": {"activePairings": 0, "unfinishedPrintJobs": 0, "blobFailures": 0},
        "errors": [],
        "errorCode": None,
    }
    session = session_factory()
    locked = False
    try:
        locked = _try_lock(session)
        if not locked:
            result["status"] = "SKIPPED_ALREADY_RUNNING"
            return result

        pairing_condition = or_(
            Pairing.status.in_({"REJECTED", "EXPIRED"}) & (Pairing.expires_at <= cutoff),
            (Pairing.status == "REVOKED")
            & Pairing.decided_at.is_not(None)
            & (Pairing.decided_at <= cutoff),
        )
        if dry_run:
            pairing_deleted = 0
            pairing_inspected = session.scalar(select(func.count(Pairing.id)).where(pairing_condition)) or 0
        else:
            pairing_deleted, pairing_inspected = _delete_in_batches(session, Pairing, pairing_condition, started_at)
        authorization_condition = (PrintAuthorization.expires_at <= started_at) | (
            PrintAuthorization.used_at.is_not(None) & (PrintAuthorization.used_at <= cutoff)
        )
        if dry_run:
            authorization_deleted = 0
            authorization_inspected = session.scalar(select(func.count(PrintAuthorization.id)).where(authorization_condition)) or 0
        else:
            authorization_deleted, authorization_inspected = _delete_in_batches(session, PrintAuthorization, authorization_condition, started_at)
        pairing_code_condition = or_(
            PairingCode.expires_at <= pairing_code_cutoff,
            PairingCode.revoked_at.is_not(None) & (PairingCode.revoked_at <= pairing_code_cutoff),
        )
        realtime_event_condition = RealtimeEvent.created_at <= realtime_event_cutoff
        telegram_message_condition = TelegramMessage.created_at <= telegram_message_cutoff
        tombstone_condition = or_(
            PrintJobTombstone.expires_at <= started_at,
            PrintJobTombstone.expires_at.is_(None) & (PrintJobTombstone.created_at <= tombstone_cutoff),
        )
        if dry_run:
            pairing_code_deleted = realtime_event_deleted = telegram_message_deleted = tombstone_deleted = 0
            pairing_code_inspected = session.scalar(select(func.count(PairingCode.id)).where(pairing_code_condition)) or 0
            realtime_event_inspected = session.scalar(select(func.count(RealtimeEvent.id)).where(realtime_event_condition)) or 0
            telegram_message_inspected = session.scalar(select(func.count(TelegramMessage.id)).where(telegram_message_condition)) or 0
            tombstone_inspected = session.scalar(select(func.count(PrintJobTombstone.id)).where(tombstone_condition)) or 0
        else:
            pairing_code_deleted, pairing_code_inspected = _delete_in_batches(session, PairingCode, pairing_code_condition, started_at)
            realtime_event_deleted, realtime_event_inspected = _delete_in_batches(session, RealtimeEvent, realtime_event_condition, started_at)
            telegram_message_deleted, telegram_message_inspected = _delete_in_batches(session, TelegramMessage, telegram_message_condition, started_at)
            tombstone_deleted, tombstone_inspected = _delete_in_batches(session, PrintJobTombstone, tombstone_condition, started_at)
        job_deleted = 0
        job_inspected = 0
        while True:
            candidates = session.execute(
                select(PrintJob.id, PrintJob.data_ref)
                .where(
                    (PrintJob.status.in_(TERMINAL_JOB_STATUSES))
                    & (PrintJob.updated_at <= cutoff)
                    & ((PrintJob.claim_expires_at.is_(None)) | (PrintJob.claim_expires_at <= started_at))
                )
                .limit(BATCH_SIZE)
            ).all()
            job_inspected += len(candidates)
            if not candidates:
                break
            deleted_in_batch = 0
            for job_id, data_ref in candidates:
                if data_ref and not dry_run:
                    if blob_store is None:
                        result["retained"]["blobFailures"] += 1
                        result["errors"].append({"type": "BLOB_STORE_NOT_CONFIGURED", "jobId": job_id})
                        continue
                    try:
                        blob_store.delete(data_ref)
                    except Exception:
                        # Retain the row so a later cleanup can retry the blob deletion.
                        result["retained"]["blobFailures"] += 1
                        result["errors"].append({"type": "BLOB_DELETE_FAILED", "jobId": job_id})
                        continue
                if not dry_run:
                    job = session.get(PrintJob, job_id)
                    if job is None:
                        continue
                    session.add(PrintJobTombstone(
                        idempotency_key=job.idempotency_key,
                        request_hash=job.request_hash,
                        original_job_id=job.id,
                        created_at=started_at,
                        expires_at=started_at + timedelta(seconds=print_job_tombstone_retention_seconds),
                    ))
                    session.flush()
                    session.delete(job)
                if not dry_run:
                    job_deleted += 1
                deleted_in_batch += 1
            session.flush()
            if dry_run:
                break
            if deleted_in_batch == 0:
                break
        audit_condition = AuditEvent.created_at <= cutoff
        rate_condition = RateLimitEvent.created_at <= cutoff
        if dry_run:
            audit_deleted = rate_deleted = 0
            audit_inspected = session.scalar(select(func.count(AuditEvent.id)).where(audit_condition)) or 0
            rate_inspected = session.scalar(select(func.count(RateLimitEvent.id)).where(rate_condition)) or 0
        else:
            audit_deleted, audit_inspected = _delete_in_batches(session, AuditEvent, audit_condition, started_at)
            rate_deleted, rate_inspected = _delete_in_batches(session, RateLimitEvent, rate_condition, started_at)
            session.execute(delete(CleanupRun).where(CleanupRun.finished_at <= started_at - timedelta(days=30)))
        result["inspected"].update(
            pairings=pairing_inspected,
            printJobs=job_inspected,
            printAuthorizations=authorization_inspected,
            auditEvents=audit_inspected,
            rateLimitEvents=rate_inspected,
            pairingCodes=pairing_code_inspected,
            realtimeEvents=realtime_event_inspected,
            telegramMessages=telegram_message_inspected,
            printJobTombstones=tombstone_inspected,
        )
        result["deleted"].update(
            pairings=pairing_deleted,
            printJobs=job_deleted,
            printAuthorizations=authorization_deleted,
            auditEvents=audit_deleted,
            rateLimitEvents=rate_deleted,
            pairingCodes=pairing_code_deleted,
            realtimeEvents=realtime_event_deleted,
            telegramMessages=telegram_message_deleted,
            printJobTombstones=tombstone_deleted,
        )
        result["retained"]["activePairings"] = session.scalar(
            select(func.count(Pairing.id)).where(Pairing.status == "APPROVED")
        ) or 0
        result["retained"]["unfinishedPrintJobs"] = session.scalar(
            select(func.count(PrintJob.id)).where(~PrintJob.status.in_(TERMINAL_JOB_STATUSES))
        ) or 0
        if result["errors"]:
            result["status"] = "PARTIAL_FAILED"
        if not dry_run:
            session.add(CleanupRun(
                id=str(uuid.uuid4()), started_at=started_at, finished_at=datetime.now(timezone.utc),
                status=result["status"], summary=result,
            ))
            session.commit()
        return result
    except Exception:
        session.rollback()
        result["status"] = "FAILED"
        result["errorCode"] = "CLEANUP_DATABASE_ERROR"
        return result
    finally:
        if locked:
            _unlock(session)
        result["finishedAt"] = datetime.now(timezone.utc).isoformat()
        session.close()
