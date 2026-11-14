import os

import pytest

from app.database import make_engine, normalize_database_url


def test_provider_postgres_urls_use_psycopg3_without_connecting():
    assert normalize_database_url("postgres://user:pass@db.example/app") == "postgresql+psycopg://user:pass@db.example/app"
    assert normalize_database_url("postgresql://user:pass@db.example/app") == "postgresql+psycopg://user:pass@db.example/app"
    assert normalize_database_url("postgresql+psycopg://user:pass@db.example/app") == "postgresql+psycopg://user:pass@db.example/app"

    engine = make_engine("postgresql://user:pass@db.example/app")
    assert engine.url.drivername == "postgresql+psycopg"
    engine.dispose()


def test_sqlite_remains_separate_from_postgres_driver():
    engine = make_engine("sqlite:///:memory:")
    assert engine.url.drivername == "sqlite"
    engine.dispose()


@pytest.mark.integration
def test_real_postgres_connection_when_explicitly_configured():
    database_url = os.getenv("RPM_TEST_POSTGRES_URL")
    if not database_url:
        pytest.skip("Set RPM_TEST_POSTGRES_URL to run the opt-in PostgreSQL integration test")
    engine = make_engine(database_url)
    with engine.connect() as connection:
        assert connection.exec_driver_sql("SELECT 1").scalar_one() == 1
    engine.dispose()
