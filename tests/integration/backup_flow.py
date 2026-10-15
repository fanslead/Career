#!/usr/bin/env python3
"""Exercise offline backup and restore against an isolated, accepted M1 task.

Example:
  python3 tests/integration/backup_flow.py --host-command \
    "dotnet apps/Career.Host/bin/Release/net10.0/Career.Host.dll"

The command is invoked with --workspace and --port for Host mode, and with
--backup or --restore plus --workspace for the offline CLI operations.
"""

from __future__ import annotations

import hashlib
import json
import os
import secrets
import shlex
import shutil
import signal
import sqlite3
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
import zipfile
from pathlib import Path
from typing import Any

sys.path.insert(0, str(Path(__file__).resolve().parent))
import m1_flow as flow  # noqa: E402
from keychain_cleanup import cleanup_keychain_item  # noqa: E402


class BackupFlowFailure(RuntimeError):
    pass


def call_host_command(
    base_command: list[str],
    workspace: Path,
    port: int | None = None,
    backup: Path | None = None,
    restore: Path | None = None,
    env: dict[str, str] | None = None,
) -> subprocess.CompletedProcess[str]:
    command = list(base_command)
    if backup is not None:
        command.extend(["--backup", str(backup), "--workspace", str(workspace)])
    elif restore is not None:
        command.extend(["--restore", str(restore), "--workspace", str(workspace)])
    else:
        if port is None:
            raise ValueError("Host mode requires a port.")
        command.extend(["--workspace", str(workspace), "--port", str(port)])
    try:
        return subprocess.run(command, env=env, text=True, capture_output=True, timeout=90, check=False)
    except subprocess.TimeoutExpired as error:
        raise BackupFlowFailure("Offline backup/restore CLI timed out.") from error


def expect_cli_success(result: subprocess.CompletedProcess[str], operation: str) -> None:
    if result.returncode != 0:
        raise BackupFlowFailure(f"{operation} CLI failed with exit code {result.returncode}.")


def expect_cli_failure(result: subprocess.CompletedProcess[str], operation: str) -> None:
    if result.returncode == 0:
        raise BackupFlowFailure(f"{operation} CLI unexpectedly accepted a damaged archive.")


def pair(base_url: str, output: flow.HostOutput, timeout: float) -> tuple[str, str]:
    try:
        code = output.code.get(timeout=min(timeout, 10))
    except Exception as error:
        raise BackupFlowFailure("Host did not print a pairing code for the isolated workspace.") from error
    status, session, _ = flow.json_request(base_url, "/api/auth/pair", method="POST", body={"code": code})
    flow.require_status(status, 200, "POST pair", session)
    token = session.get("accessToken")
    workspace_id = session.get("workspaceId")
    if not isinstance(token, str) or not token or not isinstance(workspace_id, str) or not workspace_id:
        raise BackupFlowFailure("Pairing did not return a workspace session.")
    output.add_secret(code)
    output.add_secret(token)
    return token, workspace_id


def configure_fixture_connection(base_url: str, token: str, model_url: str) -> None:
    body = {
        "requestId": str(uuid.uuid4()),
        "name": "Backup integration fixture",
        "protocol": "chat-completions",
        "endpoint": model_url,
        "model": "career-test-model",
        "apiKey": "backup-integration-fixture-key",
        "allowLocalEndpoint": True,
        "maxOutputTokens": 1024,
        "maxModelCalls": 6,
        "maxInputCharacters": 8000,
    }
    status, saved, _ = flow.json_request(base_url, "/api/connection", method="PUT", body=body, token=token)
    flow.require_status(status, 200, "PUT connection", saved)
    status, tested, _ = flow.json_request(
        base_url,
        "/api/connection/test",
        method="POST",
        body={"requestId": str(uuid.uuid4())},
        token=token,
    )
    flow.require_status(status, (200, 202), "POST connection test", tested)
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        status, current, _ = flow.json_request(base_url, "/api/connection", token=token)
        flow.require_status(status, 200, "GET connection", current)
        if current.get("testState") == "verified":
            return
        if current.get("testState") == "failed":
            raise BackupFlowFailure("The local model fixture failed its text and tool checks.")
        time.sleep(0.2)
    raise BackupFlowFailure("Timed out waiting for the local model fixture check.")


