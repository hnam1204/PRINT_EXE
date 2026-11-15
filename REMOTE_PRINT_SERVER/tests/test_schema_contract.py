from pathlib import Path

from app.database import Base


def test_migration_covers_all_sqlalchemy_tables_and_columns():
    migration_dir = Path(__file__).parents[1] / "migrations"
    migration = "\n".join(path.read_text(encoding="utf-8") for path in sorted(migration_dir.glob("*.sql"))).lower()
    for table in Base.metadata.sorted_tables:
        assert f"create table if not exists {table.name}" in migration
        for column in table.columns:
            assert column.name in migration
