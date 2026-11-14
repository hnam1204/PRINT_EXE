from datetime import datetime, timedelta, timezone
import threading
import uuid

from sqlalchemy import delete, func, select, text

from .database import AuditEvent, CleanupRun, Pairing, PrintJob, RateLimitEvent

CLEANUP_AFTER_HOURS = 72
BATCH_SIZE = 250
_sqlite_cleanup_lock = threading.Lock()
TERMINAL_JOB_STATUSES = {"COMPLETED", "CANCELLED", "FAILED"}
TERMINAL_PAIRING_STATUSES = {"REJECTED", "EXPIRED"}


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


def run_cleanup(session_factory, engine, now: datetime | None = None) -> dict:
    started_at = now or datetime.now(timezone.utc)
    cutoff = started_at - timedelta(hours=CLEANUP_AFTER_HOURS)
    result = {
        "status": "SUCCEEDED",
        "startedAt": started_at.isoformat(),
        "finishedAt": None,
        "cutoff": cutoff.isoformat(),
        "inspected": {"pairings": 0, "printJobs": 0, "auditEvents": 0, "rateLimitEvents": 0},
        "deleted": {"pairings": 0, "printJobs": 0, "auditEvents": 0, "rateLimitEvents": 0},
        "retained": {"activePairings": 0, "unfinishedPrintJobs": 0},
        "errorCode": None,
    }
    session = session_factory()
    locked = False
    try:
        locked = _try_lock(session)
        if not locked:
            result["status"] = "SKIPPED_ALREADY_RUNNING"
            return result

        pairing_deleted, pairing_inspected = _delete_in_batches(
            session,
            Pairing,
            (Pairing.status.in_(TERMINAL_PAIRING_STATUSES)) & (Pairing.expires_at <= cutoff),
            started_at,
        )
        job_deleted, job_inspected = _delete_in_batches(
            session,
            PrintJob,
            (PrintJob.status.in_(TERMINAL_JOB_STATUSES)) & (PrintJob.updated_at <= cutoff),
            started_at,
        )
        audit_deleted, audit_inspected = _delete_in_batches(
            session, AuditEvent, AuditEvent.created_at <= cutoff, started_at
        )
        rate_deleted, rate_inspected = _delete_in_batches(
            session, RateLimitEvent, RateLimitEvent.created_at <= cutoff, started_at
        )
        session.execute(delete(CleanupRun).where(CleanupRun.finished_at <= started_at - timedelta(days=30)))
        result["inspected"].update(
            pairings=pairing_inspected,
            printJobs=job_inspected,
            auditEvents=audit_inspected,
            rateLimitEvents=rate_inspected,
        )
        result["deleted"].update(
            pairings=pairing_deleted,
            printJobs=job_deleted,
            auditEvents=audit_deleted,
            rateLimitEvents=rate_deleted,
        )
        result["retained"]["activePairings"] = session.scalar(
            select(func.count(Pairing.id)).where(Pairing.status == "APPROVED")
        ) or 0
        result["retained"]["unfinishedPrintJobs"] = session.scalar(
            select(func.count(PrintJob.id)).where(~PrintJob.status.in_(TERMINAL_JOB_STATUSES))
        ) or 0
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
