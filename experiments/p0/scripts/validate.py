#!/usr/bin/env python3
"""Process-level P0 integration driver for the native Host and MAF Worker.

This intentionally uses only the Python standard library. Each case gets its
own P0_STATE directory and writes a summary plus per-process Host logs under
the requested output directory.
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import http.client
import json
import os
import shlex
import signal
import socket
import sqlite3
import subprocess
import sys
import tempfile
import threading
import time
import traceback
from contextlib import closing
from pathlib import Path
from typing import Any, Callable
from urllib.error import HTTPError, URLError
from urllib.parse import quote
from urllib.request import Request, urlopen


STAGE_TIMEOUT = 30.0
HTTP_TIMEOUT = 3.0
SSE_READ_BUDGET = 1.0


class ProbeFailure(RuntimeError):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ProbeFailure(message)


def free_loopback_url() -> str:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        port = sock.getsockname()[1]
    return f"http://127.0.0.1:{port}"


def http_request(
    method: str,
    url: str,
    *,
    payload: dict[str, Any] | None = None,
    headers: dict[str, str] | None = None,
    timeout: float = HTTP_TIMEOUT,
) -> tuple[int, bytes, dict[str, str]]:
    request_headers = dict(headers or {})
    body = None
    if payload is not None:
        body = json.dumps(payload, separators=(",", ":")).encode("utf-8")
        request_headers.setdefault("Content-Type", "application/json")
    request = Request(url, data=body, headers=request_headers, method=method)
    try:
        with urlopen(request, timeout=timeout) as response:
            return response.status, response.read(), dict(response.headers.items())
    except HTTPError as error:
        return error.code, error.read(), dict(error.headers.items())
    except (URLError, TimeoutError, socket.timeout) as error:
        raise ProbeFailure(f"{method} {url} failed: {error}") from error


def get_json(base_url: str, path: str) -> tuple[int, Any]:
    status, body, _ = http_request("GET", base_url + path)
    try:
        return status, json.loads(body) if body else None
    except json.JSONDecodeError as error:
        raise ProbeFailure(f"GET {path} returned invalid JSON: {body[:500]!r}") from error


def post_json(
    base_url: str,
    path: str,
    payload: dict[str, Any] | None = None,
    *,
    extra_headers: dict[str, str] | None = None,
) -> tuple[int, Any]:
    headers = {"X-P0-Client": "1"}
    headers.update(extra_headers or {})
    status, body, _ = http_request("POST", base_url + path, payload=payload, headers=headers)
    try:
        return status, json.loads(body) if body else None
    except json.JSONDecodeError as error:
        raise ProbeFailure(f"POST {path} returned invalid JSON: {body[:500]!r}") from error


def read_sse(base_url: str, run_id: str, cursor: int, budget: float = SSE_READ_BUDGET) -> tuple[list[dict[str, Any]], float]:
    """Read only currently available SSE events; never wait for run completion."""
    host_port = base_url.removeprefix("http://")
    host, port_text = host_port.rsplit(":", 1)
    connection = http.client.HTTPConnection(host, int(port_text), timeout=0.25)
    path = f"/api/runs/{quote(run_id, safe='')}/events?after={cursor}"
    started = time.monotonic()
    frames: list[dict[str, Any]] = []
    frame_id: int | None = None
    frame_data: list[str] = []
    try:
        connection.request("GET", path, headers={"Accept": "text/event-stream"})
        response = connection.getresponse()
        require(response.status == 200, f"SSE GET returned HTTP {response.status}")
        require("text/event-stream" in response.getheader("Content-Type", ""), "SSE endpoint has wrong content type")
        if connection.sock is not None:
            connection.sock.settimeout(0.2)
        deadline = started + budget
        while time.monotonic() < deadline:
            try:
                raw_line = response.fp.readline() if response.fp is not None else b""
            except (socket.timeout, TimeoutError):
                break
            if not raw_line:
                break
            line = raw_line.decode("utf-8", errors="replace").rstrip("\r\n")
            if not line:
                if frame_data:
                    event = json.loads("\n".join(frame_data))
                    if frame_id is not None:
                        event.setdefault("seq", frame_id)
                    frames.append(event)
                frame_id = None
                frame_data = []
            elif line.startswith("id:"):
                value = line[3:].strip()
                if value.isdigit():
                    frame_id = int(value)
            elif line.startswith("data:"):
                frame_data.append(line[5:].lstrip())
        return frames, time.monotonic() - started
    finally:
        connection.close()


class HostSupervisor:
    """Owns only Host process groups started by this validation run."""

    def __init__(self, host: Path, worker: Path, dotnet: str, state: Path, logs: Path, faults: bool = True):
        self.host = host
        self.worker = worker
        self.dotnet = dotnet
        self.state = state
        self.logs = logs
        self.faults = faults
        self.processes: list[tuple[subprocess.Popen[bytes], int]] = []
        self.process_metadata: list[dict[str, Any]] = []
        self.generation = 0
        self.base_url = ""
        self.worker_timeout_seconds: int | None = None

    def start(self, *, env_overrides: dict[str, str] | None = None) -> tuple[subprocess.Popen[bytes], str]:
        self.generation += 1
        self.base_url = free_loopback_url()
        self.logs.mkdir(parents=True, exist_ok=True)
        stdout_path = self.logs / f"host-{self.generation}.stdout.log"
        stderr_path = self.logs / f"host-{self.generation}.stderr.log"
        env = os.environ.copy()
        env.update(
            {
                "P0_URL": self.base_url,
                "P0_STATE": str(self.state.resolve()),
                "P0_WORKER": str(self.worker),
                "P0_DOTNET": self.dotnet,
                "P0_ENABLE_FAULTS": "1" if self.faults else "0",
            }
        )
        if self.faults and self.worker_timeout_seconds is not None:
            env["P0_WORKER_TIMEOUT_SECONDS"] = str(self.worker_timeout_seconds)
        env.update(env_overrides or {})
        stdout_file = stdout_path.open("wb")
        stderr_file = stderr_path.open("wb")
        try:
            kwargs: dict[str, Any] = {}
            if os.name == "nt":
                kwargs["creationflags"] = getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0)
            else:
                kwargs["start_new_session"] = True
            process = subprocess.Popen(
                [str(self.host)],
                cwd=str(self.host.parent),
                env=env,
                stdin=subprocess.DEVNULL,
                stdout=stdout_file,
                stderr=stderr_file,
                **kwargs,
            )
        except Exception:
            stdout_file.close()
            stderr_file.close()
            raise
        stdout_file.close()
        stderr_file.close()
        pgid = process.pid
        self.processes.append((process, pgid))
        self.process_metadata.append(
            {
                "generation": self.generation,
                "pid": process.pid,
                "processGroupId": pgid,
                "url": self.base_url,
                "stdoutLog": str(stdout_path),
                "stderrLog": str(stderr_path),
                "startedUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
                "terminationRequested": None,
                "returnCode": None,
                "ownedWorkerPids": [],
                "ownedWorkerPidsAfterCleanup": None,
                "groupPresentAfterCleanup": None,
            }
        )
        self._wait_healthy(process)
        return process, self.base_url

    def _wait_healthy(self, process: subprocess.Popen[bytes]) -> None:
        deadline = time.monotonic() + STAGE_TIMEOUT
        last_error = "no response"
        while time.monotonic() < deadline:
            if process.poll() is not None:
                raise ProbeFailure(f"Host exited during startup (code {process.returncode}); {self._logs_tail()}")
            try:
                status, health = get_json(self.base_url, "/health")
            except (ProbeFailure, OSError) as error:
                last_error = str(error)
            else:
                if status == 200 and isinstance(health, dict) and health.get("status") == "ok":
                    require(health.get("nativeAot") is True, f"health.nativeAot is not true: {health}")
                    require(health.get("fts5") is True, f"health.fts5 is not true: {health}")
                    require(bool(health.get("sqliteVersion")), f"health.sqliteVersion is empty: {health}")
                    require(health.get("workerConfigured") is True, f"health.workerConfigured is not true: {health}")
                    require(health.get("processId") == process.pid, f"health.processId does not match Host PID: {health}")
                    return
                last_error = f"HTTP {status}: {health}"
            time.sleep(0.1)
        raise ProbeFailure(f"Host did not become healthy within {STAGE_TIMEOUT:.0f}s: {last_error}; {self._logs_tail()}")

    def kill(self, process: subprocess.Popen[bytes], *, force: bool) -> None:
        record = next(((candidate, pgid) for candidate, pgid in self.processes if candidate is process), None)
        if record is None:
            return
        _, pgid = record
        metadata = next((item for item in self.process_metadata if item["pid"] == process.pid), None)
        if metadata is not None and metadata.get("cleanupCompletedUtc") and process.poll() is not None:
            return
        worker_pids: list[int] = []
        inspection_error: str | None = None
        try:
            worker_pids = self._worker_pids_owned_by(process, pgid)
        except ProbeFailure as error:
            inspection_error = str(error)
        if metadata is not None:
            metadata["ownedWorkerPids"] = worker_pids
            if inspection_error is not None:
                metadata["workerInspectionError"] = inspection_error
        sig = signal.SIGKILL if force else signal.SIGTERM
        if os.name == "nt":
            if metadata is not None:
                metadata["terminationRequested"] = "taskkill /F /T"
            if process.poll() is None:
                subprocess.run(
                    ["taskkill", "/F", "/T", "/PID", str(process.pid)],
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.DEVNULL,
                    timeout=5,
                    check=False,
                )
        else:
            if process.poll() is None:
                try:
                    process.send_signal(sig)
                except ProcessLookupError:
                    pass
                else:
                    if metadata is not None:
                        metadata["terminationRequested"] = sig.name
            elif metadata is not None and metadata["terminationRequested"] is None:
                metadata["terminationRequested"] = "already-exited"
            for worker_pid in worker_pids:
                self._signal_owned_worker(worker_pid, pgid, sig)

        try:
            process.wait(timeout=5 if force else 2)
        except subprocess.TimeoutExpired:
            try:
                process.kill()
            except ProcessLookupError:
                pass
            process.wait(timeout=5)

        if os.name != "nt":
            if inspection_error is not None:
                try:
                    worker_pids = self._worker_pids_owned_by(process, pgid)
                    inspection_error = None
                    if metadata is not None:
                        metadata["ownedWorkerPids"] = worker_pids
                        metadata.pop("workerInspectionError", None)
                except ProbeFailure as error:
                    inspection_error = str(error)
            deadline = time.monotonic() + (1.0 if force else 2.0)
            while time.monotonic() < deadline and any(self._pid_in_group(pid, pgid) for pid in worker_pids):
                time.sleep(0.05)
            remaining = [pid for pid in worker_pids if self._pid_in_group(pid, pgid)]
            for worker_pid in remaining:
                self._signal_owned_worker(worker_pid, pgid, signal.SIGKILL)
            deadline = time.monotonic() + 2.0
            while time.monotonic() < deadline and any(self._pid_in_group(pid, pgid) for pid in remaining):
                time.sleep(0.05)
            remaining = [pid for pid in remaining if self._pid_in_group(pid, pgid)]
            if metadata is not None:
                metadata["ownedWorkerPidsAfterCleanup"] = remaining
                metadata["returnCode"] = process.poll()
            if process.poll() is None or remaining or inspection_error is not None:
                if metadata is not None:
                    metadata["cleanupError"] = (
                        f"Host/owned Worker cleanup incomplete: host={process.pid}, "
                        f"workers={remaining}, inspection={inspection_error}"
                    )
                raise ProbeFailure(
                    f"Host/owned Worker cleanup incomplete: host={process.pid}, workers={remaining}, inspection={inspection_error}"
                )
            if metadata is not None:
                metadata.pop("cleanupError", None)
                metadata["cleanupCompletedUtc"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
        elif metadata is not None:
            # taskkill is tree-scoped while the leader is present; Windows does
            # not expose a portable ownership check for orphaned child processes.
            metadata["ownedWorkerPidsAfterCleanup"] = None
            metadata["cleanupCompletedUtc"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
        if metadata is not None:
            metadata["returnCode"] = process.poll()

    def _worker_pids_owned_by(self, host_process: subprocess.Popen[bytes], pgid: int) -> list[int]:
        if os.name == "nt":
            return []
        database = self.state / "app.db"
        if not database.is_file():
            return []
        uri = database.resolve().as_uri() + "?mode=ro"
        try:
            with closing(sqlite3.connect(uri, uri=True, timeout=2.0)) as db:
                rows = db.execute("SELECT DISTINCT worker_pid FROM runs WHERE worker_pid IS NOT NULL").fetchall()
        except sqlite3.Error as error:
            raise ProbeFailure(f"cannot inspect persisted Worker ownership before Host cleanup: {error}") from error
        return sorted({
            int(row[0])
            for row in rows
            if row[0] is not None
            and int(row[0]) != host_process.pid
            and self._pid_in_group(int(row[0]), pgid)
        })

    @classmethod
    def _pid_in_group(cls, pid: int, pgid: int) -> bool:
        if not cls._pid_exists(pid):
            return False
        try:
            return os.getpgid(pid) == pgid
        except ProcessLookupError:
            return False

    @classmethod
    def _signal_owned_worker(cls, pid: int, pgid: int, sig: int) -> None:
        if not cls._pid_in_group(pid, pgid):
            return
        try:
            os.kill(pid, sig)
        except ProcessLookupError:
            pass

    def close(self) -> None:
        errors: list[str] = []
        for process, _ in reversed(self.processes):
            try:
                self.kill(process, force=False)
            except Exception as error:
                errors.append(f"PID {process.pid}: {type(error).__name__}: {error}")
        if errors:
            raise ProbeFailure("Host process cleanup failed: " + "; ".join(errors))

    def kill_worker(self, worker_pid: int, host_process: subprocess.Popen[bytes]) -> None:
        """Kill only the Worker PID reported by this run, after checking its ownership."""
        require(worker_pid > 0 and worker_pid != host_process.pid, f"invalid Worker PID: {worker_pid}")
        host_group = host_process.pid
        if os.name != "nt":
            try:
                actual_group = os.getpgid(worker_pid)
            except ProcessLookupError as error:
                raise ProbeFailure(f"Worker PID {worker_pid} exited before the requested fault") from error
            require(actual_group == host_group, f"Worker PID {worker_pid} is outside Host process group {host_group}")
            os.kill(worker_pid, signal.SIGKILL)
        else:
            result = subprocess.run(
                ["taskkill", "/F", "/PID", str(worker_pid)],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                timeout=5,
                check=False,
            )
            require(result.returncode == 0, f"taskkill could not terminate Worker PID {worker_pid}: exit={result.returncode}")
        deadline = time.monotonic() + 5
        while self._pid_exists(worker_pid) and time.monotonic() < deadline:
            time.sleep(0.05)
        require(not self._pid_exists(worker_pid), f"Worker PID {worker_pid} remained after forced termination")

    @staticmethod
    def _pid_exists(pid: int) -> bool:
        try:
            os.kill(pid, 0)
        except ProcessLookupError:
            return False
        except PermissionError:
            return True
        return True

    def _logs_tail(self) -> str:
        if not self.logs.exists():
            return ""
        chunks = []
        for path in sorted(self.logs.glob("*.log")):
            try:
                text = path.read_text(errors="replace")[-3000:]
                chunks.append(f"{path.name}:\n{text}")
            except OSError:
                pass
        return "\n".join(chunks)


def assert_static_and_post_guards(base_url: str) -> None:
    status, body, headers = http_request("GET", base_url + "/")
    require(status == 200, f"static homepage returned HTTP {status}")
    require(bool(body), "static homepage response is empty")
    require("text/html" in headers.get("Content-Type", ""), "static homepage is not HTML")

    port = base_url.rsplit(":", 1)[1]
    allowed_host_headers = (f"127.0.0.1:{port}", f"localhost:{port}", f"[::1]:{port}")
    for host_header in allowed_host_headers:
        status, health_body, health_headers = http_request(
            "GET", base_url + "/health", headers={"Host": host_header}
        )
        require(status == 200, f"loopback GET with Host {host_header!r} returned HTTP {status}")
        require("application/json" in health_headers.get("Content-Type", ""),
                f"loopback GET with Host {host_header!r} did not return JSON")
        try:
            health = json.loads(health_body)
        except json.JSONDecodeError as error:
            raise ProbeFailure(f"loopback GET with Host {host_header!r} returned invalid health JSON") from error
        require(isinstance(health, dict) and health.get("status") == "ok",
                f"loopback GET with Host {host_header!r} returned unhealthy data: {health}")

    runs_status, runs_before = get_json(base_url, "/api/runs")
    require(runs_status == 200 and isinstance(runs_before, list),
            f"run listing before forged-Host probes returned HTTP {runs_status}: {runs_before}")
    forged_host = f"outside.invalid:{port}"
    forged_origin = f"http://{forged_host}"
    status, _, _ = http_request(
        "GET",
        base_url + "/health",
        headers={"Host": forged_host, "Origin": forged_origin},
    )
    require(status == 403, f"GET with forged Host returned HTTP {status}, expected 403")

    status, _, _ = http_request(
        "POST",
        base_url + "/api/runs",
        payload={"prompt": "forged Host guard probe", "scenario": "normal"},
        headers={"Host": forged_host, "Origin": forged_origin, "X-P0-Client": "1"},
    )
    require(status == 403, f"POST with forged Host and matching Origin returned HTTP {status}, expected 403")

    localhost_status, _, _ = http_request(
        "POST",
        base_url + "/api/runs",
        payload={"prompt": ""},
        headers={"Host": f"LOCALHOST:{port}", "Origin": f"http://localhost:{port}", "X-P0-Client": "1"},
    )
    require(localhost_status == 400,
            f"POST with case-insensitive localhost Host/Origin and empty prompt returned HTTP {localhost_status}, expected business 400")

    runs_after_status, runs_after = get_json(base_url, "/api/runs")
    require(runs_after_status == 200 and isinstance(runs_after, list),
            f"run listing after forged-Host probes returned HTTP {runs_after_status}: {runs_after}")
    require([item.get("id") for item in runs_after] == [item.get("id") for item in runs_before],
            f"forged-Host requests created or removed Runs: before={runs_before}, after={runs_after}")

    status, _, _ = http_request(
        "POST",
        base_url + "/api/runs",
        payload={"prompt": "guard probe", "scenario": "normal"},
        headers={"X-P0-Client": "1", "Origin": "https://attacker.invalid"},
    )
    require(status == 403, f"cross-origin POST returned HTTP {status}, expected 403")

    status, _, _ = http_request(
        "POST",
        base_url + "/api/runs",
        payload={"prompt": "guard probe", "scenario": "normal"},
    )
    require(status == 403, f"POST without X-P0-Client returned HTTP {status}, expected 403")


def create_run(base_url: str, *, scenario: str = "normal", fault: str | None = None) -> dict[str, Any]:
    extra_headers = {"X-P0-Fault": fault} if fault else None
    status, body = post_json(
        base_url,
        "/api/runs",
        {"prompt": "P0 record a restart-safe note", "scenario": scenario},
        extra_headers=extra_headers,
    )
    require(status == 202 and isinstance(body, dict), f"create run returned HTTP {status}: {body}")
    require(bool(body.get("id")), f"create run did not return an id: {body}")
    return body


def get_run(base_url: str, run_id: str) -> dict[str, Any]:
    status, body = get_json(base_url, f"/api/runs/{quote(run_id, safe='')}")
    require(status == 200 and isinstance(body, dict), f"get run returned HTTP {status}: {body}")
    return body


def json_evidence(raw: str | None) -> dict[str, Any]:
    if raw is None:
        return {"present": False, "validJson": False, "bytes": 0, "sha256": None}
    try:
        json.loads(raw)
    except json.JSONDecodeError:
        valid = False
    else:
        valid = True
    encoded = raw.encode("utf-8")
    return {
        "present": True,
        "validJson": valid,
        "bytes": len(encoded),
        "sha256": hashlib.sha256(encoded).hexdigest(),
    }


def read_db_evidence(state: Path, run_id: str) -> dict[str, Any]:
    """Read persisted P0 evidence without opening the database for writes."""
    database = state / "app.db"
    require(database.is_file(), f"P0 SQLite database is missing: {database}")
    uri = database.resolve().as_uri() + "?mode=ro"
    try:
        with closing(sqlite3.connect(uri, uri=True, timeout=3.0)) as db:
            db.row_factory = sqlite3.Row
            run_row = db.execute(
                "SELECT status,scenario,session,approval,operation_id,args_json,args_hash,decision,fault,error,cancel_requested "
                "FROM runs WHERE id=?",
                (run_id,),
            ).fetchone()
            require(run_row is not None, f"SQLite runs row missing for {run_id}")
            ledger_row = db.execute(
                "SELECT id,state,args_hash,result FROM tool_operations WHERE run_id=?",
                (run_id,),
            ).fetchone()
            event_rows = db.execute(
                "SELECT seq,type,payload FROM events WHERE run_id=? ORDER BY seq",
                (run_id,),
            ).fetchall()
    except sqlite3.Error as error:
        raise ProbeFailure(f"read-only SQLite evidence query failed: {error}") from error

    operation_id = run_row["operation_id"]
    try:
        arguments = json.loads(run_row["args_json"]) if run_row["args_json"] is not None else None
    except json.JSONDecodeError as error:
        raise ProbeFailure(f"persisted tool arguments are invalid JSON: {error}") from error
    receipt_path = state / "receipts" / f"{operation_id}.json" if operation_id else None
    receipt_file: Any = None
    receipt_file_error: str | None = None
    if receipt_path is not None and receipt_path.is_file():
        try:
            receipt_file = json.loads(receipt_path.read_text(encoding="utf-8"))
        except json.JSONDecodeError as error:
            receipt_file_error = f"invalid JSON: {error}"
        except OSError as error:
            raise ProbeFailure(f"cannot read persisted receipt {receipt_path}: {error}") from error
    ledger = None if ledger_row is None else {
        "id": ledger_row["id"],
        "state": ledger_row["state"],
        "argumentsHash": ledger_row["args_hash"],
        "result": json.loads(ledger_row["result"]) if ledger_row["result"] is not None else None,
    }
    events = []
    for row in event_rows:
        try:
            payload = json.loads(row["payload"])
        except json.JSONDecodeError:
            payload = row["payload"]
        events.append({"seq": row["seq"], "type": row["type"], "payload": payload})
    return {
        "run": {
            "status": run_row["status"],
            "scenario": run_row["scenario"],
            "session": json_evidence(run_row["session"]),
            "approval": json_evidence(run_row["approval"]),
            "argumentsJson": json_evidence(run_row["args_json"]),
            "arguments": arguments,
            "argumentsHash": run_row["args_hash"],
            "decision": run_row["decision"],
            "operationId": operation_id,
            "faultInjected": run_row["fault"],
            "error": run_row["error"],
            "cancelRequested": bool(run_row["cancel_requested"]),
        },
        "ledger": ledger,
        "events": events,
        "receiptFile": receipt_file,
        "receiptFileError": receipt_file_error,
        "receiptArtifacts": read_receipt_artifacts(state),
    }


def read_receipt_artifacts(state: Path) -> dict[str, list[dict[str, Any]]]:
    """Capture raw receipt, staging, and quarantine files for failure evidence."""
    artifacts: dict[str, list[dict[str, Any]]] = {}
    for directory_name in ("receipts", "staging", "quarantine"):
        directory = state / directory_name
        entries: list[dict[str, Any]] = []
        if directory.exists():
            for path in sorted(directory.rglob("*")):
                if not path.is_file():
                    continue
                content = path.read_bytes()
                try:
                    decoded = content.decode("utf-8")
                except UnicodeDecodeError:
                    decoded = None
                try:
                    parsed = json.loads(content) if decoded is not None else None
                    valid_json = decoded is not None
                except json.JSONDecodeError:
                    parsed = None
                    valid_json = False
                entries.append(
                    {
                        "path": str(path.relative_to(state)),
                        "bytes": len(content),
                        "sha256": hashlib.sha256(content).hexdigest(),
                        "validJson": valid_json,
                        "json": parsed,
                        "contentUtf8": decoded if not valid_json else None,
                        "contentBase64": base64.b64encode(content).decode("ascii"),
                    }
                )
        artifacts[directory_name] = entries
    return artifacts


def assert_persisted_checkpoint(evidence: dict[str, Any]) -> None:
    run = evidence["run"]
    require(run["session"]["present"] and run["session"]["validJson"], f"MAF session checkpoint is absent or invalid: {run}")
    require(run["approval"]["present"] and run["approval"]["validJson"], f"MAF approval checkpoint is absent or invalid: {run}")
    require(run["argumentsJson"]["present"] and run["argumentsJson"]["validJson"], f"tool arguments checkpoint is absent or invalid: {run}")
    require(bool(run["argumentsHash"]), f"arguments hash is empty: {run}")
    require(bool(run["operationId"]), f"operation id is empty: {run}")
    require(any(event["type"] == "ApprovalRequested" for event in evidence["events"]), "ApprovalRequested event is missing from SQLite")


def assert_checkpoint_survived(before: dict[str, Any], after: dict[str, Any]) -> None:
    assert_persisted_checkpoint(after)
    for key in ("session", "approval", "argumentsJson", "argumentsHash", "operationId"):
        require(before["run"][key] == after["run"][key], f"persisted approval checkpoint changed across Host restart: {key}")


def assert_final_receipt_evidence(evidence: dict[str, Any], *, recovered_session: bool) -> None:
    run = evidence["run"]
    ledger = evidence["ledger"]
    require(run["status"] == "Succeeded", f"SQLite run status is not Succeeded: {run}")
    require(ledger is not None and ledger["state"] == "Succeeded", f"tool operation ledger is not Succeeded: {ledger}")
    require(ledger["argumentsHash"] == run["argumentsHash"], f"ledger arguments hash differs from run checkpoint: {ledger}, {run}")
    require(ledger["result"] is not None, "successful tool ledger has no cached result")
    require(evidence["receiptFile"] is not None, "successful tool ledger has no persisted receipt file")
    require(ledger["result"] == evidence["receiptFile"], "cached ledger result differs from the final receipt file")
    require(evidence["receiptFile"].get("receiptId") == run["operationId"], "final receipt does not bind to the approved operation")
    require(evidence["receiptFile"].get("text") == (run["arguments"] or {}).get("text"), "final receipt text differs from approved tool arguments")
    # The receipt file and cached result are compared as parsed JSON, and the
    # receipt text is tied back to the arguments saved at the approval checkpoint.
    event_types = [event["type"] for event in evidence["events"]]
    for required in ("ApprovalRequested", "ApprovalGranted", "RunSucceeded"):
        require(required in event_types, f"SQLite event {required} is missing: {event_types}")
    require("ToolExecuting" in event_types, f"SQLite ToolExecuting event is missing: {event_types}")
    require("ToolCompleted" in event_types or "ToolResultReused" in event_types, f"SQLite has no completed or reused tool result event: {event_types}")
    if recovered_session:
        restored = any(
            event["type"] == "RunStarted"
            and isinstance(event["payload"], dict)
            and "从已保存的 MAF 会话恢复" in str(event["payload"].get("text", ""))
            for event in evidence["events"]
        )
        require(restored, "SQLite events do not prove the MAF session was restored after Host restart")


def wait_run(
    base_url: str,
    run_id: str,
    acceptable: set[str],
    *,
    timeout: float = STAGE_TIMEOUT,
    host: subprocess.Popen[bytes] | None = None,
) -> dict[str, Any]:
    deadline = time.monotonic() + timeout
    last: dict[str, Any] | None = None
    while time.monotonic() < deadline:
        if host is not None and host.poll() is not None:
            raise ProbeFailure(f"Host exited while waiting for run state: {host.returncode}")
        last = get_run(base_url, run_id)
        if last.get("status") in acceptable:
            return last
        time.sleep(0.1)
    raise ProbeFailure(f"run did not reach {sorted(acceptable)} within {timeout:.0f}s; last RunView={last}")


def wait_for_file(path: Path, *, timeout: float = STAGE_TIMEOUT, host: subprocess.Popen[bytes] | None = None) -> None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if path.exists():
            return
        if host is not None and host.poll() is not None:
            raise ProbeFailure(f"Host exited before creating fault marker {path}: code={host.returncode}")
        time.sleep(0.05)
    raise ProbeFailure(f"fault marker did not appear within {timeout:.0f}s: {path}")


def count_events(evidence: dict[str, Any], event_type: str) -> int:
    return sum(1 for event in evidence["events"] if event["type"] == event_type)


def assert_quiescent(base_url: str, state: Path, run_id: str, *, expected_receipts: int) -> tuple[dict[str, Any], dict[str, Any]]:
    """Prove recovery did not change an unresolved operation without a decision."""
    before_run = get_run(base_url, run_id)
    before_evidence = read_db_evidence(state, run_id)
    time.sleep(0.5)
    after_run = get_run(base_url, run_id)
    after_evidence = read_db_evidence(state, run_id)
    require(before_run.get("status") == "UnknownOutcome", f"run was not UnknownOutcome before idle check: {before_run}")
    require(after_run.get("status") == "UnknownOutcome", f"unresolved run changed state without reconciliation: {after_run}")
    require(before_run.get("receiptCount") == expected_receipts and after_run.get("receiptCount") == expected_receipts,
            f"idle recovery changed receipt count: before={before_run}, after={after_run}")
    require(before_run.get("toolCalls") == after_run.get("toolCalls"),
            f"idle recovery invoked another tool: before={before_run}, after={after_run}")
    require(before_evidence == after_evidence, "idle recovery wrote new SQLite or receipt artifact evidence")
    return after_run, after_evidence


def write_bytes_and_flush(path: Path, content: bytes) -> None:
    with path.open("wb") as stream:
        stream.write(content)
        stream.flush()
        os.fsync(stream.fileno())


def artifact_by_path(evidence: dict[str, Any], relative_path: str) -> dict[str, Any] | None:
    for entries in evidence.get("receiptArtifacts", {}).values():
        for entry in entries:
            if entry["path"] == relative_path:
                return entry
    return None


def assert_waiting_approval(run: dict[str, Any]) -> None:
    require(run.get("status") == "WaitingApproval", f"run is not WaitingApproval: {run}")
    require(run.get("receiptCount") == 0, f"receipt exists before approval: {run}")
    require(bool(run.get("operationId")), f"operationId is empty: {run}")
    require(bool(run.get("argumentsHash")), f"argumentsHash is empty: {run}")
    require(isinstance(run.get("workerPid"), int), f"workerPid missing before approval: {run}")
    require(run.get("modelCalls", 0) >= 1, f"MAF scripted model was not called: {run}")


def assert_resume_pid_changed(before: dict[str, Any], after: dict[str, Any]) -> None:
    require(isinstance(after.get("workerPid"), int), f"workerPid missing after approval: {after}")
    require(before.get("workerPid") != after.get("workerPid"), f"Worker process was reused across approval: before={before}, after={after}")


def assert_success(run: dict[str, Any], *, before: dict[str, Any]) -> None:
    require(run.get("status") == "Succeeded", f"run did not succeed: {run}")
    require(run.get("receiptCount") == 1, f"expected exactly one Host receipt: {run}")
    require(run.get("toolCalls", 0) >= 2, f"expected MAF add and record_note tool calls: {run}")
    require(bool(run.get("text")), f"final agent response is empty: {run}")
    assert_resume_pid_changed(before, run)


def assert_sse_reconnect(events: list[dict[str, Any]], cursor: int, expected_event: str) -> None:
    require(bool(events), f"SSE reconnect after cursor {cursor} returned no events")
    require(all(int(event.get("seq", 0)) > cursor for event in events),
            f"SSE replayed an event at or before cursor {cursor}: {events}")
    event_types = [event.get("type") for event in events]
    require(expected_event in event_types,
            f"SSE reconnect after cursor {cursor} omitted {expected_event}: {event_types}")


def assert_denied(run: dict[str, Any], *, before: dict[str, Any], evidence: dict[str, Any]) -> None:
    require(run.get("status") == "Denied", f"run did not finish as Denied: {run}")
    require(run.get("receiptCount") == 0, f"denied run has a Host receipt: {run}")
    require(isinstance(run.get("workerPid"), int) and run.get("workerPid") != before.get("workerPid"),
            f"denial reused the prior Worker process: before={before}, after={run}")
    require(isinstance(before.get("modelCalls"), int) and isinstance(run.get("modelCalls"), int)
            and run["modelCalls"] > before["modelCalls"],
            f"denial did not execute a model continuation: before={before}, after={run}")
    require(evidence["run"]["status"] == "Denied", f"SQLite run status is not Denied: {evidence['run']}")
    require(evidence["ledger"] is None, f"denied run created a tool operation ledger: {evidence['ledger']}")
    event_types = [event["type"] for event in evidence["events"]]
    require(event_types.count("ApprovalDenied") == 1,
            f"SQLite does not contain exactly one ApprovalDenied event: {event_types}")
    require(event_types.count("RunDenied") == 1,
            f"SQLite does not contain exactly one RunDenied event: {event_types}")
    require("ToolExecuting" not in event_types and "ToolCompleted" not in event_types,
            f"denied run executed or completed a side effect: {event_types}")
    require(not any(evidence["receiptArtifacts"].values()),
            f"denied run left receipt artifacts: {evidence['receiptArtifacts']}")


def start_case_host(case: Path, supervisor: HostSupervisor) -> tuple[subprocess.Popen[bytes], str]:
    return supervisor.start()


def case_normal(case: Path, supervisor: HostSupervisor) -> dict[str, Any]:
    process, base_url = start_case_host(case, supervisor)
    assert_static_and_post_guards(base_url)
    created = create_run(base_url)
    listed_status, runs = get_json(base_url, "/api/runs")
    require(listed_status == 200 and isinstance(runs, list), f"run listing returned HTTP {listed_status}: {runs}")
    require(any(item.get("id") == created["id"] for item in runs), "created run missing from history listing")

    run_id = created["id"]
    waiting = wait_run(base_url, run_id, {"WaitingApproval"}, host=process)
    assert_waiting_approval(waiting)
    initial_events, _ = read_sse(base_url, run_id, 0)
    require(bool(initial_events), "SSE stream returned no initial run events")
    cursor = max(int(event.get("seq", 0)) for event in initial_events)
    reconnected, elapsed = read_sse(base_url, run_id, cursor)
    require(all(int(event.get("seq", 0)) > cursor for event in reconnected), f"SSE replayed an event at or before cursor {cursor}: {reconnected}")
    require(elapsed < 2.0, f"finite SSE read took too long ({elapsed:.2f}s) while run awaited approval")

    run_id_path = quote(run_id, safe="")
    status, _ = post_json(
        base_url,
        f"/api/runs/{run_id_path}/decision",
        {"approved": True, "expectedHash": waiting["argumentsHash"] + "-wrong"},
    )
    require(status == 409, f"approval with wrong argumentsHash returned HTTP {status}, expected 409")

    status, accepted = post_json(
        base_url,
        f"/api/runs/{run_id_path}/decision",
        {"approved": True, "expectedHash": waiting["argumentsHash"]},
    )
    require(status == 202 and isinstance(accepted, dict), f"valid approval returned HTTP {status}: {accepted}")
    duplicate_status, _ = post_json(
        base_url,
        f"/api/runs/{run_id_path}/decision",
        {"approved": True, "expectedHash": waiting["argumentsHash"]},
    )
    require(duplicate_status == 409, f"duplicate approval returned HTTP {duplicate_status}, expected 409")
    resumed_events, _ = read_sse(base_url, run_id, cursor)
    assert_sse_reconnect(resumed_events, cursor, "ApprovalGranted")

    succeeded = wait_run(base_url, run_id, {"Succeeded"}, host=process)
    assert_success(succeeded, before=waiting)
    return {"runId": run_id, "waiting": waiting, "succeeded": succeeded, "sseCursor": cursor, "sseEventsAfterReconnect": len(resumed_events)}


def case_denied(case: Path, supervisor: HostSupervisor) -> dict[str, Any]:
    process, base_url = start_case_host(case, supervisor)
    run_id = create_run(base_url)["id"]
    waiting = wait_run(base_url, run_id, {"WaitingApproval"}, host=process)
    assert_waiting_approval(waiting)
    status, _ = post_json(
        base_url,
        f"/api/runs/{quote(run_id, safe='')}/decision",
        {"approved": False, "expectedHash": waiting["argumentsHash"]},
    )
    require(status == 202, f"valid denial returned HTTP {status}, expected 202")
    denied = wait_run(base_url, run_id, {"Denied"}, host=process)
    evidence = read_db_evidence(supervisor.state, run_id)
    assert_denied(denied, before=waiting, evidence=evidence)
    return {"runId": run_id, "waiting": waiting, "denied": denied, "finalPersistenceEvidence": evidence}


def case_invalid_arguments(case: Path, supervisor: HostSupervisor) -> dict[str, Any]:
    process, base_url = start_case_host(case, supervisor)
    run_id = create_run(base_url, scenario="invalid-arguments")["id"]
    failed = wait_run(base_url, run_id, {"Failed"}, host=process)
    require(failed.get("receiptCount") == 0, f"invalid tool arguments caused a Host receipt: {failed}")

    evidence = read_db_evidence(supervisor.state, run_id)
    require(evidence["run"]["status"] == "Failed", f"invalid-arguments SQLite status is not Failed: {evidence['run']}")
    require(evidence["run"]["scenario"] == "invalid-arguments", f"invalid-arguments scenario was not persisted: {evidence['run']}")
    require(evidence["run"]["session"]["present"] is False, f"malformed arguments persisted a session checkpoint: {evidence['run']}")
    require(evidence["run"]["approval"]["present"] is False, f"malformed arguments left a pending approval: {evidence['run']}")
    require(evidence["run"]["argumentsHash"] is None and evidence["run"]["operationId"] is None, f"malformed arguments created an approval operation: {evidence['run']}")
    require(evidence["ledger"] is None, f"malformed arguments advanced the tool ledger: {evidence['ledger']}")
    require(evidence["receiptFile"] is None, "invalid arguments unexpectedly produced a receipt file")
    event_types = [event["type"] for event in evidence["events"]]
    require("ApprovalRequested" not in event_types and "ApprovalGranted" not in event_types, f"invalid arguments left an approval event: {event_types}")
    require("Failed" in event_types, f"failure event missing from invalid-arguments run: {event_types}")
    require("ToolExecuting" not in event_types and "ToolCompleted" not in event_types, f"malformed arguments reached Host side-effect execution: {event_types}")
    return {"runId": run_id, "failed": failed, "evidence": evidence}


def case_endless(case: Path, supervisor: HostSupervisor) -> dict[str, Any]:
    process, base_url = start_case_host(case, supervisor)
    run_id = create_run(base_url, scenario="endless")["id"]
    failed = wait_run(base_url, run_id, {"Failed"}, host=process)
    require(failed.get("receiptCount") == 0, f"endless MAF calls produced a receipt: {failed}")
    evidence = read_db_evidence(supervisor.state, run_id)
    require(evidence["run"]["status"] == "Failed", f"endless SQLite status is not Failed: {evidence['run']}")
    require(evidence["run"]["scenario"] == "endless", f"endless scenario was not persisted: {evidence['run']}")
    require(evidence["run"]["session"]["present"] is False, f"endless run persisted a session checkpoint: {evidence['run']}")
    require(evidence["run"]["approval"]["present"] is False, f"endless run persisted a pending approval: {evidence['run']}")
    require(evidence["run"]["argumentsHash"] is None and evidence["run"]["operationId"] is None, f"endless run persisted an approval operation: {evidence['run']}")
    require(evidence["ledger"] is None, f"endless run created a tool operation ledger: {evidence['ledger']}")
    require(evidence["receiptFile"] is None, "endless run unexpectedly produced a receipt file")
    event_types = [event["type"] for event in evidence["events"]]
    require("ApprovalRequested" not in event_types and "ApprovalGranted" not in event_types, f"endless run left an approval event: {event_types}")
    return {"runId": run_id, "failed": failed, "evidence": evidence}


def case_approval_restart(case: Path, supervisor: HostSupervisor) -> dict[str, Any]:
    first_process, first_url = start_case_host(case, supervisor)
    run_id = create_run(first_url)["id"]
    waiting_before_restart = wait_run(first_url, run_id, {"WaitingApproval"}, host=first_process)
    assert_waiting_approval(waiting_before_restart)
    checkpoint_before_restart = read_db_evidence(supervisor.state, run_id)
    assert_persisted_checkpoint(checkpoint_before_restart)
    supervisor.kill(first_process, force=True)

    restarted_process, restarted_url = start_case_host(case, supervisor)
    restored = wait_run(restarted_url, run_id, {"WaitingApproval"}, host=restarted_process)
    assert_waiting_approval(restored)
    checkpoint_after_restart = read_db_evidence(supervisor.state, run_id)
    assert_checkpoint_survived(checkpoint_before_restart, checkpoint_after_restart)
    require(restored.get("workerPid") == waiting_before_restart.get("workerPid"), "restored pending run lost its original waiting Worker identity")

    run_id_path = quote(run_id, safe="")
    status, _ = post_json(
        restarted_url,
        f"/api/runs/{run_id_path}/decision",
        {"approved": True, "expectedHash": restored["argumentsHash"]},
    )
    require(status == 202, f"approval after Host restart returned HTTP {status}")
    succeeded = wait_run(restarted_url, run_id, {"Succeeded"}, host=restarted_process)
    assert_success(succeeded, before=restored)
    final_evidence = read_db_evidence(supervisor.state, run_id)
    assert_final_receipt_evidence(final_evidence, recovered_session=True)
    return {
        "runId": run_id,
        "waitingBeforeRestart": waiting_before_restart,
        "checkpointBeforeRestart": checkpoint_before_restart,
        "restored": restored,
        "checkpointAfterRestart": checkpoint_after_restart,
        "succeeded": succeeded,
        "finalPersistenceEvidence": final_evidence,
    }


def case_fault_recovery(case: Path, supervisor: HostSupervisor, fault: str) -> dict[str, Any]:
    process, base_url = start_case_host(case, supervisor)
    run_id = create_run(base_url, fault=fault)["id"]
    waiting = wait_run(base_url, run_id, {"WaitingApproval"}, host=process)
    assert_waiting_approval(waiting)

    status, _ = post_json(
        base_url,
        f"/api/runs/{quote(run_id, safe='')}/decision",
        {"approved": True, "expectedHash": waiting["argumentsHash"]},
    )
    require(status == 202, f"fault case approval returned HTTP {status}")
    state_path = supervisor.state
    marker = state_path / "fault-ready"
    wait_for_file(marker, host=process)
    paused = get_run(base_url, run_id)
    expected_receipts = 1 if fault == "after-effect" else 0
    require(paused.get("receiptCount") == expected_receipts, f"{fault} fault had unexpected receipt count at crash point: {paused}")
    checkpoint_at_fault = read_db_evidence(state_path, run_id)
    assert_persisted_checkpoint(checkpoint_at_fault)
    require(checkpoint_at_fault["ledger"] is not None and checkpoint_at_fault["ledger"]["state"] == "Executing", f"fault did not persist an Executing ledger row: {checkpoint_at_fault['ledger']}")
    supervisor.kill(process, force=True)

    restarted_process, restarted_url = start_case_host(case, supervisor)
    unknown = wait_run(restarted_url, run_id, {"UnknownOutcome"}, host=restarted_process)
    require(unknown.get("receiptCount") == expected_receipts, f"recovery changed receipt count before reconcile: {unknown}")
    unknown_evidence = read_db_evidence(state_path, run_id)
    require(unknown_evidence["ledger"] is not None and unknown_evidence["ledger"]["state"] == "Executing", f"recovery changed the unresolved ledger before reconcile: {unknown_evidence['ledger']}")
    require(any(event["type"] == "OutcomeUnknown" for event in unknown_evidence["events"]), "Host recovery did not persist an OutcomeUnknown event")

    # Leave the restarted Host idle briefly; an unknown result must not be
    # blindly replayed before reconciliation explicitly resolves it.
    before_idle = get_run(restarted_url, run_id)
    events_before_idle = unknown_evidence["events"]
    time.sleep(0.5)
    after_idle = get_run(restarted_url, run_id)
    require(after_idle.get("status") == "UnknownOutcome", f"unknown tool operation was automatically retried: {after_idle}")
    require(after_idle.get("receiptCount") == expected_receipts, f"automatic retry changed receipt count: {after_idle}")
    require(after_idle.get("toolCalls") == before_idle.get("toolCalls"), f"automatic retry invoked tools again: before={before_idle}, after={after_idle}")
    unknown_after_idle_evidence = read_db_evidence(state_path, run_id)
    require(unknown_after_idle_evidence["events"] == events_before_idle, "Host appended events while UnknownOutcome remained unreconciled")

    status, reconciled = post_json(restarted_url, f"/api/runs/{quote(run_id, safe='')}/reconcile")
    require(status == 202 and isinstance(reconciled, dict), f"{fault} reconcile returned HTTP {status}: {reconciled}")
    succeeded = wait_run(restarted_url, run_id, {"Succeeded"}, host=restarted_process)
    assert_success(succeeded, before=waiting)
    final_evidence = read_db_evidence(state_path, run_id)
    assert_final_receipt_evidence(final_evidence, recovered_session=True)
    require(any(event["type"] == "OutcomeReconciled" for event in final_evidence["events"]), "reconciliation event is missing from final SQLite evidence")
    require(final_evidence["run"]["faultInjected"] == "", "reconcile did not clear the persisted fault injection")
    return {
        "runId": run_id,
        "fault": fault,
        "waiting": waiting,
        "checkpointAtFault": checkpoint_at_fault,
        "pausedAtFault": paused,
        "unknownAfterRestart": unknown,
        "unknownAfterIdle": after_idle,
        "unknownPersistenceEvidence": unknown_after_idle_evidence,
        "succeeded": succeeded,
        "finalPersistenceEvidence": final_evidence,
    }


def approve_faulted_run(
    case: Path,
    supervisor: HostSupervisor,
    fault: str,
) -> tuple[subprocess.Popen[bytes], str, str, dict[str, Any], Path]:
    process, base_url = start_case_host(case, supervisor)
    run_id = create_run(base_url, fault=fault)["id"]
    waiting = wait_run(base_url, run_id, {"WaitingApproval"}, host=process)
    assert_waiting_approval(waiting)
    status, _ = post_json(
        base_url,
        f"/api/runs/{quote(run_id, safe='')}/decision",
        {"approved": True, "expectedHash": waiting["argumentsHash"]},
    )
    require(status == 202, f"{fault} approval returned HTTP {status}")
    marker = supervisor.state / "fault-ready"
    wait_for_file(marker, host=process)
    return process, base_url, run_id, waiting, marker


def case_staged_receipt_recovery(case: Path, supervisor: HostSupervisor, fault: str) -> dict[str, Any]:
    process, base_url, run_id, waiting, marker = approve_faulted_run(case, supervisor, fault)
    marker_text = marker.read_text(encoding="utf-8", errors="replace")
    operation_id = waiting["operationId"]
    staging_path = supervisor.state / "staging" / f"{operation_id}.json.tmp"
    final_path = supervisor.state / "receipts" / f"{operation_id}.json"
    require(staging_path.is_file(), f"{fault} did not create its staged receipt: {staging_path}")
    require(not final_path.exists(), f"{fault} published a receipt before its barrier: {final_path}")
    staged_before_restart = staging_path.read_bytes()
    require(bool(staged_before_restart), f"{fault} staged receipt is empty")
    if fault == "receipt-partial":
        try:
            json.loads(staged_before_restart)
        except json.JSONDecodeError:
            pass
        else:
            raise ProbeFailure("receipt-partial staging file contains complete JSON")
    else:
        try:
            staged_json = json.loads(staged_before_restart)
        except json.JSONDecodeError as error:
            raise ProbeFailure(f"receipt-flushed staging file is not valid JSON: {error}") from error
        expected_text = json.loads(waiting["argumentsJson"])["text"]
        require(staged_json == {"receiptId": operation_id, "text": expected_text},
                f"receipt-flushed staging file differs from the approved result: {staged_json}")
    paused = get_run(base_url, run_id)
    require(paused.get("receiptCount") == 0, f"staged receipt was exposed as final: {paused}")
    evidence_at_fault = read_db_evidence(supervisor.state, run_id)
    require(evidence_at_fault["ledger"] is not None and evidence_at_fault["ledger"]["state"] == "Executing",
            f"{fault} did not persist an Executing ledger row: {evidence_at_fault['ledger']}")
    supervisor.kill(process, force=True)

    restarted_process, restarted_url = start_case_host(case, supervisor)
    unknown = wait_run(restarted_url, run_id, {"UnknownOutcome"}, host=restarted_process)
    require(unknown.get("receiptCount") == 0, f"recovery exposed a staged receipt before reconciliation: {unknown}")
    staged_after_restart = staging_path.read_bytes()
    require(staged_after_restart == staged_before_restart, f"Host changed staged bytes before reconciliation for {fault}")
    unknown, unknown_evidence = assert_quiescent(restarted_url, supervisor.state, run_id, expected_receipts=0)

    status, reconciled = post_json(restarted_url, f"/api/runs/{quote(run_id, safe='')}/reconcile")
    require(status == 202 and isinstance(reconciled, dict), f"{fault} reconcile returned HTTP {status}: {reconciled}")
    succeeded = wait_run(restarted_url, run_id, {"Succeeded"}, host=restarted_process)
    assert_success(succeeded, before=waiting)
    final_evidence = read_db_evidence(supervisor.state, run_id)
    assert_final_receipt_evidence(final_evidence, recovered_session=True)
    require(final_evidence["run"]["operationId"] == operation_id, "recovery changed the approved operation id")
    require(len(final_evidence["receiptArtifacts"]["receipts"]) == 1, "recovery produced more than one final receipt file")
    require(not final_evidence["receiptArtifacts"]["staging"], "recovery left a staged receipt behind")
    if fault == "receipt-partial":
        archived = [item for item in final_evidence["receiptArtifacts"]["quarantine"] if item["sha256"] == hashlib.sha256(staged_before_restart).hexdigest()]
        require(len(archived) == 1, "partial receipt was not preserved exactly once in quarantine")
        require(count_events(final_evidence, "ToolExecuting") == 2,
                "partial receipt recovery did not record exactly one safe re-execution")
    else:
        require(not final_evidence["receiptArtifacts"]["quarantine"], "valid flushed receipt was incorrectly quarantined")
        require(count_events(final_evidence, "ToolExecuting") == 1,
                "flushed receipt recovery executed the tool again instead of reusing it")
        require(count_events(final_evidence, "ToolResultReused") >= 1,
                "flushed receipt recovery did not record cached result reuse")
    return {
        "runId": run_id,
        "fault": fault,
        "faultMarker": marker_text,
        "waiting": waiting,
        "pausedAtFault": paused,
        "evidenceAtFault": evidence_at_fault,
        "stagedReceiptBeforeRestartBase64": base64.b64encode(staged_before_restart).decode("ascii"),
        "unknownAfterRestart": unknown,
        "unknownPersistenceEvidence": unknown_evidence,
        "succeeded": succeeded,
        "finalPersistenceEvidence": final_evidence,
    }


def case_corrupt_final_receipt(case: Path, supervisor: HostSupervisor, corruption: str) -> dict[str, Any]:
    process, base_url, run_id, waiting, marker = approve_faulted_run(case, supervisor, "after-effect")
    marker_text = marker.read_text(encoding="utf-8", errors="replace")
    operation_id = waiting["operationId"]
    receipt_path = supervisor.state / "receipts" / f"{operation_id}.json"
    require(receipt_path.is_file(), f"after-effect did not publish a final receipt: {receipt_path}")
    valid_bytes = receipt_path.read_bytes()
    evidence_at_fault = read_db_evidence(supervisor.state, run_id)
    require(evidence_at_fault["receiptFile"] is not None, "after-effect receipt is not parseable before corruption")
    require(evidence_at_fault["ledger"] is not None and evidence_at_fault["ledger"]["state"] == "Executing",
            f"after-effect ledger was not Executing: {evidence_at_fault['ledger']}")
    supervisor.kill(process, force=True)

    restarted_process, restarted_url = start_case_host(case, supervisor)
    unknown = wait_run(restarted_url, run_id, {"UnknownOutcome"}, host=restarted_process)
    unknown, unknown_evidence = assert_quiescent(restarted_url, supervisor.state, run_id, expected_receipts=1)
    if corruption == "bad-json":
        corrupted_bytes = b'{"receiptId":'
    else:
        value = json.loads(valid_bytes)
        value["text"] += " [conflict]"
        corrupted_bytes = json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    write_bytes_and_flush(receipt_path, corrupted_bytes)
    evidence_after_corruption = read_db_evidence(supervisor.state, run_id)

    status, rejected = post_json(restarted_url, f"/api/runs/{quote(run_id, safe='')}/reconcile")
    require(status == 409, f"{corruption} receipt reconcile returned HTTP {status}, expected 409: {rejected}")
    quarantined = get_run(restarted_url, run_id)
    quarantined_evidence = read_db_evidence(supervisor.state, run_id)
    require(quarantined.get("status") == "UnknownOutcome", f"corrupt receipt did not remain blocked: {quarantined}")
    require(quarantined.get("error") == "receipt_corrupt_or_conflicting", f"corrupt receipt has wrong recovery error: {quarantined}")
    require(quarantined_evidence["ledger"] is not None and quarantined_evidence["ledger"]["state"] == "Quarantined",
            f"corrupt receipt did not quarantine its operation: {quarantined_evidence['ledger']}")
    require(quarantined.get("receiptCount") == 0, f"quarantined receipt remains exposed as final: {quarantined}")
    require(not quarantined_evidence["receiptArtifacts"]["receipts"], "corrupt final receipt was not moved out of receipts/")
    archived = [item for item in quarantined_evidence["receiptArtifacts"]["quarantine"]
                if item["sha256"] == hashlib.sha256(corrupted_bytes).hexdigest()]
    require(len(archived) == 1, "corrupt receipt bytes were not preserved exactly once in quarantine")
    require(count_events(quarantined_evidence, "ToolExecuting") == 1, "quarantining a corrupt receipt caused a tool replay")
    require(count_events(quarantined_evidence, "ToolCompleted") == 0, "corrupt receipt was incorrectly marked completed")

    before_repeat = quarantined_evidence
    before_repeat_run = quarantined
    repeat_status, repeated = post_json(restarted_url, f"/api/runs/{quote(run_id, safe='')}/reconcile")
    require(repeat_status == 409, f"repeated {corruption} reconcile returned HTTP {repeat_status}: {repeated}")
    after_repeat_run = get_run(restarted_url, run_id)
    after_repeat = read_db_evidence(supervisor.state, run_id)
    require(after_repeat_run == before_repeat_run, f"repeated reconcile changed Run state: {before_repeat_run} -> {after_repeat_run}")
    require(after_repeat == before_repeat, "repeated reconcile wrote new database or quarantine evidence")
    require(after_repeat_run.get("toolCalls") == unknown.get("toolCalls"), "corrupt receipt path triggered another tool call")
    simulated_quarantine_recovery: dict[str, Any] | None = None
    if corruption == "bad-json":
        archived_entry = next(
            (item for item in quarantined_evidence["receiptArtifacts"]["quarantine"]
             if item["sha256"] == hashlib.sha256(corrupted_bytes).hexdigest()),
            None,
        )
        require(archived_entry is not None, "cannot simulate the committed-quarantine-before-move window without archived bytes")
        archived_path = supervisor.state / archived_entry["path"]
        require(archived_path.is_file(), f"quarantined receipt evidence is missing from disk: {archived_path}")
        archived_path.replace(receipt_path)
        evidence_before_recovery = read_db_evidence(supervisor.state, run_id)
        run_before_recovery = get_run(restarted_url, run_id)
        require(evidence_before_recovery["ledger"] is not None and evidence_before_recovery["ledger"]["state"] == "Quarantined",
                f"simulated pre-move state lost its Quarantined ledger: {evidence_before_recovery['ledger']}")
        require(evidence_before_recovery["run"]["error"] == "receipt_corrupt_or_conflicting",
                f"simulated pre-move state lost its blocked outcome: {evidence_before_recovery['run']}")
        require(evidence_before_recovery["receiptFileError"] is not None and
                len(evidence_before_recovery["receiptArtifacts"]["receipts"]) == 1 and
                not evidence_before_recovery["receiptArtifacts"]["quarantine"],
                f"simulated crash window is not Quarantined with the corrupt file still at its final name: {evidence_before_recovery}")
        require(run_before_recovery.get("status") == "UnknownOutcome" and run_before_recovery.get("receiptCount") == 0,
                f"simulated corrupt final receipt was exposed as a valid result: {run_before_recovery}")

        supervisor.kill(restarted_process, force=True)
        recovery_process, recovery_url = start_case_host(case, supervisor)
        recovered_run = wait_run(recovery_url, run_id, {"UnknownOutcome"}, host=recovery_process)
        recovered_evidence = read_db_evidence(supervisor.state, run_id)
        require(recovered_run.get("receiptCount") == 0 and recovered_run.get("error") == "receipt_corrupt_or_conflicting",
                f"restart did not preserve the blocked corrupt-receipt outcome: {recovered_run}")
        require(recovered_evidence["ledger"] is not None and recovered_evidence["ledger"]["state"] == "Quarantined",
                f"restart changed the Quarantined ledger: {recovered_evidence['ledger']}")
        require(not recovered_evidence["receiptArtifacts"]["receipts"],
                "restart did not move the remaining corrupt final file out of receipts/")
        recovered_archive = [item for item in recovered_evidence["receiptArtifacts"]["quarantine"]
                            if item["sha256"] == hashlib.sha256(corrupted_bytes).hexdigest()]
        require(len(recovered_archive) == 1, "restart did not preserve the simulated corrupt receipt exactly once")
        require(count_events(recovered_evidence, "ToolExecuting") == 1,
                "restart after the simulated quarantine commit replayed the tool")
        repeat_recovery_status, repeat_recovery_body = post_json(
            recovery_url, f"/api/runs/{quote(run_id, safe='')}/reconcile"
        )
        require(repeat_recovery_status == 409,
                f"reconcile after simulated quarantine recovery returned HTTP {repeat_recovery_status}: {repeat_recovery_body}")
        stable_recovery_evidence = read_db_evidence(supervisor.state, run_id)
        require(stable_recovery_evidence == recovered_evidence,
                "repeated reconcile changed evidence after simulated quarantine recovery")
        simulated_quarantine_recovery = {
            "simulation": "Restored the already committed Quarantined row's malformed bytes to the final receipt path before restart; this models, but does not kill the Host inside, the quarantine-move window.",
            "runBeforeRecovery": run_before_recovery,
            "evidenceBeforeRecovery": evidence_before_recovery,
            "runAfterRecovery": recovered_run,
            "evidenceAfterRecovery": recovered_evidence,
            "rejectedRepeatedReconcile": {"status": repeat_recovery_status, "body": repeat_recovery_body},
            "evidenceAfterRepeatedReconcile": stable_recovery_evidence,
        }
    return {
        "runId": run_id,
        "fault": "after-effect",
        "corruption": corruption,
        "faultMarker": marker_text,
        "waiting": waiting,
        "evidenceAtFault": evidence_at_fault,
        "unknownAfterRestart": unknown,
        "unknownPersistenceEvidence": unknown_evidence,
        "evidenceAfterCorruption": evidence_after_corruption,
        "corruptedReceiptBase64": base64.b64encode(corrupted_bytes).decode("ascii"),
        "firstReconcile": {"status": status, "body": rejected},
        "quarantinedRun": quarantined,
        "quarantinedEvidence": quarantined_evidence,
        "repeatedReconcile": {"status": repeat_status, "body": repeated},
        "evidenceAfterRepeatedReconcile": after_repeat,
        "simulatedQuarantineMoveRecovery": simulated_quarantine_recovery,
    }


def case_after_ledger_restart(case: Path, supervisor: HostSupervisor) -> dict[str, Any]:
    process, base_url, run_id, waiting, marker = approve_faulted_run(case, supervisor, "after-ledger")
    marker_text = marker.read_text(encoding="utf-8", errors="replace")
    paused = get_run(base_url, run_id)
    evidence_at_fault = read_db_evidence(supervisor.state, run_id)
    require(evidence_at_fault["ledger"] is not None and evidence_at_fault["ledger"]["state"] == "Succeeded",
            f"after-ledger barrier did not persist success: {evidence_at_fault['ledger']}")
    require(paused.get("receiptCount") == 1, f"after-ledger barrier lost its final receipt: {paused}")
    require(count_events(evidence_at_fault, "ToolExecuting") == 1 and count_events(evidence_at_fault, "ToolCompleted") == 1,
            f"after-ledger barrier has unexpected tool event counts: {evidence_at_fault['events']}")
    receipt_before = artifact_by_path(evidence_at_fault, f"receipts/{waiting['operationId']}.json")
    require(receipt_before is not None, "after-ledger receipt bytes are missing from evidence")
    supervisor.kill(process, force=True)

    restarted_process, restarted_url = start_case_host(case, supervisor)
    succeeded = wait_run(restarted_url, run_id, {"Succeeded"}, host=restarted_process)
    assert_success(succeeded, before=paused)
    final_evidence = read_db_evidence(supervisor.state, run_id)
    assert_final_receipt_evidence(final_evidence, recovered_session=True)
    require(count_events(final_evidence, "ToolExecuting") == 1, "Host restart re-executed an already successful tool")
    require(count_events(final_evidence, "ToolCompleted") == 1, "Host restart duplicated ToolCompleted")
    require(count_events(final_evidence, "ToolResultReused") >= 1, "Host restart did not reuse the stored tool result")
    require(count_events(final_evidence, "RunSucceeded") == 1, "Host restart emitted multiple RunSucceeded events")
    require(count_events(final_evidence, "OutcomeReconciled") == 0, "after-ledger recovery incorrectly required reconciliation")
    receipt_after = artifact_by_path(final_evidence, f"receipts/{waiting['operationId']}.json")
    require(receipt_after == receipt_before, "Host restart changed the already committed receipt bytes")
    return {
        "runId": run_id,
        "fault": "after-ledger",
        "faultMarker": marker_text,
        "waiting": waiting,
        "pausedAtFault": paused,
        "evidenceAtFault": evidence_at_fault,
        "succeeded": succeeded,
        "finalPersistenceEvidence": final_evidence,
    }


def assert_cached_finalization(
    before: dict[str, Any],
    after: dict[str, Any],
    evidence: dict[str, Any],
    operation_id: str,
) -> None:
    require(after.get("status") == "Succeeded", f"Run finalization did not succeed: {after}")
    require(after.get("receiptCount") == 1, f"finalization did not preserve exactly one receipt: {after}")
    require(after.get("workerPid") != before.get("workerPid"), f"finalization reused the prior Worker process: {before}, {after}")
    require(evidence["run"]["operationId"] == operation_id, "finalization changed the original operation id")
    require(evidence["ledger"] is not None and evidence["ledger"]["state"] == "Succeeded", "finalization lost successful tool ledger state")
    require(count_events(evidence, "ToolExecuting") == 1, "finalization repeated the external tool execution")
    require(count_events(evidence, "ToolCompleted") == 1, "finalization duplicated ToolCompleted")
    require(count_events(evidence, "ToolResultReused") >= 1, "finalization did not reuse the cached tool result")
    require(count_events(evidence, "RunSucceeded") == 1, "finalization did not commit exactly one successful Run result")


def case_finalization_resume(case: Path, supervisor: HostSupervisor) -> dict[str, Any]:
    supervisor.worker_timeout_seconds = 3
    process, base_url, run_id, waiting, marker = approve_faulted_run(case, supervisor, "before-final-commit")
    marker_text = marker.read_text(encoding="utf-8", errors="replace")
    worker_pid = get_run(base_url, run_id).get("workerPid")
    require(isinstance(worker_pid, int), "before-final-commit did not persist the Worker PID")
    require(not HostSupervisor._pid_exists(worker_pid), f"Worker PID {worker_pid} was still running at finalization barrier")
    evidence_at_fault = read_db_evidence(supervisor.state, run_id)
    require(evidence_at_fault["run"]["faultInjected"] == "", "before-final-commit fault was not cleared before its barrier")
    require(evidence_at_fault["ledger"] is not None and evidence_at_fault["ledger"]["state"] == "Succeeded",
            f"finalization barrier lacks a committed tool result: {evidence_at_fault['ledger']}")
    receipt_before = artifact_by_path(evidence_at_fault, f"receipts/{waiting['operationId']}.json")
    require(receipt_before is not None, "finalization barrier lacks receipt bytes/hash evidence")

    pending = wait_run(base_url, run_id, {"FinalizationPending"}, timeout=STAGE_TIMEOUT, host=process)
    pending_evidence = read_db_evidence(supervisor.state, run_id)
    require(pending_evidence["run"]["faultInjected"] == "", "timeout re-armed the finalization fault")
    require(pending_evidence["ledger"]["state"] == "Succeeded", "timeout changed the committed tool ledger")
    status, resumed = post_json(base_url, f"/api/runs/{quote(run_id, safe='')}/resume")
    require(status == 202 and isinstance(resumed, dict), f"FinalizationPending resume returned HTTP {status}: {resumed}")
    succeeded = wait_run(base_url, run_id, {"Succeeded"}, host=process)
    final_evidence = read_db_evidence(supervisor.state, run_id)
    assert_final_receipt_evidence(final_evidence, recovered_session=True)
    assert_cached_finalization(pending, succeeded, final_evidence, waiting["operationId"])
    receipt_after = artifact_by_path(final_evidence, f"receipts/{waiting['operationId']}.json")
    require(receipt_after == receipt_before, "resume changed the committed receipt bytes/hash")

    repeat_status, repeated = post_json(base_url, f"/api/runs/{quote(run_id, safe='')}/resume")
    require(repeat_status == 409, f"resume outside FinalizationPending returned HTTP {repeat_status}: {repeated}")
    after_repeat = read_db_evidence(supervisor.state, run_id)
    require(after_repeat == final_evidence, "rejected duplicate resume wrote new database or receipt evidence")
    return {
        "runId": run_id,
        "fault": "before-final-commit",
        "timeoutSeconds": supervisor.worker_timeout_seconds,
        "faultMarker": marker_text,
        "waiting": waiting,
        "evidenceAtFault": evidence_at_fault,
        "finalizationPending": pending,
        "pendingEvidence": pending_evidence,
        "resumeResponse": {"status": status, "body": resumed},
        "succeeded": succeeded,
        "finalPersistenceEvidence": final_evidence,
        "rejectedRepeatResume": {"status": repeat_status, "body": repeated},
        "evidenceAfterRejectedRepeatResume": after_repeat,
    }


def case_finalization_corrupt_receipt(case: Path, supervisor: HostSupervisor) -> dict[str, Any]:
    supervisor.worker_timeout_seconds = 3
    process, base_url, run_id, waiting, marker = approve_faulted_run(case, supervisor, "before-final-commit")
    marker_text = marker.read_text(encoding="utf-8", errors="replace")
    pending = wait_run(base_url, run_id, {"FinalizationPending"}, timeout=STAGE_TIMEOUT, host=process)
    require(pending.get("receiptCount") == 1, f"finalization timeout did not retain its committed receipt: {pending}")
    operation_id = waiting["operationId"]
    receipt_path = supervisor.state / "receipts" / f"{operation_id}.json"
    require(receipt_path.is_file(), f"FinalizationPending receipt is missing: {receipt_path}")
    valid_bytes = receipt_path.read_bytes()
    pending_evidence = read_db_evidence(supervisor.state, run_id)
    require(pending_evidence["run"]["status"] == "FinalizationPending" and
            pending_evidence["ledger"] is not None and pending_evidence["ledger"]["state"] == "Succeeded",
            f"finalization corruption case did not begin from a committed tool result: {pending_evidence}")
    require(count_events(pending_evidence, "ToolExecuting") == 1 and
            count_events(pending_evidence, "ToolCompleted") == 1,
            f"FinalizationPending evidence has unexpected tool event counts: {pending_evidence['events']}")

    corrupted_bytes = b'{"receiptId":'
    write_bytes_and_flush(receipt_path, corrupted_bytes)
    corrupted_evidence = read_db_evidence(supervisor.state, run_id)
    corrupted_run = get_run(base_url, run_id)
    require(corrupted_run.get("receiptCount") == 0,
            f"malformed cached receipt still counted as a completed receipt: {corrupted_run}")
    require(corrupted_evidence["receiptFile"] is None and corrupted_evidence["receiptFileError"],
            f"malformed cached receipt was not captured as invalid raw evidence: {corrupted_evidence}")
    require(corrupted_evidence["ledger"] == pending_evidence["ledger"],
            "receipt corruption changed the already successful operation ledger before resume")

    resume_status, resume_response = post_json(base_url, f"/api/runs/{quote(run_id, safe='')}/resume")
    require(resume_status == 202 and isinstance(resume_response, dict),
            f"FinalizationPending resume with corrupt receipt returned HTTP {resume_status}: {resume_response}")
    unknown = wait_run(base_url, run_id, {"UnknownOutcome"}, host=process)
    quarantined_evidence = read_db_evidence(supervisor.state, run_id)
    require(unknown.get("status") == "UnknownOutcome" and unknown.get("error") == "receipt_corrupt_or_conflicting",
            f"corrupt cached receipt did not block finalization: {unknown}")
    require(unknown.get("receiptCount") == 0, f"quarantined receipt remained exposed as completed: {unknown}")
    require(quarantined_evidence["run"]["status"] == "UnknownOutcome" and
            quarantined_evidence["run"]["error"] == "receipt_corrupt_or_conflicting",
            f"corrupt cached receipt block was not persisted: {quarantined_evidence['run']}")
    require(quarantined_evidence["ledger"] is not None and quarantined_evidence["ledger"]["state"] == "Quarantined",
            f"corrupt cached receipt did not quarantine the successful ledger: {quarantined_evidence['ledger']}")
    require(not quarantined_evidence["receiptArtifacts"]["receipts"],
            "corrupt cached receipt remained in the published receipts directory")
    archived = [item for item in quarantined_evidence["receiptArtifacts"]["quarantine"]
                if item["sha256"] == hashlib.sha256(corrupted_bytes).hexdigest()]
    require(len(archived) == 1, "corrupt cached receipt bytes were not preserved exactly once in quarantine")
    require(count_events(quarantined_evidence, "ToolExecuting") == 1,
            "resume with a corrupt cached receipt re-executed the tool")
    require(count_events(quarantined_evidence, "ToolCompleted") == 1,
            "resume with a corrupt cached receipt changed the previously committed effect")
    require(count_events(quarantined_evidence, "ToolResultReused") == 0,
            "Host reported a cached-result reuse despite rejecting the corrupt receipt")
    require(count_events(quarantined_evidence, "RunSucceeded") == 0,
            "Host finalized the Run despite rejecting the corrupt receipt")

    repeat_resume_status, repeat_resume = post_json(base_url, f"/api/runs/{quote(run_id, safe='')}/resume")
    require(repeat_resume_status == 409,
            f"repeated resume after receipt quarantine returned HTTP {repeat_resume_status}: {repeat_resume}")
    repeat_reconcile_status, repeat_reconcile = post_json(base_url, f"/api/runs/{quote(run_id, safe='')}/reconcile")
    require(repeat_reconcile_status == 409,
            f"reconcile after receipt quarantine returned HTTP {repeat_reconcile_status}: {repeat_reconcile}")
    after_rejections_run = get_run(base_url, run_id)
    after_rejections = read_db_evidence(supervisor.state, run_id)
    require(after_rejections_run == unknown, "rejected resume/reconcile changed the blocked Run state")
    require(after_rejections == quarantined_evidence,
            "rejected resume/reconcile changed SQLite, receipt, or quarantine evidence")
    return {
        "runId": run_id,
        "fault": "before-final-commit",
        "faultMarker": marker_text,
        "waiting": waiting,
        "finalizationPending": pending,
        "pendingEvidence": pending_evidence,
        "originalReceiptBase64": base64.b64encode(valid_bytes).decode("ascii"),
        "corruptedReceiptBase64": base64.b64encode(corrupted_bytes).decode("ascii"),
        "evidenceAfterCorruption": corrupted_evidence,
        "resumeResponse": {"status": resume_status, "body": resume_response},
        "unknownOutcome": unknown,
        "quarantinedEvidence": quarantined_evidence,
        "rejectedRepeatResume": {"status": repeat_resume_status, "body": repeat_resume},
        "rejectedReconcile": {"status": repeat_reconcile_status, "body": repeat_reconcile},
        "evidenceAfterRejectedRecovery": after_rejections,
    }


def case_worker_dies_after_ledger(case: Path, supervisor: HostSupervisor) -> dict[str, Any]:
    supervisor.worker_timeout_seconds = 3
    process, base_url, run_id, waiting, marker = approve_faulted_run(case, supervisor, "after-ledger")
    marker_text = marker.read_text(encoding="utf-8", errors="replace")
    paused = get_run(base_url, run_id)
    worker_pid = paused.get("workerPid")
    require(isinstance(worker_pid, int), f"after-ledger fault has no Worker PID: {paused}")
    evidence_at_fault = read_db_evidence(supervisor.state, run_id)
    require(evidence_at_fault["ledger"] is not None and evidence_at_fault["ledger"]["state"] == "Succeeded",
            f"Worker-death case did not begin after ledger success: {evidence_at_fault['ledger']}")
    receipt_before = artifact_by_path(evidence_at_fault, f"receipts/{waiting['operationId']}.json")
    require(receipt_before is not None, "Worker-death case lacks receipt bytes/hash evidence")
    supervisor.kill_worker(worker_pid, process)
    pending = wait_run(base_url, run_id, {"FinalizationPending"}, timeout=STAGE_TIMEOUT, host=process)
    pending_evidence = read_db_evidence(supervisor.state, run_id)
    require(pending_evidence["ledger"]["state"] == "Succeeded", "Worker timeout changed the committed tool result")
    require(pending.get("receiptCount") == 1, f"Worker timeout lost or duplicated the receipt: {pending}")
    require(count_events(pending_evidence, "ToolExecuting") == 1, "Worker timeout retried the external tool")
    require(count_events(pending_evidence, "ToolCompleted") == 1, "Worker timeout duplicated ToolCompleted")
    require(count_events(pending_evidence, "RunSucceeded") == 0, "Worker timeout incorrectly committed Run success")
    supervisor.kill(process, force=True)

    restarted_process, restarted_url = start_case_host(case, supervisor)
    succeeded = wait_run(restarted_url, run_id, {"Succeeded"}, host=restarted_process)
    assert_success(succeeded, before=pending)
    final_evidence = read_db_evidence(supervisor.state, run_id)
    assert_final_receipt_evidence(final_evidence, recovered_session=True)
    assert_cached_finalization(pending, succeeded, final_evidence, waiting["operationId"])
    receipt_after = artifact_by_path(final_evidence, f"receipts/{waiting['operationId']}.json")
    require(receipt_after == receipt_before, "Host restart changed the receipt after Worker death")
    return {
        "runId": run_id,
        "fault": "after-ledger",
        "failure": "worker-killed-and-timeout",
        "timeoutSeconds": supervisor.worker_timeout_seconds,
        "faultMarker": marker_text,
        "waiting": waiting,
        "pausedAtFault": paused,
        "killedWorkerPid": worker_pid,
        "evidenceAtFault": evidence_at_fault,
        "finalizationPending": pending,
        "pendingEvidence": pending_evidence,
        "succeededAfterHostRestart": succeeded,
        "finalPersistenceEvidence": final_evidence,
    }


def assert_cancelled_without_effect(run: dict[str, Any], evidence: dict[str, Any]) -> None:
    require(run.get("status") == "Cancelled", f"run was not Cancelled: {run}")
    require(run.get("receiptCount") == 0, f"cancelled run exposed a receipt: {run}")
    require(evidence["run"]["status"] == "Cancelled" and evidence["run"]["cancelRequested"],
            f"cancel intent or terminal state was not persisted: {evidence['run']}")
    require(evidence["ledger"] is None or evidence["ledger"]["state"] == "Cancelled",
            f"cancelled run has a live/successful ledger operation: {evidence['ledger']}")
    require(count_events(evidence, "ToolExecuting") == 0,
            f"cancellation before approval dispatched the side effect: {evidence['events']}")
    require(count_events(evidence, "ToolCompleted") == 0,
            f"cancellation before approval completed a side effect: {evidence['events']}")
    require(not any(evidence["receiptArtifacts"].values()),
            f"cancellation before approval left receipt artifacts: {evidence['receiptArtifacts']}")


def case_cancel_waiting_approval(case: Path, supervisor: HostSupervisor) -> dict[str, Any]:
    process, base_url = start_case_host(case, supervisor)
    run_id = create_run(base_url)["id"]
    waiting = wait_run(base_url, run_id, {"WaitingApproval"}, host=process)
    assert_waiting_approval(waiting)

    started = time.monotonic()
    status, response = post_json(base_url, f"/api/runs/{quote(run_id, safe='')}/cancel")
    elapsed = time.monotonic() - started
    require(status == 202 and isinstance(response, dict), f"WaitingApproval cancellation returned HTTP {status}: {response}")
    require(elapsed < 2.0, f"cancellation from initial WaitingApproval blocked for {elapsed:.2f}s")
    cancelled = wait_run(base_url, run_id, {"Cancelled"}, host=process)
    evidence = read_db_evidence(supervisor.state, run_id)
    assert_cancelled_without_effect(cancelled, evidence)

    decision_status, decision_body = post_json(
        base_url,
        f"/api/runs/{quote(run_id, safe='')}/decision",
        {"approved": True, "expectedHash": waiting["argumentsHash"]},
    )
    require(decision_status == 409, f"approval after cancellation returned HTTP {decision_status}: {decision_body}")
    resume_status, resume_body = post_json(base_url, f"/api/runs/{quote(run_id, safe='')}/resume")
    require(resume_status == 409, f"resume after cancellation returned HTTP {resume_status}: {resume_body}")
    return {
        "runId": run_id,
        "waiting": waiting,
        "cancelResponse": {"status": status, "body": response, "elapsedSeconds": elapsed},
        "cancelled": cancelled,
        "finalPersistenceEvidence": evidence,
        "rejectedApproval": {"status": decision_status, "body": decision_body},
        "rejectedResume": {"status": resume_status, "body": resume_body},
    }


def case_cancel_persisted_restart(case: Path, supervisor: HostSupervisor) -> dict[str, Any]:
    process, base_url = start_case_host(case, supervisor)
    run_id = create_run(base_url, fault="cancel-persisted")["id"]
    waiting = wait_run(base_url, run_id, {"WaitingApproval"}, host=process)
    assert_waiting_approval(waiting)
    marker = supervisor.state / "fault-ready"
    cancel_result: dict[str, Any] = {}

    def send_cancel() -> None:
        try:
            cancel_result["response"] = post_json(base_url, f"/api/runs/{quote(run_id, safe='')}/cancel")
        except Exception as error:
            cancel_result["error"] = f"{type(error).__name__}: {error}"

    request_thread = threading.Thread(target=send_cancel, name=f"cancel-{run_id}", daemon=True)
    request_thread.start()
    wait_for_file(marker, host=process)
    marker_text = marker.read_text(encoding="utf-8", errors="replace")
    require(marker_text == "cancel-persisted", f"cancellation stopped at the wrong fault barrier: {marker_text!r}")
    evidence_at_barrier = read_db_evidence(supervisor.state, run_id)
    require(evidence_at_barrier["run"]["status"] == "Cancelling" and evidence_at_barrier["run"]["cancelRequested"],
            f"cancel API reached its barrier before persisting cancel intent: {evidence_at_barrier['run']}")
    require(count_events(evidence_at_barrier, "RunCancelRequested") == 1,
            f"cancel intent event is missing or duplicated at the barrier: {evidence_at_barrier['events']}")
    require(evidence_at_barrier["ledger"] is None, f"unapproved run created a tool ledger before cancellation: {evidence_at_barrier['ledger']}")
    require(not any(evidence_at_barrier["receiptArtifacts"].values()),
            f"unapproved run created receipt artifacts before cancellation: {evidence_at_barrier['receiptArtifacts']}")

    supervisor.kill(process, force=True)
    request_thread.join(timeout=HTTP_TIMEOUT + 1.0)
    require(not request_thread.is_alive(), "cancel HTTP request remained blocked after Host termination")
    require("response" not in cancel_result and "error" in cancel_result,
            f"cancel HTTP request unexpectedly completed across the persisted-cancellation crash window: {cancel_result}")

    restarted_process, restarted_url = start_case_host(case, supervisor)
    cancelled = wait_run(restarted_url, run_id, {"Cancelled"}, host=restarted_process)
    evidence = read_db_evidence(supervisor.state, run_id)
    assert_cancelled_without_effect(cancelled, evidence)
    require(count_events(evidence, "CancellationRestored") == 1,
            f"restart did not record exactly one restored cancellation: {evidence['events']}")
    require(count_events(evidence, "WorkerStarted") == 1,
            f"restart dispatched another Worker for a persisted cancellation: {evidence['events']}")
    require(count_events(evidence, "ApprovalGranted") == 0, "cancelled waiting approval was treated as approved")

    decision_status, decision_body = post_json(
        restarted_url,
        f"/api/runs/{quote(run_id, safe='')}/decision",
        {"approved": True, "expectedHash": waiting["argumentsHash"]},
    )
    require(decision_status == 409, f"approval after restored cancellation returned HTTP {decision_status}: {decision_body}")
    resume_status, resume_body = post_json(restarted_url, f"/api/runs/{quote(run_id, safe='')}/resume")
    require(resume_status == 409, f"resume after restored cancellation returned HTTP {resume_status}: {resume_body}")
    repeat_cancel_status, repeat_cancel_body = post_json(restarted_url, f"/api/runs/{quote(run_id, safe='')}/cancel")
    require(repeat_cancel_status == 409,
            f"duplicate cancellation after restore returned HTTP {repeat_cancel_status}: {repeat_cancel_body}")
    return {
        "runId": run_id,
        "fault": "cancel-persisted",
        "waiting": waiting,
        "faultMarker": marker_text,
        "cancelRequestAfterHostTermination": cancel_result,
        "evidenceAtBarrier": evidence_at_barrier,
        "cancelledAfterRestart": cancelled,
        "finalPersistenceEvidence": evidence,
        "rejectedApproval": {"status": decision_status, "body": decision_body},
        "rejectedResume": {"status": resume_status, "body": resume_body},
        "rejectedRepeatedCancel": {"status": repeat_cancel_status, "body": repeat_cancel_body},
    }


def case_cancel_reconcile(case: Path, supervisor: HostSupervisor, fault: str) -> dict[str, Any]:
    process, base_url, run_id, waiting, marker = approve_faulted_run(case, supervisor, fault)
    marker_text = marker.read_text(encoding="utf-8", errors="replace")
    expected_receipts = 1 if fault == "after-effect" else 0
    operation_id = waiting["operationId"]
    paused = get_run(base_url, run_id)
    require(paused.get("receiptCount") == expected_receipts,
            f"{fault} cancellation barrier has unexpected receipt count: {paused}")
    evidence_at_barrier = read_db_evidence(supervisor.state, run_id)
    require(evidence_at_barrier["ledger"] is not None and evidence_at_barrier["ledger"]["state"] == "Executing",
            f"{fault} cancellation barrier lacks an Executing ledger: {evidence_at_barrier['ledger']}")
    if fault == "after-effect":
        require(evidence_at_barrier["receiptFile"] is not None,
                "after-effect cancellation barrier lacks a complete published receipt")
        receipt_before_reconcile = artifact_by_path(evidence_at_barrier, f"receipts/{operation_id}.json")
        require(receipt_before_reconcile is not None, "after-effect cancellation barrier lacks raw receipt evidence")
    else:
        receipt_before_reconcile = None
        require(not evidence_at_barrier["receiptArtifacts"]["receipts"],
                "before-effect cancellation already published a final receipt")

    cancel_status, cancel_response = post_json(base_url, f"/api/runs/{quote(run_id, safe='')}/cancel")
    require(cancel_status == 202 and isinstance(cancel_response, dict),
            f"{fault} cancel returned HTTP {cancel_status}: {cancel_response}")
    unknown = wait_run(base_url, run_id, {"UnknownOutcome"}, host=process)
    require(unknown.get("receiptCount") == expected_receipts,
            f"{fault} cancellation changed receipts before reconciliation: {unknown}")
    require(cancel_response.get("status") in {"Cancelling", "UnknownOutcome"},
            f"{fault} cancellation response has unexpected state: {cancel_response}")
    unknown, unknown_evidence = assert_quiescent(
        base_url, supervisor.state, run_id, expected_receipts=expected_receipts
    )
    require(unknown_evidence["run"]["cancelRequested"], f"{fault} cancel intent was not persisted: {unknown_evidence['run']}")
    require(unknown_evidence["ledger"] is not None and unknown_evidence["ledger"]["state"] == "Executing",
            f"{fault} operation left Executing before reconciliation: {unknown_evidence['ledger']}")

    reconcile_status, reconciled = post_json(base_url, f"/api/runs/{quote(run_id, safe='')}/reconcile")
    require(reconcile_status == 202 and isinstance(reconciled, dict),
            f"{fault} cancelled reconcile returned HTTP {reconcile_status}: {reconciled}")
    cancelled = wait_run(base_url, run_id, {"Cancelled"}, host=process)
    final_evidence = read_db_evidence(supervisor.state, run_id)
    require(final_evidence["run"]["cancelRequested"], f"{fault} reconciliation cleared the cancellation intent")
    require(count_events(final_evidence, "OutcomeReconciled") == 1,
            f"{fault} reconciliation did not record exactly one outcome: {final_evidence['events']}")
    require(count_events(final_evidence, "ToolExecuting") == 1,
            f"{fault} cancellation replayed or skipped the original execution: {final_evidence['events']}")
    require(count_events(final_evidence, "ToolCompleted") == 0,
            f"{fault} cancellation incorrectly completed the Worker side of the operation")
    require(count_events(final_evidence, "RunSucceeded") == 0,
            f"{fault} cancellation incorrectly marked the Run successful")
    require(not final_evidence["receiptArtifacts"]["staging"],
            f"{fault} reconciliation left a staging receipt: {final_evidence['receiptArtifacts']}")
    if fault == "before-effect":
        require(cancelled.get("receiptCount") == 0 and final_evidence["ledger"]["state"] == "Cancelled",
                f"before-effect cancellation was not reconciled to Cancelled without a receipt: {cancelled}, {final_evidence['ledger']}")
        require(not final_evidence["receiptArtifacts"]["receipts"],
                "before-effect cancellation left a published receipt")
        require(final_evidence["ledger"]["result"] is None,
                "before-effect cancellation unexpectedly recorded a tool result")
    else:
        require(cancelled.get("receiptCount") == 1 and final_evidence["ledger"]["state"] == "Succeeded",
                f"after-effect cancellation did not retain the known successful effect: {cancelled}, {final_evidence['ledger']}")
        require(final_evidence["receiptFile"] == final_evidence["ledger"]["result"],
                "after-effect cancellation did not bind the cached result to the final receipt")
        receipt_after_reconcile = artifact_by_path(final_evidence, f"receipts/{operation_id}.json")
        require(receipt_after_reconcile == receipt_before_reconcile,
                "after-effect cancellation changed the already published receipt")
        require(len(final_evidence["receiptArtifacts"]["receipts"]) == 1,
                "after-effect cancellation left more than one final receipt")

    for action, endpoint, payload in (
        ("approval", "decision", {"approved": True, "expectedHash": waiting["argumentsHash"]}),
        ("resume", "resume", None),
        ("repeated cancel", "cancel", None),
    ):
        action_status, action_body = post_json(base_url, f"/api/runs/{quote(run_id, safe='')}/{endpoint}", payload)
        require(action_status == 409, f"{fault} {action} after cancellation returned HTTP {action_status}: {action_body}")
    after_rejections = read_db_evidence(supervisor.state, run_id)
    require(after_rejections == final_evidence, f"{fault} rejected cancellation follow-up changed persistence evidence")
    return {
        "runId": run_id,
        "fault": fault,
        "faultMarker": marker_text,
        "waiting": waiting,
        "pausedAtBarrier": paused,
        "evidenceAtBarrier": evidence_at_barrier,
        "cancelResponse": {"status": cancel_status, "body": cancel_response},
        "reconcileResponse": {"status": reconcile_status, "body": reconciled},
        "unknownAfterCancellation": unknown,
        "unknownPersistenceEvidence": unknown_evidence,
        "cancelledAfterReconcile": cancelled,
        "finalPersistenceEvidence": final_evidence,
        "evidenceAfterRejectedFollowUps": after_rejections,
    }


def case_executing_ledger_write_failure(case: Path, supervisor: HostSupervisor) -> dict[str, Any]:
    process, base_url = start_case_host(case, supervisor)
    run_id = create_run(base_url)["id"]
    waiting = wait_run(base_url, run_id, {"WaitingApproval"}, host=process)
    assert_waiting_approval(waiting)
    trigger_sql = """CREATE TRIGGER fail_executing_ledger_write
        BEFORE UPDATE OF state ON tool_operations
        WHEN NEW.state='Executing'
        BEGIN
          SELECT RAISE(ABORT, 'injected_executing_ledger_write_failure');
        END"""
    database = supervisor.state / "app.db"
    try:
        with sqlite3.connect(database, timeout=3.0) as db:
            db.execute(trigger_sql)
    except sqlite3.Error as error:
        raise ProbeFailure(f"could not inject the scoped SQLite ledger-write failure: {error}") from error

    status, approved = post_json(
        base_url,
        f"/api/runs/{quote(run_id, safe='')}/decision",
        {"approved": True, "expectedHash": waiting["argumentsHash"]},
    )
    require(status == 202 and isinstance(approved, dict), f"approval for SQLite-failure case returned HTTP {status}: {approved}")
    failed = wait_run(base_url, run_id, {"Failed"}, host=process)
    evidence = read_db_evidence(supervisor.state, run_id)
    require(evidence["run"]["status"] == "Failed", f"ledger-write failure did not fail the Run: {evidence['run']}")
    require(evidence["run"]["error"] and "injected_executing_ledger_write_failure" in evidence["run"]["error"],
            f"Run did not preserve the injected SQLite failure reason: {evidence['run']}")
    require(evidence["ledger"] is not None and evidence["ledger"]["state"] == "Approved",
            f"failed Executing ledger write advanced the operation: {evidence['ledger']}")
    require(count_events(evidence, "ToolExecuting") == 0 and count_events(evidence, "ToolCompleted") == 0,
            f"side-effect events were persisted despite the failed ledger transaction: {evidence['events']}")
    require(failed.get("receiptCount") == 0 and evidence["receiptFile"] is None,
            f"failed ledger transaction produced a receipt: {failed}, {evidence['receiptFile']}")
    require(not any(evidence["receiptArtifacts"].values()),
            f"failed ledger transaction left receipt artifacts: {evidence['receiptArtifacts']}")
    try:
        with sqlite3.connect(database.resolve().as_uri() + "?mode=ro", uri=True, timeout=3.0) as db:
            persisted_trigger = db.execute(
                "SELECT sql FROM sqlite_master WHERE type='trigger' AND name='fail_executing_ledger_write'"
            ).fetchone()
    except sqlite3.Error as error:
        raise ProbeFailure(f"could not verify the persisted SQLite failure trigger: {error}") from error
    require(persisted_trigger is not None and "injected_executing_ledger_write_failure" in persisted_trigger[0],
            "the evidence database does not contain the failure trigger used for this case")
    return {
        "runId": run_id,
        "waiting": waiting,
        "approvalResponse": {"status": status, "body": approved},
        "failed": failed,
        "triggerSql": persisted_trigger[0],
        "finalPersistenceEvidence": evidence,
    }


def case_corrupt_checkpoint(case: Path, supervisor: HostSupervisor) -> dict[str, Any]:
    process, base_url = start_case_host(case, supervisor)
    run_id = create_run(base_url)["id"]
    waiting = wait_run(base_url, run_id, {"WaitingApproval"}, host=process)
    assert_waiting_approval(waiting)
    malformed_session = "{malformed-session-checkpoint"
    database = supervisor.state / "app.db"
    try:
        with sqlite3.connect(database, timeout=3.0) as db:
            db.execute("UPDATE runs SET session=? WHERE id=?", (malformed_session, run_id))
    except sqlite3.Error as error:
        raise ProbeFailure(f"could not inject malformed session checkpoint: {error}") from error
    corrupted_evidence = read_db_evidence(supervisor.state, run_id)
    require(corrupted_evidence["run"]["session"]["present"] and not corrupted_evidence["run"]["session"]["validJson"],
            f"malformed session checkpoint was not persisted: {corrupted_evidence['run']['session']}")
    require(corrupted_evidence["run"]["approval"]["present"] and corrupted_evidence["run"]["approval"]["validJson"],
            f"checkpoint injection also damaged the independent approval data: {corrupted_evidence['run']['approval']}")

    status, approval_response = post_json(
        base_url,
        f"/api/runs/{quote(run_id, safe='')}/decision",
        {"approved": True, "expectedHash": waiting["argumentsHash"]},
    )
    require(status == 202 and isinstance(approval_response, dict),
            f"approval with malformed checkpoint returned HTTP {status}: {approval_response}")
    failed = wait_run(base_url, run_id, {"Failed"}, host=process)
    evidence = read_db_evidence(supervisor.state, run_id)
    require(evidence["run"]["status"] == "Failed" and evidence["run"]["error"],
            f"malformed checkpoint was not surfaced as a persisted run failure: {evidence['run']}")
    require(evidence["run"]["session"]["present"] and not evidence["run"]["session"]["validJson"],
            f"malformed checkpoint evidence changed unexpectedly: {evidence['run']['session']}")
    require(evidence["ledger"] is not None and evidence["ledger"]["state"] == "Approved",
            f"invalid checkpoint began executing the approved operation: {evidence['ledger']}")
    require(count_events(evidence, "WorkerStarted") == 1 and failed.get("workerPid") == waiting.get("workerPid"),
            f"Host started a Worker with the malformed checkpoint: {evidence['events']}, {failed}")
    require(count_events(evidence, "ToolExecuting") == 0 and count_events(evidence, "ToolCompleted") == 0,
            f"invalid checkpoint reached side-effect execution: {evidence['events']}")
    require(failed.get("receiptCount") == 0 and evidence["receiptFile"] is None,
            f"invalid checkpoint produced a receipt: {failed}, {evidence['receiptFile']}")
    require(not any(evidence["receiptArtifacts"].values()),
            f"invalid checkpoint left receipt artifacts: {evidence['receiptArtifacts']}")
    return {
        "runId": run_id,
        "waiting": waiting,
        "malformedSessionCheckpoint": malformed_session,
        "evidenceBeforeApproval": corrupted_evidence,
        "approvalResponse": {"status": status, "body": approval_response},
        "failed": failed,
        "finalPersistenceEvidence": evidence,
    }


def case_duplicate_host_lock(case: Path, supervisor: HostSupervisor) -> dict[str, Any]:
    original_process, original_url = start_case_host(case, supervisor)
    status, initial_health = get_json(original_url, "/health")
    require(status == 200 and isinstance(initial_health, dict), f"original Host health failed before lock probe: {initial_health}")
    require(initial_health.get("processId") == original_process.pid, f"original Host health identifies another process: {initial_health}")

    second_start_error: str | None = None
    try:
        second_process, second_url = supervisor.start()
    except ProbeFailure as error:
        second_start_error = str(error)
        second_process, second_url = supervisor.processes[-1][0], supervisor.base_url
    else:
        supervisor.kill(second_process, force=True)
        raise ProbeFailure(f"second Host unexpectedly acquired the same state path and became healthy at {second_url}")
    require(second_process is not original_process, "duplicate Host probe reused the original process handle")
    require(second_process.poll() is not None and second_process.returncode != 0,
            f"second Host did not fail startup while the original owns the state path: pid={second_process.pid}, error={second_start_error}")

    status, final_health = get_json(original_url, "/health")
    require(status == 200 and isinstance(final_health, dict) and final_health.get("status") == "ok",
            f"original Host stopped serving after duplicate-host rejection: HTTP {status}, {final_health}")
    require(final_health.get("processId") == original_process.pid and original_process.poll() is None,
            f"original Host identity/process changed after duplicate-host rejection: {final_health}")
    run_id = create_run(original_url)["id"]
    waiting = wait_run(original_url, run_id, {"WaitingApproval"}, host=original_process)
    assert_waiting_approval(waiting)
    return {
        "originalHostPid": original_process.pid,
        "secondHostPid": second_process.pid,
        "secondHostReturnCode": second_process.returncode,
        "secondHostStartError": second_start_error,
        "secondHostStderrLog": supervisor.process_metadata[-1]["stderrLog"],
        "initialHealth": initial_health,
        "healthAfterDuplicateRejection": final_health,
        "runIdServedByOriginalHost": run_id,
        "waitingApproval": waiting,
    }


def case_workspace_lock_disabled_rejected(case: Path, supervisor: HostSupervisor) -> dict[str, Any]:
    require(os.name != "nt", "workspace-lock-disabled-rejected is Unix-only")
    start_error: str | None = None
    try:
        process, _ = supervisor.start(env_overrides={"DOTNET_SYSTEM_IO_DISABLEFILELOCKING": "1"})
    except ProbeFailure as error:
        start_error = str(error)
        process = supervisor.processes[-1][0]
    else:
        supervisor.kill(process, force=True)
        raise ProbeFailure("Host became healthy even though Unix file locking was disabled")

    require(process.poll() is not None and process.returncode != 0,
            f"Host did not reject disabled workspace locking: pid={process.pid}, returnCode={process.returncode}")
    stderr_log = Path(supervisor.process_metadata[-1]["stderrLog"])
    stderr_text = stderr_log.read_text(encoding="utf-8", errors="replace") if stderr_log.is_file() else ""
    require("workspace_lock_not_enforced" in stderr_text or "workspace_lock_not_enforced" in (start_error or ""),
            f"Host failure did not identify the disabled workspace lock: {start_error!r}, stderr={stderr_text[-3000:]!r}")
    require(not (supervisor.state / "app.db").exists(),
            "Host created app.db before rejecting the unsupported workspace lock")
    artifacts = read_receipt_artifacts(supervisor.state)
    require(not any(artifacts.values()), f"Host created receipt side effects before rejecting the workspace lock: {artifacts}")
    require(supervisor.process_metadata[-1]["ownedWorkerPids"] == [],
            "workspace-lock rejection unexpectedly identified a Worker process")
    return {
        "hostPid": process.pid,
        "returnCode": process.returncode,
        "startError": start_error,
        "stderrLog": str(stderr_log),
        "stderrTail": stderr_text[-3000:],
        "lockingEnvironment": {"DOTNET_SYSTEM_IO_DISABLEFILELOCKING": "1"},
        "appDbCreated": False,
        "receiptArtifacts": artifacts,
        "ownedWorkerPids": supervisor.process_metadata[-1]["ownedWorkerPids"],
    }


CASE_FUNCTIONS: dict[str, Callable[[Path, HostSupervisor], dict[str, Any]]] = {
    "normal": case_normal,
    "denied": case_denied,
    "invalid-arguments": case_invalid_arguments,
    "endless": case_endless,
    "approval-restart": case_approval_restart,
    "fault-before-effect": lambda case, supervisor: case_fault_recovery(case, supervisor, "before-effect"),
    "fault-after-effect": lambda case, supervisor: case_fault_recovery(case, supervisor, "after-effect"),
    "receipt-partial": lambda case, supervisor: case_staged_receipt_recovery(case, supervisor, "receipt-partial"),
    "receipt-flushed": lambda case, supervisor: case_staged_receipt_recovery(case, supervisor, "receipt-flushed"),
    "receipt-corrupt-json": lambda case, supervisor: case_corrupt_final_receipt(case, supervisor, "bad-json"),
    "receipt-conflicting-final": lambda case, supervisor: case_corrupt_final_receipt(case, supervisor, "conflicting-content"),
    "after-ledger-host-restart": case_after_ledger_restart,
    "before-final-commit-resume": case_finalization_resume,
    "after-ledger-worker-death-timeout-restart": case_worker_dies_after_ledger,
    "cancel-waiting-approval": case_cancel_waiting_approval,
    "cancel-persisted-restart": case_cancel_persisted_restart,
    "cancel-before-effect-reconcile": lambda case, supervisor: case_cancel_reconcile(case, supervisor, "before-effect"),
    "cancel-after-effect-reconcile": lambda case, supervisor: case_cancel_reconcile(case, supervisor, "after-effect"),
    "executing-ledger-write-failure": case_executing_ledger_write_failure,
    "corrupt-session-checkpoint": case_corrupt_checkpoint,
    "duplicate-host-state-lock": case_duplicate_host_lock,
    "finalization-corrupt-receipt": case_finalization_corrupt_receipt,
}
if os.name != "nt":
    CASE_FUNCTIONS["workspace-lock-disabled-rejected"] = case_workspace_lock_disabled_rejected


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary_name = tempfile.mkstemp(prefix=f".{path.name}.", suffix=".tmp", dir=path.parent)
    temporary = Path(temporary_name)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as stream:
            json.dump(value, stream, ensure_ascii=False, indent=2)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    except Exception:
        try:
            temporary.unlink(missing_ok=True)
        except OSError:
            pass
        raise


def run_case(name: str, function: Callable[[Path, HostSupervisor], dict[str, Any]], args: argparse.Namespace) -> dict[str, Any]:
    case_root = args.output / "cases" / name
    run_dir = case_root / "runs" / args.execution_id
    logs_dir = run_dir / "logs"
    state = run_dir / "state"
    run_dir.mkdir(parents=True, exist_ok=False)
    state.mkdir()
    result: dict[str, Any] = {
        "name": name,
        "executionId": args.execution_id,
        "ok": False,
        "startedUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
    }
    supervisor = HostSupervisor(args.host, args.worker, args.dotnet, state, logs_dir)
    try:
        result["details"] = function(run_dir, supervisor)
        result["ok"] = True
    except Exception as error:  # Keep going so each isolated case reports its result.
        result["error"] = f"{type(error).__name__}: {error}"
        result["traceback"] = traceback.format_exc()
        result["hostLogsTail"] = supervisor._logs_tail()
    finally:
        try:
            supervisor.close()
        except Exception as cleanup_error:
            result["cleanupError"] = f"{type(cleanup_error).__name__}: {cleanup_error}"
            result["ok"] = False
        result["processes"] = supervisor.process_metadata
        result["runDirectory"] = str(run_dir)
        result["logsDirectory"] = str(logs_dir)
        result["stateDirectory"] = str(state)
        result["finishedUtc"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
        write_json(run_dir / "summary.json", result)
        write_json(case_root / "summary.json", result)
    return result


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True, help="absolute path to the native P0.Host application")
    parser.add_argument("--worker", required=True, help="absolute path to the published P0.Worker.dll")
    parser.add_argument("--dotnet", default="dotnet", help="dotnet command used to launch the Worker (default: dotnet)")
    parser.add_argument("--output", required=True, help="directory for case logs and summary.json")
    args = parser.parse_args(argv)
    args.host = Path(args.host)
    args.worker = Path(args.worker)
    args.output = Path(args.output).expanduser().resolve()
    if not args.host.is_absolute():
        parser.error("--host must be an absolute path")
    if not args.worker.is_absolute():
        parser.error("--worker must be an absolute path")
    if not args.host.is_file():
        parser.error(f"--host does not exist or is not a file: {args.host}")
    if not args.worker.is_file() or args.worker.suffix.lower() != ".dll":
        parser.error(f"--worker must be an existing published .dll: {args.worker}")
    try:
        command_parts = shlex.split(args.dotnet, posix=os.name != "nt")
    except ValueError as error:
        parser.error(f"invalid --dotnet command: {error}")
    if not command_parts:
        parser.error("--dotnet command is empty")
    args.output.mkdir(parents=True, exist_ok=True)
    args.execution_id = f"{time.strftime('%Y%m%dT%H%M%SZ', time.gmtime())}-{os.getpid()}-{os.urandom(4).hex()}"
    return args


def build_suite_summary(
    args: argparse.Namespace,
    results: list[dict[str, Any]],
    registered_names: list[str],
    started_utc: str,
) -> dict[str, Any]:
    names = list(registered_names)
    errors: list[str] = []
    if not names:
        errors.append("case registry is empty")
    if any(not isinstance(name, str) or not name.strip() for name in names):
        errors.append("case registry contains an empty or invalid name")
    if len(set(name for name in names if isinstance(name, str))) != len(names):
        errors.append("case registry contains duplicate names")
    if len(results) != len(names):
        errors.append(f"result count {len(results)} does not match registered case count {len(names)}")

    actual_names = [result.get("name") if isinstance(result, dict) else None for result in results]
    if actual_names != names:
        errors.append(f"result names do not match ordered registry: expected={names}, actual={actual_names}")
    if any(not isinstance(result, dict) or type(result.get("ok")) is not bool for result in results):
        errors.append("every case result must contain a boolean ok value")

    boolean_results = [result for result in results if isinstance(result, dict) and type(result.get("ok")) is bool]
    passed = sum(1 for result in boolean_results if result["ok"] is True)
    failed = sum(1 for result in boolean_results if result["ok"] is False)
    return {
        "suite": "P0 process integration",
        "executionId": args.execution_id,
        "host": str(args.host),
        "worker": str(args.worker),
        "dotnet": args.dotnet,
        "startedUtc": started_utc,
        "finishedUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "caseNames": names,
        "caseCount": len(names),
        "passed": passed,
        "failed": failed,
        "suiteError": "; ".join(errors) if errors else None,
        "cases": results,
    }


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv if argv is not None else sys.argv[1:])
    registered_names = list(CASE_FUNCTIONS)
    started_utc = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    results = [run_case(name, function, args) for name, function in CASE_FUNCTIONS.items()]
    summary = build_suite_summary(args, results, registered_names, started_utc)
    write_json(args.output / "summary.json", summary)
    for result in results:
        label = "PASS" if result["ok"] else "FAIL"
        print(f"{label}: {result['name']}")
        if not result["ok"]:
            print(f"  {result.get('error', 'unknown failure')}")
            print(f"  details: {args.output / 'cases' / result['name'] / 'summary.json'}")
    print(f"Summary: {args.output / 'summary.json'}")
    if summary["suiteError"]:
        print(f"SUITE ERROR: {summary['suiteError']}")
    return 0 if summary["failed"] == 0 and summary["suiteError"] is None else 1


if __name__ == "__main__":
    raise SystemExit(main())
