"""Focused regression tests for smoke.py process deadlines and cleanup."""
import os
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path

SMOKE = Path(__file__).with_name("smoke.py")


@unittest.skipIf(os.name == "nt", "process-group cleanup check is POSIX-specific")
class SmokeProcessTests(unittest.TestCase):
    def test_unresponsive_worker_times_out_and_is_reaped(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            temporary_path = Path(temporary_directory)
            bin_directory = temporary_path / "bin"
            bin_directory.mkdir()
            pid_file = temporary_path / "worker.pid"
            fake_dotnet = bin_directory / "dotnet"
            fake_dotnet.write_text(
                "#!" + sys.executable + "\n"
                "import os, time\n"
                f"open({str(pid_file)!r}, 'w').write(str(os.getpid()))\n"
                "time.sleep(30)\n",
                encoding="utf-8",
            )
            fake_dotnet.chmod(0o755)
            environment = os.environ.copy()
            environment["PATH"] = str(bin_directory) + os.pathsep + environment.get("PATH", "")

            completed = subprocess.run(
                [sys.executable, str(SMOKE), "--timeout-seconds", "3"],
                cwd=SMOKE.parents[2],
                env=environment,
                text=True,
                capture_output=True,
                timeout=7,
                check=False,
            )

            self.assertNotEqual(completed.returncode, 0, completed.stdout)
            self.assertIn("Worker exceeded the 3s process deadline", completed.stderr)
            self.assertTrue(pid_file.exists(), completed.stderr + completed.stdout)
            worker_pid = int(pid_file.read_text(encoding="utf-8"))
            for _ in range(20):
                try:
                    os.kill(worker_pid, 0)
                except ProcessLookupError:
                    break
                time.sleep(0.025)
            else:
                self.fail(f"Worker process {worker_pid} remained after smoke.py timed out")

    def test_invalid_json_fails_promptly_and_reaps_worker(self):
        with tempfile.TemporaryDirectory() as temporary_directory:
            temporary_path = Path(temporary_directory)
            bin_directory = temporary_path / "bin"
            bin_directory.mkdir()
            pid_file = temporary_path / "worker.pid"
            fake_dotnet = bin_directory / "dotnet"
            fake_dotnet.write_text(
                "#!" + sys.executable + "\n"
                "import os, time\n"
                f"open({str(pid_file)!r}, 'w').write(str(os.getpid()))\n"
                "print('{invalid-json', flush=True)\n"
                "time.sleep(30)\n",
                encoding="utf-8",
            )
            fake_dotnet.chmod(0o755)
            environment = os.environ.copy()
            environment["PATH"] = str(bin_directory) + os.pathsep + environment.get("PATH", "")
            started = time.monotonic()

            completed = subprocess.run(
                [sys.executable, str(SMOKE), "--timeout-seconds", "15"],
                cwd=SMOKE.parents[2],
                env=environment,
                text=True,
                capture_output=True,
                timeout=4,
                check=False,
            )

            elapsed = time.monotonic() - started
            self.assertNotEqual(completed.returncode, 0, completed.stdout)
            self.assertIn("Expecting property name enclosed in double quotes", completed.stderr)
            self.assertLess(elapsed, 3, completed.stderr + completed.stdout)
            self.assertTrue(pid_file.exists(), completed.stderr + completed.stdout)
            worker_pid = int(pid_file.read_text(encoding="utf-8"))
            for _ in range(20):
                try:
                    os.kill(worker_pid, 0)
                except ProcessLookupError:
                    break
                time.sleep(0.025)
            else:
                self.fail(f"Worker process {worker_pid} remained after smoke.py rejected invalid JSON")


if __name__ == "__main__":
    unittest.main()