def create_accepted_artifact(base_url: str, token: str, process: subprocess.Popen[str], timeout: float) -> tuple[str, str, str]:
    task_id = flow.create_confirmed_task(base_url, token, "Backup accepted task", "backup-result.md")
    task = flow.wait_for_task(
        base_url,
        task_id,
        token,
        lambda value: value.get("status") == "WaitingApproval" and any(
            item.get("status") == "Pending" for item in value.get("approvals", [])
        ),
        "the artifact publication approval",
        timeout,
        process,
    )
    approval = next(item for item in reversed(task.get("approvals", [])) if item.get("status") == "Pending")
    flow.decide_approval(base_url, task, approval, token, "approve", "Approve the isolated backup fixture artifact")
    task = flow.wait_for_task(
        base_url,
        task_id,
        token,
        lambda value: value.get("status") == "InReview" and bool(value.get("artifacts")),
        "the published artifact entering review",
        timeout,
        process,
    )
    artifact = task["artifacts"][-1]
    acceptance = {
        "requestId": str(uuid.uuid4()),
        "expectedVersion": task["version"],
        "taskRevision": task["revision"],
        "artifactId": artifact["id"],
        "artifactVersion": artifact["version"],
        "contentHash": artifact["contentHash"],
        "decision": "accept",
    }
    status, _, _ = flow.json_request(
        base_url,
        f"/api/tasks/{urllib.parse.quote(task_id, safe='')}/acceptances",
        method="POST",
        body=acceptance,
        token=token,
    )
    flow.require_status(status, (200, 202), "POST acceptance", {})
    completed = flow.wait_for_task(
        base_url,
        task_id,
        token,
        lambda value: value.get("status") == "Done"
        and any(item.get("decision") == "accept" and item.get("contentHash") == artifact["contentHash"] for item in value.get("acceptances", [])),
        "the accepted task entering Done",
        timeout,
        process,
    )
    if completed.get("status") != "Done":
        raise BackupFlowFailure("The fixture task was not accepted before backup.")
    return task_id, artifact["id"], artifact["contentHash"]


def query_database(database_bytes: bytes, query: str, parameters: tuple[Any, ...] = ()) -> list[tuple[Any, ...]]:
    connection = sqlite3.connect(":memory:")
    try:
        connection.deserialize(database_bytes)
        return connection.execute(query, parameters).fetchall()
    finally:
        connection.close()


