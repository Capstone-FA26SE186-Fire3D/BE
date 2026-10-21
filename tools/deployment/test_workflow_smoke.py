"""Execute the workflow's real smoke shell with a fake curl; no network access."""

import os
from pathlib import Path
import shutil
import subprocess
import textwrap
import unittest


class WorkflowSmokeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        if os.name == "nt":
            cls.bash = str(Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "Git/bin/bash.exe")
            if not Path(cls.bash).exists():
                raise unittest.SkipTest("Git Bash required on Windows")
        else:
            cls.bash = shutil.which("bash")
            if not cls.bash:
                raise unittest.SkipTest("bash required")
        workflow = (Path(__file__).resolve().parents[2] / ".github/workflows/azure-deploy.yml").read_text()
        step = workflow.split("      - name: Smoke test deployed API\n", 1)[1]
        cls.script = textwrap.dedent(step.split("        run: |\n", 1)[1])

    def run_smoke(self, status, exit_code=0, failing_path=""):
        env = dict(os.environ, APP_URL="https://test.invalid", MOCK_STATUS=str(status),
                   MOCK_EXIT=str(exit_code), FAILING_PATH=failing_path)
        fake_curl = '''
curl() {
  local url="${@: -1}"
  if [[ -z "$FAILING_PATH" || "$url" == *"$FAILING_PATH" ]]; then
    printf '%s' "$MOCK_STATUS"
    return "$MOCK_EXIT"
  fi
  printf '200'
}
cat() { :; }
'''
        return subprocess.run([self.bash, "-e", "-c", fake_curl + self.script],
                              env=env, capture_output=True, text=True, timeout=10)

    def test_success_requires_200_for_every_endpoint(self):
        self.assertEqual(self.run_smoke(200).returncode, 0)
        for path in ("/health/ready", "/api/learn/situations", "/api/learn/posts", "/openapi/v1.json"):
            for status in (302, 401, 404, 500, "000"):
                with self.subTest(path=path, status=status):
                    self.assertNotEqual(self.run_smoke(status, failing_path=path).returncode, 0)

    def test_timeout_after_http_200_still_fails(self):
        for path in ("/health/ready", "/api/learn/situations"):
            with self.subTest(path=path):
                self.assertNotEqual(self.run_smoke(200, exit_code=28, failing_path=path).returncode, 0)


if __name__ == "__main__":
    unittest.main()
