"""Read-only release gate; never applies migrations or prints connection details."""

import argparse
import os
from pathlib import Path
import re
import subprocess
from urllib.parse import parse_qs, unquote, urlsplit


MIGRATION_ID = re.compile(r"[0-9]{14}_[A-Za-z0-9_]+")
ATTRIBUTE = re.compile(r'\[Migration\s*\(\s*"([^"\r\n]+)"\s*\)\s*\]')
DEFAULT_MIGRATIONS = Path(__file__).resolve().parents[2] / "Fire3D/Fire3D.Infrastructure/Migrations"


class PreflightError(Exception):
    """A safe, credential-free diagnostic suitable for CI logs."""


def candidate_migrations(directory):
    migrations = set()
    for source in Path(directory).glob("*.cs"):
        content = source.read_text(encoding="utf-8-sig")
        attributes = ATTRIBUTE.findall(content)
        if len(attributes) != len(re.findall(r"\[Migration(?:Attribute)?\s*\(", content)):
            raise PreflightError("Unsupported migration attribute syntax; deployment blocked.")
        for migration in attributes:
            if not MIGRATION_ID.fullmatch(migration) or migration in migrations:
                raise PreflightError("Candidate has invalid or duplicate migration attributes.")
            migrations.add(migration)
    if not migrations:
        raise PreflightError("No candidate migration attributes found; deployment blocked.")
    return migrations


def connection_environment(connection):
    if not connection or not connection.strip():
        raise PreflightError("DEPLOY_PREFLIGHT_CONNECTION is required; deployment blocked.")
    try:
        uri = urlsplit(connection)
        query = parse_qs(uri.query, strict_parsing=True)
        values = {
            "PGHOST": unquote(uri.hostname or ""),
            "PGPORT": str(uri.port or 5432),
            "PGDATABASE": unquote(uri.path.removeprefix("/")),
            "PGUSER": unquote(uri.username or ""),
            "PGPASSWORD": unquote(uri.password or ""),
        }
        sslmode = query.get("sslmode", ["require"])
        if (uri.scheme not in {"postgres", "postgresql"} or uri.fragment
                or set(query) - {"sslmode"} or len(sslmode) != 1
                or sslmode[0] not in {"require", "verify-ca", "verify-full"}
                or not all(values.values())
                or any(any(ord(char) < 32 for char in value) for value in values.values())
                or "/" in values["PGDATABASE"] or "/" in values["PGHOST"]):
            raise ValueError()
    except ValueError:
        raise PreflightError("Invalid preflight connection: use a PostgreSQL URI with credentials, database and TLS.") from None
    # Prevent ambient libpq settings and the original DSN leaking to the child.
    env = {key: value for key, value in os.environ.items()
           if not key.startswith("PG") and key != "DEPLOY_PREFLIGHT_CONNECTION"}
    env.update(values)
    env.update(PGSSLMODE=sslmode[0], PGCONNECT_TIMEOUT="10",
               PGOPTIONS="-c default_transaction_read_only=on -c statement_timeout=10000")
    return env


def applied_migrations(connection):
    env = connection_environment(connection)
    # A static query, explicitly read-only, with no migration or DDL commands.
    sql = ('BEGIN READ ONLY; SELECT "MigrationId" FROM public."__EFMigrationsHistory" '
           'ORDER BY "MigrationId"; ROLLBACK;')
    try:
        result = subprocess.run(
            ["psql", "--no-psqlrc", "--no-password", "--quiet", "--tuples-only",
             "--no-align", "--set", "ON_ERROR_STOP=1", "--command", sql],
            env=env, capture_output=True, text=True, timeout=30, check=False)
    except (OSError, subprocess.TimeoutExpired):
        raise PreflightError("Cannot complete migration query (psql unavailable or connection timed out).") from None
    # Never print psql diagnostics: they can contain hostnames, users or secrets.
    if result.returncode:
        raise PreflightError("Migration query failed; check connectivity and history SELECT permission.")
    migrations = set(result.stdout.splitlines())
    if not migrations or any(not MIGRATION_ID.fullmatch(value) for value in migrations):
        raise PreflightError("Migration history is empty or invalid; deployment blocked.")
    return migrations


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("migrations", nargs="?", type=Path, default=DEFAULT_MIGRATIONS)
    args = parser.parse_args(argv)
    try:
        candidate = candidate_migrations(args.migrations)
        applied = applied_migrations(os.environ.get("DEPLOY_PREFLIGHT_CONNECTION", ""))
        missing = sorted(candidate - applied)
        if missing:
            raise PreflightError("Candidate migrations are missing; deployment blocked:\n" + "\n".join(missing))
    except (PreflightError, OSError) as error:
        print(str(error) if isinstance(error, PreflightError) else "Cannot read candidate migration files.")
        return 1
    print(f"Migration preflight passed: all {len(candidate)} candidate migrations are applied.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