def validate_archive(backup: Path, workspace_id: str, task_id: str, artifact_id: str, content_hash: str) -> str:
    with zipfile.ZipFile(backup) as archive:
        names = archive.namelist()
        if len(names) != len(set(names)) or "manifest.json" not in names:
            raise BackupFlowFailure("Backup ZIP has duplicate entries or no manifest.")
        manifest = json.loads(archive.read("manifest.json"))
        if manifest.get("schemaVersion") != 1 or manifest.get("workspaceId") != workspace_id:
            raise BackupFlowFailure("Backup manifest did not preserve the workspace identity.")
        if manifest.get("protocol") != "career-worker/1" or not manifest.get("secretRestorationRequirement"):
            raise BackupFlowFailure("Backup manifest omitted protocol or secret restoration requirements.")
        declared: dict[str, dict[str, Any]] = {}
        total = 0
        for item in manifest.get("files", []):
            path = item.get("path")
            if not isinstance(path, str) or path in declared:
                raise BackupFlowFailure("Backup manifest has an invalid or repeated file path.")
            try:
                payload = archive.read(path)
            except KeyError as error:
                raise BackupFlowFailure(f"Backup manifest file is missing: {path}.") from error
            if len(payload) != item.get("bytes") or hashlib.sha256(payload).hexdigest() != item.get("sha256"):
                raise BackupFlowFailure(f"Backup manifest length or hash does not match {path}.")
            declared[path] = item
            total += len(payload)
            if total > 512 * 1024 * 1024:
                raise BackupFlowFailure("Backup payload exceeded the 512 MiB integration limit.")
        if set(names) != set(declared) | {"manifest.json"}:
            raise BackupFlowFailure("Backup ZIP contains unlisted files or misses manifest entries.")
        if any(path.endswith(("-wal", "-shm")) or path.endswith(".host.lock") or "process.marker" in path for path in names):
            raise BackupFlowFailure("Backup ZIP contains a runtime lock or SQLite sidecar.")
        if "state/app.db" not in declared or "workspace.json" not in declared:
            raise BackupFlowFailure("Backup ZIP omitted the workspace database or metadata.")
        database_bytes = archive.read("state/app.db")
        if database_bytes[:16] != b"SQLite format 3\x00" or database_bytes[18:20] != b"\x01\x01":
            raise BackupFlowFailure("Backup database is not a standalone rollback-journal SQLite file.")

        done = query_database(database_bytes, "SELECT id FROM tasks WHERE id=? AND status='Done'", (task_id,))
        accepted = query_database(
            database_bytes,
            "SELECT artifact_id,artifact_version,content_hash FROM acceptances WHERE task_id=? AND decision='accept'",
            (task_id,),
        )
        if not done or not any(row[0] == artifact_id and row[2] == content_hash for row in accepted):
            raise BackupFlowFailure("Backup database did not retain the accepted task and artifact receipt.")
        artifact_rows = query_database(
            database_bytes,
            "SELECT content_hash FROM artifacts WHERE id=? AND version=?",
            (artifact_id, accepted[0][1]),
        )
        if not artifact_rows or artifact_rows[0][0] != content_hash:
            raise BackupFlowFailure("Backup database artifact hash did not match its acceptance.")
        artifact_manifest_path = f"artifacts/{artifact_id}/{accepted[0][1]}/manifest.json"
        if artifact_manifest_path not in declared:
            raise BackupFlowFailure("Backup ZIP omitted the accepted artifact manifest.")
        artifact_manifest = json.loads(archive.read(artifact_manifest_path))
        artifact_path = f"artifacts/{artifact_id}/{accepted[0][1]}/{artifact_manifest['fileName']}"
        if artifact_path not in declared or declared[artifact_path]["sha256"] != content_hash:
            raise BackupFlowFailure("Backup ZIP did not preserve the accepted artifact bytes.")
        return artifact_path


def corrupt_archive(source: Path, destination: Path) -> None:
    with zipfile.ZipFile(source, "r") as original, zipfile.ZipFile(destination, "w", compression=zipfile.ZIP_DEFLATED) as damaged:
        names = original.namelist()
        candidates = [name for name in names if name.startswith("artifacts/") and not name.endswith("manifest.json")]
        if not candidates:
            raise BackupFlowFailure("Could not find an artifact file to tamper with.")
        target = candidates[0]
        for name in names:
            payload = original.read(name)
            damaged.writestr(name, b"damaged backup content\n" if name == target else payload)


def wait_for_workspace(base_url: str, token: str, process: subprocess.Popen[str], timeout: float) -> dict[str, Any]:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise BackupFlowFailure(f"Restored Host exited with code {process.returncode}.")
        status, workspace, _ = flow.json_request(base_url, "/api/workspace", token=token, timeout=1)
        if status == 200 and workspace.get("executionAvailable") is True:
            return workspace
        time.sleep(0.2)
    raise BackupFlowFailure("Restored Host did not finish restart recovery in time.")


