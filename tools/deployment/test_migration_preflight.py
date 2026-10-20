import contextlib
import io
import os
from pathlib import Path
import subprocess
import unittest
from unittest.mock import Mock, patch

import migration_preflight as preflight


MIGRATION = "20261019090000_AddGeometryPull"
CONNECTION = "postgresql://preflight:secret%40value@db.example:5432/fire3d?sslmode=require"


class MigrationPreflightTests(unittest.TestCase):
    def setUp(self):
        self.root = Path("candidate")
        self.sources = [Mock(read_text=Mock(return_value=f'[Migration("{MIGRATION}")]\nclass One {{}}'))]
        glob = patch("migration_preflight.Path.glob", return_value=self.sources)
        glob.start()
        self.addCleanup(glob.stop)

    def test_reads_attribute_ids_in_both_inline_and_designer_files(self):
        other = "20261019120000_DropLegacyConstraints"
        self.sources.append(Mock(read_text=Mock(return_value=f'[DbContext(typeof(Context))][Migration("{other}")]')))
        self.assertEqual(preflight.candidate_migrations(self.root), {MIGRATION, other})

    def test_empty_candidate_fails(self):
        self.sources.clear()
        with self.assertRaises(preflight.PreflightError):
            preflight.candidate_migrations(self.root)

    def test_invalid_or_duplicate_attribute_fails(self):
        for value in ("invalid", MIGRATION):
            with self.subTest(value=value):
                self.sources[:] = self.sources[:1] + [Mock(read_text=Mock(return_value=f'[Migration("{value}")]'))]
                with self.assertRaises(preflight.PreflightError):
                    preflight.candidate_migrations(self.root)

    def test_unsupported_attribute_cannot_silently_omit_a_migration(self):
        self.sources.append(Mock(read_text=Mock(return_value='[Migration(MigrationId)]')))
        with self.assertRaises(preflight.PreflightError):
            preflight.candidate_migrations(self.root)

    def test_connection_is_decoded_into_environment_not_arguments(self):
        with patch.dict(os.environ, {"PGOPTIONS": "unsafe", "DEPLOY_PREFLIGHT_CONNECTION": CONNECTION}):
            env = preflight.connection_environment(CONNECTION)
        self.assertEqual(env["PGPASSWORD"], "secret@value")
        self.assertEqual(env["PGDATABASE"], "fire3d")
        self.assertEqual(env["PGCONNECT_TIMEOUT"], "10")
        self.assertIn("default_transaction_read_only=on", env["PGOPTIONS"])
        self.assertNotIn("DEPLOY_PREFLIGHT_CONNECTION", env)

    def test_missing_invalid_or_unsafe_connection_fails(self):
        for value in ("", "Host=db;Password=secret", "postgresql://db/fire3d",
                      CONNECTION + "&options=unsafe", CONNECTION.replace("require", "disable")):
            with self.subTest(value=value), self.assertRaises(preflight.PreflightError):
                preflight.connection_environment(value)

    @patch("migration_preflight.subprocess.run")
    def test_query_is_read_only_bounded_and_has_no_credentials_in_arguments(self, run):
        run.return_value = subprocess.CompletedProcess([], 0, MIGRATION + "\n", "")
        self.assertEqual(preflight.applied_migrations(CONNECTION), {MIGRATION})
        args, kwargs = run.call_args
        self.assertIn("BEGIN READ ONLY", args[0][-1])
        self.assertIn("ROLLBACK", args[0][-1])
        self.assertNotIn("secret", " ".join(args[0]))
        self.assertEqual(kwargs["timeout"], 30)
        self.assertIn("--no-password", args[0])

    @patch("migration_preflight.subprocess.run")
    def test_database_errors_empty_history_and_timeout_fail_closed(self, run):
        for outcome in (subprocess.CompletedProcess([], 1, "", CONNECTION),
                        subprocess.CompletedProcess([], 0, "", ""),
                        subprocess.CompletedProcess([], 0, "unexpected output", ""),
                        subprocess.TimeoutExpired("psql", 30), FileNotFoundError("psql")):
            with self.subTest(outcome=type(outcome).__name__):
                run.side_effect = outcome if isinstance(outcome, Exception) else None
                run.return_value = outcome
                with self.assertRaises(preflight.PreflightError) as error:
                    preflight.applied_migrations(CONNECTION)
                self.assertNotIn("secret", str(error.exception))
                self.assertNotIn(CONNECTION, str(error.exception))

    @patch("migration_preflight.applied_migrations")
    def test_missing_migration_blocks_release(self, applied):
        applied.return_value = {"20261018090000_Other"}
        output = io.StringIO()
        with patch.dict(os.environ, {"DEPLOY_PREFLIGHT_CONNECTION": CONNECTION}), contextlib.redirect_stdout(output):
            result = preflight.main([str(self.root)])
        self.assertEqual(result, 1)
        self.assertIn(MIGRATION, output.getvalue())
        self.assertNotIn(CONNECTION, output.getvalue())

    @patch("migration_preflight.applied_migrations")
    def test_all_candidate_migrations_applied_allows_release(self, applied):
        applied.return_value = {MIGRATION, "20261020090000_Later"}
        with patch.dict(os.environ, {"DEPLOY_PREFLIGHT_CONNECTION": CONNECTION}), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(preflight.main([str(self.root)]), 0)

    def test_missing_secret_blocks_release_without_connecting(self):
        with patch.dict(os.environ, {}, clear=True), patch("migration_preflight.subprocess.run") as run:
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(preflight.main([str(self.root)]), 1)
            run.assert_not_called()


if __name__ == "__main__":
    unittest.main()
