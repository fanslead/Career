"""Focused regression tests for the P0 Python probe and Linux wrapper."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import signal
import sqlite3
import subprocess
import sys
import tempfile
import textwrap
import time
import unittest
from contextlib import closing
from pathlib import Path
from unittest import mock

import validate


SCRIPTS_DIR = Path(__file__).resolve().parent
WRAPPER = SCRIPTS_DIR / "validate-linux-container.sh"


class EvidenceAndAssertionTests(unittest.TestCase):
    def test_empty_persisted_json_text_is_present_but_invalid(self) -> None:
        absent = validate.json_evidence(None)
        empty = validate.json_evidence("")

        self.assertFalse(absent["present"])
        self.assertTrue(empty["present"])
        self.assertFalse(empty["validJson"])
        self.assertEqual(empty["bytes"], 0)
        self.assertEqual(empty["sha256"], hashlib.sha256(b"").hexdigest())

    def test_read_db_evidence_preserves_empty_checkpoint_presence(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            state = Path(temporary)
            with closing(sqlite3.connect(state / "app.db")) as db:
                db.executescript(
                    """
                    CREATE TABLE runs (
                        id TEXT PRIMARY KEY, status TEXT, scenario TEXT, session TEXT,
                        approval TEXT, operation_id TEXT, args_json TEXT, args_hash TEXT,
                        decision TEXT, fault TEXT, error TEXT, cancel_requested INTEGER
                    );
                    CREATE TABLE tool_operations (
                        id TEXT, state TEXT, args_hash TEXT, result TEXT, run_id TEXT
                    );
                    CREATE TABLE events (seq INTEGER, type TEXT, payload TEXT, run_id TEXT);
                    INSERT INTO runs VALUES ('r1', 'Failed', 'invalid-arguments', '', NULL,
                                             NULL, '{}', NULL, NULL, NULL, NULL, 0);
                    """
                )

            evidence = validate.read_db_evidence(state, "r1")
            self.assertTrue(evidence["run"]["session"]["present"])
            self.assertFalse(evidence["run"]["session"]["validJson"])
            self.assertFalse(evidence["run"]["approval"]["present"])
            self.assertTrue(evidence["run"]["argumentsJson"]["present"])
            self.assertEqual(evidence["run"]["arguments"], {})

            with closing(sqlite3.connect(state / "app.db")) as db:
                db.execute("UPDATE runs SET args_json='' WHERE id='r1'")
                db.commit()
            with self.assertRaises(validate.ProbeFailure):
                validate.read_db_evidence(state, "r1")

    def test_sse_reconnect_requires_nonempty_fresh_expected_event(self) -> None:
        events = [{"seq": 12, "type": "ApprovalGranted"}, {"seq": 13, "type": "RunResumed"}]
        validate.assert_sse_reconnect(events, 11, "ApprovalGranted")
        with self.assertRaises(validate.ProbeFailure):
            validate.assert_sse_reconnect([], 11, "ApprovalGranted")
        with self.assertRaises(validate.ProbeFailure):
            validate.assert_sse_reconnect([{"seq": 11, "type": "ApprovalGranted"}], 11, "ApprovalGranted")
        with self.assertRaises(validate.ProbeFailure):
            validate.assert_sse_reconnect([{"seq": 12, "type": "RunResumed"}], 11, "ApprovalGranted")

    def test_denial_assertion_requires_persisted_terminal_denial_without_side_effect(self) -> None:
        run = {"status": "Denied", "receiptCount": 0, "workerPid": 202, "modelCalls": 2}
        before = {"workerPid": 101, "modelCalls": 1}
        evidence = {
            "run": {"status": "Denied"},
            "ledger": None,
            "events": [{"type": "ApprovalDenied"}, {"type": "RunDenied"}],
            "receiptArtifacts": {"receipts": [], "staging": [], "quarantine": []},
        }
        validate.assert_denied(run, before=before, evidence=evidence)

        invalid = dict(run, modelCalls=1)
        with self.assertRaises(validate.ProbeFailure):
            validate.assert_denied(invalid, before=before, evidence=evidence)

    def test_suite_summary_rejects_empty_or_inconsistent_registry(self) -> None:
        args = argparse.Namespace(execution_id="e1", host=Path("host"), worker=Path("worker.dll"), dotnet="dotnet")
        one_case = [{"name": "normal", "ok": True}]
        summary = validate.build_suite_summary(args, one_case, ["normal"], "started")
        self.assertIsNone(summary["suiteError"])
        self.assertEqual(summary["caseNames"], ["normal"])
        self.assertEqual(summary["caseCount"], 1)
        self.assertEqual((summary["passed"], summary["failed"]), (1, 0))

        empty_summary = validate.build_suite_summary(args, [], [], "started")
        self.assertEqual(empty_summary["caseCount"], 0)
        self.assertTrue(empty_summary["suiteError"])

        mismatch = validate.build_suite_summary(args, [{"name": "other", "ok": 1}], ["normal"], "started")
        self.assertTrue(mismatch["suiteError"])


@unittest.skipIf(os.name == "nt", "Unix process-group cleanup regression")
class HostCleanupRetryTests(unittest.TestCase):
    def test_close_retries_persistent_worker_inspection_failure(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            state = root / "state"
            state.mkdir()
            pid_file = root / "worker.pid"
            parent_code = textwrap.dedent(
                """
                import os, signal, subprocess, sys, time
                pid_file = sys.argv[1]
                worker = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(60)'])
                with open(pid_file, 'w', encoding='ascii') as stream:
                    stream.write(str(worker.pid))
                def stop(signum, frame):
                    try:
                        os.kill(worker.pid, signal.SIGTERM)
                    except ProcessLookupError:
                        pass
                    try:
                        worker.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        worker.kill()
                        worker.wait(timeout=5)
                    raise SystemExit(0)
                signal.signal(signal.SIGTERM, stop)
                while True:
                    time.sleep(1)
                """
            )
            process = subprocess.Popen(
                [sys.executable, "-c", parent_code, str(pid_file)],
                start_new_session=True,
                stdin=subprocess.DEVNULL,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
            )
            try:
                deadline = time.monotonic() + 5
                while not pid_file.exists() and time.monotonic() < deadline:
                    time.sleep(0.01)
                self.assertTrue(pid_file.exists(), "parent did not report its child PID")
                worker_pid = int(pid_file.read_text(encoding="ascii"))
                with closing(sqlite3.connect(state / "app.db")) as db:
                    db.execute("CREATE TABLE runs (worker_pid INTEGER)")
                    db.execute("INSERT INTO runs VALUES (?)", (worker_pid,))
                    db.commit()

                supervisor = validate.HostSupervisor(Path("host"), Path("worker.dll"), "dotnet", state, root / "logs")
                supervisor.processes.append((process, process.pid))
                metadata = {
                    "pid": process.pid,
                    "terminationRequested": None,
                    "returnCode": None,
                    "ownedWorkerPids": [],
                    "ownedWorkerPidsAfterCleanup": None,
                }
                supervisor.process_metadata.append(metadata)

                original_inspect = supervisor._worker_pids_owned_by
                inspection_calls = 0

                def fail_then_recover(host_process: subprocess.Popen[bytes], pgid: int) -> list[int]:
                    nonlocal inspection_calls
                    inspection_calls += 1
                    if inspection_calls <= 2:
                        raise validate.ProbeFailure("simulated SQLite ownership inspection failure")
                    return original_inspect(host_process, pgid)

                with mock.patch.object(supervisor, "_worker_pids_owned_by", side_effect=fail_then_recover):
                    with self.assertRaises(validate.ProbeFailure):
                        supervisor.kill(process, force=False)
                    self.assertIsNotNone(process.poll())
                    self.assertNotIn("cleanupCompletedUtc", metadata)
                    self.assertIn("cleanupError", metadata)
                    supervisor.close()

                self.assertGreaterEqual(inspection_calls, 3)
                self.assertIn("cleanupCompletedUtc", metadata)
                self.assertNotIn("cleanupError", metadata)
                deadline = time.monotonic() + 2
                while validate.HostSupervisor._pid_exists(worker_pid) and time.monotonic() < deadline:
                    time.sleep(0.02)
                self.assertFalse(validate.HostSupervisor._pid_exists(worker_pid), "owned Worker child remained alive")
            finally:
                if process.poll() is None:
                    process.kill()
                    process.wait(timeout=5)


@unittest.skipIf(os.name == "nt", "Bash wrapper regression")
class FakeDockerWrapperTests(unittest.TestCase):
    def _run_wrapper(self, temporary: str, *, daemon_failure: bool = False, invalid_summary: bool = False, missing_context: bool = False) -> tuple[subprocess.CompletedProcess[str], Path, dict]:
        root = Path(temporary)
        fake_bin = root / "bin"
        fake_bin.mkdir()
        state_path = root / "docker-state.json"
        docker_path = fake_bin / "docker"
        docker_path.write_text(
            "#!/usr/bin/env python3\n" + textwrap.dedent(
                r"""
                import fcntl, hashlib, json, os, pathlib, sys
                args = sys.argv[1:]
                state_path = pathlib.Path(os.environ['FAKE_DOCKER_STATE'])
                lock = open(str(state_path) + '.lock', 'a+')
                fcntl.flock(lock.fileno(), fcntl.LOCK_EX)
                state = json.loads(state_path.read_text()) if state_path.exists() else {'containers': {}, 'volumes': {}, 'logsSeen': False, 'testRemoved': False}
                def save():
                    temporary_path = pathlib.Path(str(state_path) + '.tmp')
                    temporary_path.write_text(json.dumps(state))
                    temporary_path.replace(state_path)
                def missing(kind, name):
                    label = 'container' if kind == 'container' else 'volume'
                    print(f'Error response from daemon: No such {label}: {name}', file=sys.stderr)
                    raise SystemExit(1)
                if args[:1] == ['version']:
                    print('27.1.0')
                elif args[:1] == ['build']:
                    pass
                elif args[:2] == ['image', 'inspect']:
                    fmt = args[args.index('--format') + 1]
                    print('sha256:fake-image' if fmt == '{{.Id}}' else '{"Id":"sha256:fake-image","Os":"linux","Architecture":"amd64","Entrypoint":[],"Cmd":[]}')
                elif args[:1] == ['run']:
                    print('fake runtime environment')
                elif args[:2] == ['volume', 'inspect']:
                    name = args[-1]
                    if name not in state['volumes']:
                        missing('volume', name)
                    if os.environ.get('FAKE_DOCKER_DAEMON_FAILURE') == '1' and state.get('logsSeen') and state.get('testRemoved'):
                        print('docker context p0 not found' if os.environ.get('FAKE_DOCKER_MISSING_CONTEXT') == '1' else 'Error response from daemon: Cannot connect to the Docker daemon', file=sys.stderr)
                        raise SystemExit(1)
                    if '--format' in args:
                        print(state['volumes'][name]['runId'])
                elif args[:2] == ['container', 'inspect']:
                    name = args[-1]
                    if name not in state['containers']:
                        missing('container', name)
                    if '--format' in args:
                        print(state['containers'][name]['runId'])
                elif args[:2] == ['volume', 'create']:
                    name = args[args.index('--name') + 1]
                    run_id = next(arg.split('=', 1)[1] for arg in args if arg.startswith('com.example.career-p0.run-id='))
                    state['volumes'][name] = {'runId': run_id}
                    save()
                    print(name)
                elif args[:1] == ['create']:
                    name = args[args.index('--name') + 1]
                    run_id = next(arg.split('=', 1)[1] for arg in args if arg.startswith('com.example.career-p0.run-id='))
                    state['containers'][name] = {'runId': run_id}
                    save()
                    print('container-id-' + name)
                elif args[:1] == ['start']:
                    pass
                elif args[:1] == ['wait']:
                    print('0')
                elif args[:1] == ['logs']:
                    state['logsSeen'] = True
                    save()
                    print('fake validation console')
                elif args[:1] == ['cp']:
                    destination = pathlib.Path(args[-1])
                    locks = destination / 'lockfiles'
                    validation = destination / 'validation'
                    locks.mkdir(parents=True, exist_ok=True)
                    validation.mkdir(parents=True, exist_ok=True)
                    content = b'locked input\n'
                    (locks / 'checks.txt').write_bytes(content)
                    digest = hashlib.sha256(content).hexdigest()
                    (locks / 'SHA256SUMS').write_text(f'{digest}  checks.txt\n')
                    if os.environ.get('FAKE_DOCKER_INVALID_SUMMARY') == '1':
                        summary = {'caseNames': [], 'caseCount': 0, 'cases': [], 'passed': 0, 'failed': 0, 'suiteError': 'empty'}
                    else:
                        summary = {'caseNames': ['normal'], 'caseCount': 1, 'cases': [{'name': 'normal', 'ok': True}], 'passed': 1, 'failed': 0, 'suiteError': None}
                    (validation / 'summary.json').write_text(json.dumps(summary))
                elif args[:2] == ['rm', '-f'] or args[:1] == ['rm']:
                    name = args[-1]
                    state['containers'].pop(name, None)
                    if name.endswith('-validation'):
                        state['testRemoved'] = True
                    save()
                elif args[:2] == ['volume', 'rm']:
                    state['volumes'].pop(args[-1], None)
                    save()
                else:
                    print('unsupported fake docker command: ' + repr(args), file=sys.stderr)
                    raise SystemExit(98)
                save()
                """
            ),
            encoding="utf-8",
        )
        docker_path.chmod(0o755)
        results = root / "results"
        env = os.environ.copy()
        env.update(
            {
                "PATH": f"{fake_bin}{os.pathsep}{env.get('PATH', '')}",
                "FAKE_DOCKER_STATE": str(state_path),
                "FAKE_DOCKER_DAEMON_FAILURE": "1" if daemon_failure else "0",
                "FAKE_DOCKER_INVALID_SUMMARY": "1" if invalid_summary else "0",
                "FAKE_DOCKER_MISSING_CONTEXT": "1" if missing_context else "0",
            }
        )
        completed = subprocess.run(
            ["/bin/bash", str(WRAPPER), str(results), "--platform", "linux/amd64", "--rid", "linux-x64"],
            env=env,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            timeout=30,
            check=False,
        )
        state = json.loads(state_path.read_text(encoding="utf-8")) if state_path.exists() else {}
        return completed, results, state

    def test_successful_wrapper_validates_summary_and_cleans_resources(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            completed, results, state = self._run_wrapper(temporary)
            self.assertEqual(completed.returncode, 0, completed.stderr + completed.stdout)
            self.assertFalse(state["containers"])
            self.assertFalse(state["volumes"])
            cleanup_log = (results / "resource-cleanup.log").read_text(encoding="utf-8")
            self.assertIn("removed validation container", cleanup_log)
            self.assertIn("removed results volume", cleanup_log)

    def test_unknown_docker_inspect_failure_fails_and_preserves_volume(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            completed, results, state = self._run_wrapper(temporary, daemon_failure=True)
            self.assertEqual(completed.returncode, 1, completed.stderr + completed.stdout)
            self.assertFalse(state["containers"])
            self.assertEqual(len(state["volumes"]), 1)
            cleanup_log = (results / "resource-cleanup.log").read_text(encoding="utf-8")
            self.assertIn("Cannot connect to the Docker daemon", cleanup_log)

    def test_invalid_exported_summary_fails_and_preserves_volume(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            completed, results, state = self._run_wrapper(temporary, invalid_summary=True)
            self.assertEqual(completed.returncode, 1, completed.stderr + completed.stdout)
            self.assertFalse(state["containers"])
            self.assertEqual(len(state["volumes"]), 1)
            self.assertIn("preserving owned volume", completed.stderr)
            cleanup_log = (results / "resource-cleanup.log").read_text(encoding="utf-8")
            self.assertIn("preserved results volume", cleanup_log)

    def test_missing_docker_context_is_not_treated_as_missing_volume(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            completed, results, state = self._run_wrapper(temporary, daemon_failure=True, missing_context=True)
            self.assertEqual(completed.returncode, 1, completed.stderr + completed.stdout)
            self.assertEqual(len(state["volumes"]), 1)
            self.assertIn("could not inspect volume", (results / "resource-cleanup.log").read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