def run(arguments: Any) -> dict[str, Any]:
    base_command = shlex.split(arguments.host_command)
    if not base_command:
        raise BackupFlowFailure("--host-command is empty.")

    temporary = Path(tempfile.mkdtemp(prefix="career-backup-flow-"))
    workspace = temporary / "source"
    backup = temporary / "workspace.zip"
    restored = temporary / "restored"
    damaged = temporary / "damaged.zip"
    damaged_target = temporary / "damaged-target"
    key_file = temporary / "vault.key"
    fixture_path = Path(__file__).resolve().parents[1] / "fixtures" / "model-server.py"
    host_process: subprocess.Popen[str] | None = None
    restored_process: subprocess.Popen[str] | None = None
    fixture_process: subprocess.Popen[str] | None = None
    workspace_id: str | None = None
    token: str | None = None
    report: dict[str, Any] = {
        "schemaVersion": 1,
        "test": "backup_flow",
        "status": "failed",
        "credentialsIncluded": False,
        "checks": {},
    }
    cleanup_failure: str | None = None
    try:
        port = flow.reserve_port()
        restored_port = flow.reserve_port()
        fixture_port = flow.reserve_port()
        fixture_process = flow.launch(
            [sys.executable, str(fixture_path), "--host", "127.0.0.1", "--port", str(fixture_port)],
            os.environ.copy(),
            stdout=subprocess.DEVNULL,
        )
        fixture_base = f"http://127.0.0.1:{fixture_port}"
        flow.wait_json(fixture_base, "/health", None, arguments.startup_timeout, fixture_process)
        model_url = f"{fixture_base}/v1"

        host_env = os.environ.copy()
        if sys.platform.startswith("linux"):
            key_file.write_bytes(secrets.token_bytes(32))
            key_file.chmod(0o600)
            host_env["CAREER_VAULT_KEY_FILE"] = str(key_file)

        host_process = flow.launch(
            base_command + ["--workspace", str(workspace), "--port", str(port)], host_env
        )
        host_output = flow.HostOutput(host_process)
        setattr(host_process, "_career_output", host_output)
        base_url = f"http://127.0.0.1:{port}"
        flow.wait_json(base_url, "/api/bootstrap", None, arguments.startup_timeout, host_process)
        token, workspace_id = pair(base_url, host_output, arguments.startup_timeout)
        configure_fixture_connection(base_url, token, model_url)
        task_id, artifact_id, content_hash = create_accepted_artifact(base_url, token, host_process, arguments.flow_timeout)

        flow.stop_process(host_process)
        host_process = None
        token_hash = hashlib.sha256(token.encode("utf-8")).hexdigest()
        database_path = workspace / "state" / "app.db"
        with sqlite3.connect(database_path, timeout=5) as database:
            changed = database.execute(
                "UPDATE sessions SET access_expires=? WHERE access_hash=?",
                ("2099-01-01T00:00:00.0000000+00:00", token_hash),
            ).rowcount
            database.commit()
        if changed != 1:
            raise BackupFlowFailure("Could not preserve a still-live owner token for the revocation test.")

        result = call_host_command(base_command, workspace, backup=backup, env=host_env)
        expect_cli_success(result, "Backup")
        if not backup.is_file():
            raise BackupFlowFailure("Backup CLI did not create the requested archive.")
        artifact_path = validate_archive(backup, workspace_id, task_id, artifact_id, content_hash)

        corrupt_archive(backup, damaged)
        damaged_target.mkdir()
        result = call_host_command(base_command, damaged_target, restore=damaged, env=host_env)
        expect_cli_failure(result, "Corrupt restore")
        if list(damaged_target.iterdir()):
            raise BackupFlowFailure("Failed restore changed its empty target directory.")

        result = call_host_command(base_command, restored, restore=backup, env=host_env)
        expect_cli_success(result, "Restore")
        if not (restored / "state" / "restore-pending").is_file():
            raise BackupFlowFailure("Restore CLI did not leave the required startup barrier marker.")
        restored_artifact = restored / Path(artifact_path)
        if hashlib.sha256(restored_artifact.read_bytes()).hexdigest() != content_hash:
            raise BackupFlowFailure("Restored artifact bytes did not match the accepted content hash.")
        restored_database = (restored / "state" / "app.db").read_bytes()
        if not query_database(restored_database, "SELECT id FROM tasks WHERE id=? AND status='Done'", (task_id,)):
            raise BackupFlowFailure("Restore did not preserve the accepted task status.")

        restored_process = flow.launch(
            base_command + ["--workspace", str(restored), "--port", str(restored_port)], host_env
        )
        restored_output = flow.HostOutput(restored_process)
        setattr(restored_process, "_career_output", restored_output)
        restored_url = f"http://127.0.0.1:{restored_port}"
        bootstrap = flow.wait_json(restored_url, "/api/bootstrap", None, arguments.startup_timeout, restored_process)
        if bootstrap.get("workspaceId") != workspace_id:
            raise BackupFlowFailure("Restore changed the stable workspace ID.")
        status, _, _ = flow.json_request(restored_url, "/api/workspace", token=token)
        if status != 401:
            raise BackupFlowFailure("The pre-backup owner session remained valid after restore.")

        restored_token, restored_id = pair(restored_url, restored_output, arguments.startup_timeout)
        restored_output.add_secret(restored_token)
        if restored_id != workspace_id:
            raise BackupFlowFailure("New pairing after restore returned a different workspace ID.")
        workspace_state = wait_for_workspace(restored_url, restored_token, restored_process, arguments.startup_timeout)
        if workspace_state.get("intakePaused") is not True:
            raise BackupFlowFailure("Restored workspace did not keep intake paused.")
        with sqlite3.connect(restored / "state" / "app.db") as database:
            revoked = database.execute("SELECT revoked FROM sessions WHERE access_hash=?", (token_hash,)).fetchone()
            if revoked is None or revoked[0] != 1:
                raise BackupFlowFailure("Restore startup did not revoke the old persisted session.")
        marker_names = [path.name for path in (restored / "state").glob("restore-applied-*")]
        if not (restored / "state" / "restore-pending").exists() and not marker_names:
            raise BackupFlowFailure("Restore barrier marker was removed without an audit marker.")
        with zipfile.ZipFile(backup) as archive:
            archive_entry_count = len(archive.namelist())
        report.update(
            {
                "status": "passed",
                "checks": {
                    "acceptedTaskAndArtifactReceiptPreserved": True,
                    "archiveManifestAndFileHashesVerified": True,
                    "databaseIsStandaloneRollbackJournal": True,
                    "damagedRestoreRejectedWithoutChangingTarget": True,
                    "restoredArtifactHashVerified": True,
                    "stableWorkspaceIdPreserved": True,
                    "oldSessionRevoked": True,
                    "restoredIntakePaused": True,
                    "restoreBarrierAuditMarkerRetained": True,
                },
                "archive": {
                    "entryCount": archive_entry_count,
                    "bytes": backup.stat().st_size,
                },
            }
        )
        print("PASS backup lifecycle: accepted task and SHA-256 restored, damaged archive preserved target, startup paused intake and revoked the old session.")
    finally:
        original_error = sys.exc_info()[1]
        flow.stop_process(host_process)
        flow.stop_process(restored_process)
        flow.stop_process(fixture_process)
        keychain_result = (
            cleanup_keychain_item(workspace_id)
            if sys.platform == "darwin"
            else {"cleanup": None, "status": "not-applicable", "absenceVerified": None}
        )
        report["cleanup"] = {"keychainItem": keychain_result}
        if keychain_result.get("cleanup") is False:
            cleanup_failure = str(keychain_result.get("error", "Keychain item cleanup failed."))
            report["status"] = "failed"
            report["cleanupError"] = cleanup_failure
            if original_error is not None:
                report["primaryFailureType"] = type(original_error).__name__
                if hasattr(original_error, "add_note"):
                    original_error.add_note(f"Keychain cleanup also failed: {cleanup_failure}")
                print(f"Keychain cleanup also failed; preserving original error: {cleanup_failure}", file=sys.stderr)
        shutil.rmtree(temporary, ignore_errors=True)
        report_path = getattr(arguments, "report", None)
        if report_path:
            destination = Path(report_path).expanduser()
            if not destination.is_absolute():
                destination = Path.cwd() / destination
            destination.parent.mkdir(parents=True, exist_ok=True)
            temporary_report = destination.with_suffix(destination.suffix + ".tmp")
            temporary_report.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
            os.replace(temporary_report, destination)
            print(f"Report saved: {destination.resolve()}")
    if cleanup_failure:
        raise BackupFlowFailure(cleanup_failure)
    return report


def parse_arguments() -> Any:
    import argparse

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host-command", required=True, help="Host executable/command before the workspace-mode flags.")
    parser.add_argument("--startup-timeout", type=float, default=30)
    parser.add_argument("--flow-timeout", type=float, default=90)
    parser.add_argument("--report", help="Optional path for a credential-free JSON result report.")
    return parser.parse_args()


def main() -> int:
    try:
        run(parse_arguments())
        return 0
    except (BackupFlowFailure, flow.FlowFailure, urllib.error.URLError, OSError, sqlite3.Error, KeyError, ValueError) as error:
        print(f"FAIL backup lifecycle: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
