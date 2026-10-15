#!/usr/bin/env python3
"""Run the documented M1 API lifecycle against an isolated local Career Host.

The script intentionally uses only the Python standard library. It starts the
test-only OpenAI-compatible model fixture and the Host command supplied by the
caller. The Host command must consume the `{workspace}` placeholder so this
flow cannot accidentally mutate an existing workspace.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import queue
import re
import shlex
import shutil
import signal
import sqlite3
import socket
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from pathlib import Path
from typing import Any

from keychain_cleanup import cleanup_keychain_item


PAIRING_CODE = re.compile(r"(?:pairing\s*code|配对码|配对代码)\s*[:：=]\s*([A-Za-z0-9_-]{4,})", re.IGNORECASE)
SENSITIVE_FIELD = re.compile(
    r"(?i)\b(api[_ -]?key|(?:access|refresh|bearer|session|auth)?[_ -]?token|secret|password|pairing[_ -]?code|authorization)\b([\"']?\s*[:=]\s*[\"']?)([^\"'\s,;}\]]+)"
)
BEARER_TOKEN = re.compile(r"(?i)\b(bearer\s+)[A-Za-z0-9._~+/=-]+")


class FlowFailure(RuntimeError):
    pass


def diagnostic_token(value: Any) -> str:
    if not isinstance(value, str):
        return "none"
    safe_value = "".join(
        character for character in value
        if character.isascii() and (character.isalnum() or character in "_-")
    )
    return safe_value[:48] or "none"


class HostOutput:
    def __init__(self, process: subprocess.Popen[str]) -> None:
        self.process = process
        self.lines: list[str] = []
        self.secrets: set[str] = set()
        self.lock = threading.Lock()
        self.code: queue.Queue[str] = queue.Queue()
        self.reader = threading.Thread(target=self._read, daemon=True)
        self.reader.start()

    def _read(self) -> None:
        assert self.process.stdout is not None
        for line in self.process.stdout:
            with self.lock:
                self.lines.append(line.rstrip())
            match = PAIRING_CODE.search(line)
            if match:
                self.code.put(match.group(1))

    def tail(self) -> str:
        with self.lock:
            lines = self.lines[-30:]
            secrets = sorted(self.secrets, key=len, reverse=True)
        output = "\n".join(lines)
        for secret in secrets:
            output = output.replace(secret, "<redacted>")
        output = BEARER_TOKEN.sub(r"\1<redacted>", output)
        output = SENSITIVE_FIELD.sub(r"\1\2<redacted>", output)
        return PAIRING_CODE.sub("pairing code: <redacted>", output)

    def error_tail(self) -> str:
        return "\n".join(
            line
            for line in self.tail().splitlines()
            if re.search(r"(?i)\b(error|exception|critical|failed|failure)\b", line)
        )

    def add_secret(self, value: str | None) -> None:
        if value:
            with self.lock:
                self.secrets.add(value)


def reserve_port() -> int:
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        return int(listener.getsockname()[1])


def json_request(
    base_url: str,
    path: str,
    method: str = "GET",
    body: dict[str, Any] | None = None,
    token: str | None = None,
    timeout: float = 5,
    extra_headers: dict[str, str] | None = None,
) -> tuple[int, dict[str, Any], dict[str, str]]:
    data = None if body is None else json.dumps(body, separators=(",", ":")).encode("utf-8")
    headers = {"Accept": "application/json", "X-Career-Client": "web"}
    if data is not None:
        headers["Content-Type"] = "application/json"
    if token:
        headers["Authorization"] = f"Bearer {token}"
    if extra_headers:
        headers.update(extra_headers)
    request = urllib.request.Request(base_url.rstrip("/") + path, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            raw = response.read()
            return response.status, parse_json(raw), dict(response.headers.items())
    except urllib.error.HTTPError as error:
        raw = error.read()
        return error.code, parse_json(raw), dict(error.headers.items())


def bytes_request(
    base_url: str,
    path: str,
    token: str,
    timeout: float = 5,
) -> tuple[int, bytes, dict[str, str]]:
    request = urllib.request.Request(
        base_url.rstrip("/") + path,
        headers={"Authorization": f"Bearer {token}", "X-Career-Client": "web"},
    )
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return response.status, response.read(), dict(response.headers.items())
    except urllib.error.HTTPError as error:
        return error.code, error.read(), dict(error.headers.items())


def parse_json(raw: bytes) -> dict[str, Any]:
    if not raw:
        return {}
    try:
        value = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise FlowFailure(f"Expected JSON response, received invalid content ({len(raw)} bytes).") from error
    if not isinstance(value, dict):
        raise FlowFailure("Expected a JSON object response.")
    return value


def require_status(actual: int, expected: int | tuple[int, ...], label: str, payload: dict[str, Any]) -> None:
    accepted = (expected,) if isinstance(expected, int) else expected
    if actual not in accepted:
        code = payload.get("code") or payload.get("Code") or payload.get("title") or payload.get("detail")
        raise FlowFailure(f"{label} returned HTTP {actual}; expected {accepted}; error={code!r}.")


def wait_json(
    base_url: str,
    path: str,
    token: str | None,
    timeout: float,
    process: subprocess.Popen[str] | None = None,
) -> dict[str, Any]:
    deadline = time.monotonic() + timeout
    last_error: Exception | None = None
    while time.monotonic() < deadline:
        if process is not None and process.poll() is not None:
            raise FlowFailure(f"Host exited with code {process.returncode}.\n{process_output_tail(process)}")
        try:
            status, value, _ = json_request(base_url, path, token=token, timeout=1)
            if status == 200:
                return value
            last_error = FlowFailure(f"HTTP {status}")
        except (urllib.error.URLError, TimeoutError, FlowFailure) as error:
            last_error = error
        time.sleep(0.2)
    raise FlowFailure(f"Timed out waiting for {path}: {last_error!r}.")


def process_output_tail(process: subprocess.Popen[str]) -> str:
    host_output = getattr(process, "_career_output", None)
    tail = host_output.error_tail() if host_output is not None else ""
    return "\n" + tail if tail else ""


def task_from_api(base_url: str, task_id: str, token: str) -> dict[str, Any]:
    status, task, _ = json_request(base_url, f"/api/tasks/{urllib.parse.quote(task_id, safe='')}", token=token)
    require_status(status, 200, "GET task", task)
    return task


def wait_for_task(
    base_url: str,
    task_id: str,
    token: str,
    predicate: Any,
    description: str,
    timeout: float,
    process: subprocess.Popen[str],
) -> dict[str, Any]:
    deadline = time.monotonic() + timeout
    latest: dict[str, Any] = {}
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise FlowFailure(f"Host exited while waiting for {description}: {process.returncode}.{process_output_tail(process)}")
        latest = task_from_api(base_url, task_id, token)
        if predicate(latest):
            return latest
        if latest.get("status") in ("Failed", "Blocked", "Cancelled"):
            raise FlowFailure(f"Task entered {latest.get('status')} while waiting for {description}.")
        time.sleep(0.25)
    approval_states = [approval.get("status") for approval in latest.get("approvals", [])]
    raise FlowFailure(
        f"Timed out waiting for {description}; last task status={latest.get('status')!r}; "
        f"approval states={approval_states!r}.{process_output_tail(process)}"
    )


def require_problem_code(status: int, payload: dict[str, Any], expected: str, label: str) -> None:
    if payload.get("code") != expected:
        raise FlowFailure(f"{label} returned HTTP {status} with code={payload.get('code')!r}; expected {expected}.")


def response_header(headers: dict[str, str], name: str) -> str:
    return next((value for key, value in headers.items() if key.casefold() == name.casefold()), "")


def fixture_request(
    fixture_base_url: str,
    path: str,
    method: str = "GET",
    body: dict[str, Any] | None = None,
) -> dict[str, Any]:
    status, value, _ = json_request(fixture_base_url, path, method=method, body=body, timeout=3)
    require_status(status, 200, f"fixture {method} {path}", value)
    return value


def set_connection_budget(base_url: str, token: str, model_url: str, max_model_calls: int) -> None:
    request = {
        "requestId": str(uuid.uuid4()),
        "name": "M1 local protocol fixture",
        "protocol": "chat-completions",
        "endpoint": model_url,
        "model": "career-test-model",
        "apiKey": None,
        "allowLocalEndpoint": True,
        "maxOutputTokens": 1024,
        "maxModelCalls": max_model_calls,
        "maxInputCharacters": 8000,
    }
    status, saved, _ = json_request(base_url, "/api/connection", method="PUT", body=request, token=token)
    require_status(status, 200, "PUT connection budget", saved)
    if saved.get("maxModelCalls") != max_model_calls or saved.get("hasCredential") is not True:
        raise FlowFailure("Saving a model-call budget changed credential state or returned the wrong limit.")
    status, tested, _ = json_request(
        base_url,
        "/api/connection/test",
        method="POST",
        body={"requestId": str(uuid.uuid4())},
        token=token,
    )
    require_status(status, 200, "POST connection test after budget change", tested)
    if tested.get("testState") != "verified":
        raise FlowFailure("The local model capability check did not verify the changed call budget.")


def create_confirmed_task(base_url: str, token: str, title: str, file_name: str) -> str:
    create = {
        "requestId": str(uuid.uuid4()),
        "title": title,
        "goal": "Create a reviewable local integration artifact",
        "criteria": ["Include the goal", "Keep the output in Markdown"],
        "inputText": f"Use the test-only local model fixture. Request fileName '{file_name}'.",
        "mode": "development",
        "project": None,
    }
    status, task, _ = json_request(base_url, "/api/tasks", method="POST", body=create, token=token)
    require_status(status, 201, "POST safety task", task)
    task_id = task.get("id")
    if not isinstance(task_id, str) or task.get("status") != "AwaitingPlan":
        raise FlowFailure("A safety task did not enter AwaitingPlan with an ID.")
    confirmation = {
        "requestId": str(uuid.uuid4()),
        "expectedVersion": task["version"],
        "taskRevision": task["revision"],
        "proposalHash": task["plan"]["proposalHash"],
    }
    status, confirmed, _ = json_request(
        base_url,
        f"/api/tasks/{urllib.parse.quote(task_id, safe='')}/confirm-plan",
        method="POST",
        body=confirmation,
        token=token,
    )
    require_status(status, (200, 202), "POST safety task confirmation", confirmed)
    return task_id


def decide_approval(
    base_url: str,
    task: dict[str, Any],
    approval: dict[str, Any],
    token: str,
    decision: str,
    note: str,
) -> dict[str, Any]:
    body = {
        "requestId": str(uuid.uuid4()),
        "expectedVersion": task["version"],
        "payloadHash": approval["payloadHash"],
        "decision": decision,
        "note": note,
    }
    status, updated, _ = json_request(
        base_url,
        f"/api/approvals/{urllib.parse.quote(approval['id'], safe='')}/decision",
        method="POST",
        body=body,
        token=token,
    )
    require_status(status, (200, 202), f"POST {decision} approval", updated)
    return updated


def fixture_metrics(fixture_base_url: str) -> dict[str, Any]:
    return fixture_request(fixture_base_url, "/__metrics")


def wait_for_fixture_request_count(fixture_base_url: str, minimum: int, timeout: float) -> dict[str, Any]:
    deadline = time.monotonic() + timeout
    latest: dict[str, Any] = {}
    while time.monotonic() < deadline:
        latest = fixture_metrics(fixture_base_url)
        if latest.get("requests", 0) >= minimum:
            return latest
        time.sleep(0.1)
    raise FlowFailure(f"Timed out waiting for the local model fixture to receive {minimum} requests.")


def published_file_snapshot(workspace: Path) -> set[str]:
    artifacts = workspace / "artifacts"
    if not artifacts.exists():
        return set()
    return {str(path.relative_to(workspace)) for path in artifacts.rglob("*") if path.is_file()}


def test_auth_boundaries_and_refresh(
    base_url: str,
    workspace: Path,
    token: str,
    refresh_cookie: str,
) -> tuple[str, str]:
    status, unauthenticated, _ = json_request(base_url, "/api/workspace")
    require_status(status, 401, "unauthenticated workspace read", unauthenticated)
    require_problem_code(status, unauthenticated, "OWNER_SESSION_REQUIRED", "unauthenticated workspace read")

    status, unauthenticated_write, _ = json_request(
        base_url,
        "/api/workspace/pause-intake",
        method="POST",
        body={"requestId": str(uuid.uuid4())},
    )
    require_status(status, 401, "unauthenticated workspace write", unauthenticated_write)
    require_problem_code(status, unauthenticated_write, "OWNER_SESSION_REQUIRED", "unauthenticated workspace write")

    status, rejected_origin, _ = json_request(
        base_url,
        "/api/workspace",
        token=token,
        extra_headers={"Origin": "https://untrusted.example"},
    )
    require_status(status, 403, "hostile Origin workspace read", rejected_origin)
    require_problem_code(status, rejected_origin, "ORIGIN_NOT_ALLOWED", "hostile Origin workspace read")

    access_hash = hashlib.sha256(token.encode("utf-8")).hexdigest()
    database_path = workspace / "state" / "app.db"
    with sqlite3.connect(database_path, timeout=5) as database:
        cursor = database.execute(
            "UPDATE sessions SET access_expires = ? WHERE access_hash = ?",
            ("2000-01-01T00:00:00.0000000+00:00", access_hash),
        )
        if cursor.rowcount != 1:
            raise FlowFailure("Could not install the isolated expired-access-session test condition.")
        database.commit()

    status, expired_access, _ = json_request(base_url, "/api/workspace", token=token)
    require_status(status, 401, "expired access session", expired_access)
    require_problem_code(status, expired_access, "OWNER_SESSION_REQUIRED", "expired access session")

    status, refreshed, headers = json_request(
        base_url,
        "/api/auth/refresh",
        method="POST",
        extra_headers={"Cookie": refresh_cookie},
    )
    require_status(status, 200, "refresh after access expiry", refreshed)
    new_token = refreshed.get("accessToken")
    new_cookie = response_header(headers, "set-cookie").split(";", 1)[0]
    if not isinstance(new_token, str) or not new_token or new_token == token or not new_cookie.startswith("career.refresh="):
        raise FlowFailure("Refresh after access expiry did not rotate the short-lived session.")

    status, old_access, _ = json_request(base_url, "/api/workspace", token=token)
    require_status(status, 401, "previous access session after refresh", old_access)
    status, renewed_access, _ = json_request(base_url, "/api/workspace", token=new_token)
    require_status(status, 200, "renewed access session", renewed_access)
    return new_token, new_cookie


def test_logout_revokes_access_and_refresh(base_url: str, token: str, refresh_cookie: str) -> None:
    status, logged_out, _ = json_request(
        base_url,
        "/api/auth/logout",
        method="POST",
        token=token,
        extra_headers={"Cookie": refresh_cookie},
    )
    require_status(status, 204, "POST logout", logged_out)

    status, rejected_access, _ = json_request(base_url, "/api/workspace", token=token)
    require_status(status, 401, "access session after logout", rejected_access)
    require_problem_code(status, rejected_access, "OWNER_SESSION_REQUIRED", "access session after logout")

    status, rejected_refresh, _ = json_request(
        base_url,
        "/api/auth/refresh",
        method="POST",
        extra_headers={"Cookie": refresh_cookie},
    )
    require_status(status, 401, "refresh session after logout", rejected_refresh)
    require_problem_code(status, rejected_refresh, "SESSION_EXPIRED", "refresh session after logout")


def run_low_budget_scenario(base_url: str, fixture_base_url: str, model_url: str, token: str, process: subprocess.Popen[str], flow_timeout: float) -> None:
    fixture_request(
        fixture_base_url,
        "/__control",
        method="POST",
        body={"reset": True, "delayMs": 0, "reapproveAfterDenial": True},
    )
    set_connection_budget(base_url, token, model_url, max_model_calls=2)
    task_id = create_confirmed_task(base_url, token, "M1 low model-call budget", "budget-result.md")
    task = wait_for_task(
        base_url,
        task_id,
        token,
        lambda value: value.get("status") == "WaitingApproval" and bool(value.get("approvals")),
        "a proposal under the low model-call budget",
        flow_timeout,
        process,
    )
    first_approval = task["approvals"][-1]
    decide_approval(base_url, task, first_approval, token, "reject", "Exercise the bounded revision call.")
    revised = wait_for_task(
        base_url,
        task_id,
        token,
        lambda value: value.get("status") == "WaitingApproval" and len(value.get("approvals", [])) > 1,
        "a revised proposal at the final allowed model call",
        flow_timeout,
        process,
    )
    if revised.get("artifacts"):
        raise FlowFailure("Rejecting a low-budget proposal published an artifact.")
    approval = revised["approvals"][-1]
    metrics_before_approval = fixture_metrics(fixture_base_url)
    decide_approval(base_url, revised, approval, token, "approve", "Approve the final permitted proposal.")
    published = wait_for_task(
        base_url,
        task_id,
        token,
        lambda value: value.get("status") == "InReview"
        and bool(value.get("runs"))
        and value["runs"][-1].get("status") == "Succeeded",
        "the published result to remain in review when the call limit prevents a third model request",
        flow_timeout,
        process,
    )
    run = published["runs"][-1]
    if run.get("modelCalls") != 2:
        raise FlowFailure("The published run did not stop at the configured two-call budget boundary.")
    if published.get("status") != "InReview" or published.get("acceptances"):
        raise FlowFailure("A low-budget run was treated as accepted without a completed review.")
    if len(published.get("artifacts", [])) != 1:
        raise FlowFailure("The explicitly approved operation did not preserve its single publication receipt.")
    metrics_after_publication = fixture_metrics(fixture_base_url)
    if metrics_after_publication.get("requests") != metrics_before_approval.get("requests"):
        raise FlowFailure("The worker dispatched a model request after exhausting the configured budget.")


def restart_host(
    command: list[str],
    host_env: dict[str, str],
    base_url: str,
    token: str,
    workspace_id: str,
    timeout: float,
    old_process: subprocess.Popen[str],
) -> tuple[subprocess.Popen[str], HostOutput]:
    stop_process(old_process)
    process = launch(command, host_env)
    output = HostOutput(process)
    setattr(process, "_career_output", output)
    try:
        bootstrap = wait_json(base_url, "/api/bootstrap", None, timeout, process)
        if bootstrap.get("workspaceId") != workspace_id or bootstrap.get("hasOwner") is not True:
            raise FlowFailure("The restarted Host did not restore the workspace identity and initialized owner.")
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if process.poll() is not None:
                raise FlowFailure(f"Host exited during startup recovery: {process.returncode}.{process_output_tail(process)}")
            status, snapshot, _ = json_request(base_url, "/api/workspace", token=token, timeout=1)
            if status == 200 and snapshot.get("executionAvailable") is True:
                return process, output
            time.sleep(0.1)
        raise FlowFailure("Timed out waiting for execution recovery to finish after restart.")
    except Exception:
        stop_process(process)
        raise


def run_restart_recovery_scenario(
    base_url: str,
    fixture_base_url: str,
    token: str,
    workspace_id: str,
    command: list[str],
    host_env: dict[str, str],
    process: subprocess.Popen[str],
    startup_timeout: float,
    flow_timeout: float,
) -> tuple[subprocess.Popen[str], HostOutput]:
    fixture_request(
        fixture_base_url,
        "/__control",
        method="POST",
        body={"reset": True, "delayMs": 0},
    )
    task_id = create_confirmed_task(base_url, token, "M1 approval restart recovery", "restart-result.md")
    pending = wait_for_task(
        base_url,
        task_id,
        token,
        lambda value: value.get("status") == "WaitingApproval" and bool(value.get("approvals")),
        "a pending approval before Host restart",
        flow_timeout,
        process,
    )
    approval = pending["approvals"][-1]
    approved_id = approval["id"]
    approved_hash = approval["payloadHash"]
    calls_before_restart = fixture_metrics(fixture_base_url).get("requests")

    process, output = restart_host(
        command,
        host_env,
        base_url,
        token,
        workspace_id,
        startup_timeout,
        process,
    )
    try:
        recovered = task_from_api(base_url, task_id, token)
        if recovered.get("status") != "WaitingApproval" or recovered.get("artifacts"):
            raise FlowFailure("Restart did not preserve the task as a non-publishing pending approval.")
        recovered_approval = next((item for item in recovered.get("approvals", []) if item.get("id") == approved_id), None)
        if recovered_approval is None or recovered_approval.get("status") != "Pending" or recovered_approval.get("payloadHash") != approved_hash:
            raise FlowFailure("Restart changed or dropped the exact pending approval payload.")
        time.sleep(0.75)
        still_waiting = task_from_api(base_url, task_id, token)
        metrics_after_wait = fixture_metrics(fixture_base_url)
        if still_waiting.get("status") != "WaitingApproval" or metrics_after_wait.get("requests") != calls_before_restart:
            raise FlowFailure("A pending approval resumed model execution without an owner decision after restart.")

        decide_approval(base_url, still_waiting, recovered_approval, token, "approve", "Continue after verifying the recovered preview.")
        in_review = wait_for_task(
            base_url,
            task_id,
            token,
            lambda value: value.get("status") == "InReview" and bool(value.get("artifacts")),
            "the restarted task publishing only after approval",
            flow_timeout,
            process,
        )
        artifact = in_review["artifacts"][-1]
        acceptance = {
            "requestId": str(uuid.uuid4()),
            "expectedVersion": in_review["version"],
            "taskRevision": in_review["revision"],
            "artifactId": artifact["id"],
            "artifactVersion": artifact["version"],
            "contentHash": artifact["contentHash"],
            "decision": "accept",
            "note": "Accept the recovered and reviewed version.",
        }
        status, _, _ = json_request(
            base_url,
            f"/api/tasks/{urllib.parse.quote(task_id, safe='')}/acceptances",
            method="POST",
            body=acceptance,
            token=token,
        )
        require_status(status, (200, 202), "POST recovered artifact acceptance", {})
        done = wait_for_task(
            base_url,
            task_id,
            token,
            lambda value: value.get("status") == "Done" and bool(value.get("acceptances")),
            "the recovered task entering Done after acceptance",
            flow_timeout,
            process,
        )
        if done.get("acceptances", [])[-1].get("contentHash") != artifact["contentHash"]:
            raise FlowFailure("The restarted task accepted a different artifact version.")
        return process, output
    except Exception:
        stop_process(process)
        raise


def run_running_restart_recovery_scenario(
    base_url: str,
    fixture_base_url: str,
    token: str,
    workspace_id: str,
    command: list[str],
    host_env: dict[str, str],
    process: subprocess.Popen[str],
    startup_timeout: float,
    flow_timeout: float,
) -> tuple[subprocess.Popen[str], HostOutput]:
    fixture_request(
        fixture_base_url,
        "/__control",
        method="POST",
        body={"reset": True, "delayMs": 5000},
    )
    task_id = create_confirmed_task(base_url, token, "M1 running restart recovery", "running-restart-result.md")
    running = wait_for_task(
        base_url,
        task_id,
        token,
        lambda value: value.get("status") == "Running",
        "a running task before Host restart",
        flow_timeout,
        process,
    )
    wait_for_fixture_request_count(fixture_base_url, 1, min(flow_timeout, 10))
    original_run = running["runs"][-1]
    calls_before_restart = fixture_metrics(fixture_base_url).get("requests")

    process, output = restart_host(
        command,
        host_env,
        base_url,
        token,
        workspace_id,
        startup_timeout,
        process,
    )
    try:
        paused = task_from_api(base_url, task_id, token)
        paused_runs = paused.get("runs", [])
        paused_run = paused_runs[-1] if paused_runs else {}
        if paused.get("status") != "Paused" or paused_run.get("status") != "Paused":
            raise FlowFailure(
                "A running task did not recover as Paused after Host restart "
                f"(taskStatus={diagnostic_token(paused.get('status'))}; "
                f"runStatus={diagnostic_token(paused_run.get('status'))}; "
                f"failureCode={diagnostic_token(paused_run.get('failureCode'))})."
            )
        if paused_run.get("id") != original_run.get("id") or paused.get("artifacts"):
            raise FlowFailure("Restart changed the interrupted run identity or published an artifact.")

        time.sleep(0.75)
        still_paused = task_from_api(base_url, task_id, token)
        calls_after_recovery = fixture_metrics(fixture_base_url).get("requests")
        if still_paused.get("status") != "Paused" or calls_after_recovery != calls_before_restart:
            raise FlowFailure("A recovered running task dispatched again before owner resume.")

        fixture_request(fixture_base_url, "/__control", method="POST", body={"delayMs": 0})
        status, resumed, _ = json_request(
            base_url,
            f"/api/tasks/{urllib.parse.quote(task_id, safe='')}/resume",
            method="POST",
            body={"requestId": str(uuid.uuid4()), "expectedVersion": still_paused["version"]},
            token=token,
        )
        require_status(status, 200, "POST task resume after restart", resumed)
        if resumed.get("status") != "Queued":
            raise FlowFailure("Owner resume after restart did not queue the saved task.")

        wait_for_fixture_request_count(fixture_base_url, int(calls_before_restart) + 1, min(flow_timeout, 10))
        waiting = wait_for_task(
            base_url,
            task_id,
            token,
            lambda value: value.get("status") == "WaitingApproval" and bool(value.get("approvals")),
            "the resumed task to reach its next owner approval",
            flow_timeout,
            process,
        )
        resumed_run = waiting["runs"][-1]
        if resumed_run.get("id") != original_run.get("id") or resumed_run.get("attemptNumber", 0) <= original_run.get("attemptNumber", 0):
            raise FlowFailure("Manual resume did not advance the interrupted run attempt.")
        if waiting.get("artifacts"):
            raise FlowFailure("The resumed task published an artifact before owner approval.")
        return process, output
    except Exception:
        stop_process(process)
        raise


def run_control_scenario(
    action: str,
    terminal_status: str,
    base_url: str,
    fixture_base_url: str,
    token: str,
    workspace: Path,
    process: subprocess.Popen[str],
    flow_timeout: float,
) -> None:
    fixture_request(
        fixture_base_url,
        "/__control",
        method="POST",
        body={"reset": True, "delayMs": 5000},
    )
    files_before = published_file_snapshot(workspace)
    task_id = create_confirmed_task(base_url, token, f"M1 {action} while model is waiting", f"{action}-result.md")
    running = wait_for_task(
        base_url,
        task_id,
        token,
        lambda value: value.get("status") == "Running",
        f"the slow model call before {action}",
        flow_timeout,
        process,
    )
    wait_for_fixture_request_count(fixture_base_url, 1, min(flow_timeout, 10))
    try:
        active = task_from_api(base_url, task_id, token)
        if active.get("status") != "Running":
            raise FlowFailure(f"The task left Running before its in-flight {action} request.")
        status, requested, _ = json_request(
            base_url,
            f"/api/tasks/{urllib.parse.quote(task_id, safe='')}/{action}",
            method="POST",
            body={"requestId": str(uuid.uuid4()), "expectedVersion": active["version"]},
            token=token,
        )
        require_status(status, 200, f"POST task {action}", requested)
        if requested.get("status") != "Stopping":
            raise FlowFailure(f"The in-flight {action} request did not enter Stopping before acknowledgement.")
        stopped = wait_for_task(
            base_url,
            task_id,
            token,
            lambda value: value.get("status") == terminal_status,
            f"the worker acknowledging {action}",
            min(flow_timeout, 20),
            process,
        )
        if stopped.get("runs", [])[-1].get("status") != terminal_status or stopped.get("artifacts"):
            raise FlowFailure(f"The {action} result did not preserve the expected terminal state without publication.")
        if published_file_snapshot(workspace) != files_before:
            raise FlowFailure(f"The in-flight {action} scenario created an artifact file before model completion.")
    finally:
        fixture_request(fixture_base_url, "/__control", method="POST", body={"delayMs": 0})


def run_extended_scenarios(
    base_url: str,
    fixture_base_url: str,
    model_url: str,
    workspace: Path,
    token: str,
    refresh_cookie: str,
    workspace_id: str,
    command: list[str],
    host_env: dict[str, str],
    process: subprocess.Popen[str],
    output: HostOutput,
    startup_timeout: float,
    flow_timeout: float,
    report: dict[str, Any],
) -> tuple[subprocess.Popen[str], HostOutput, str, str]:
    try:
        token, refresh_cookie = test_auth_boundaries_and_refresh(base_url, workspace, token, refresh_cookie)
        report["assertions"]["auth_boundaries_and_refresh"] = "passed"
        output.add_secret(token)
        output.add_secret(refresh_cookie)
        run_low_budget_scenario(base_url, fixture_base_url, model_url, token, process, flow_timeout)
        report["assertions"]["two_call_budget_enforced"] = "passed"
        set_connection_budget(base_url, token, model_url, max_model_calls=6)
        process, output = run_restart_recovery_scenario(
            base_url,
            fixture_base_url,
            token,
            workspace_id,
            command,
            host_env,
            process,
            startup_timeout,
            flow_timeout,
        )
        report["assertions"]["pending_approval_restart_recovery"] = "passed"
        output.add_secret(token)
        output.add_secret(refresh_cookie)
        process, output = run_running_restart_recovery_scenario(
            base_url,
            fixture_base_url,
            token,
            workspace_id,
            command,
            host_env,
            process,
            startup_timeout,
            flow_timeout,
        )
        report["assertions"]["running_restart_paused_until_manual_resume"] = "passed"
        output.add_secret(token)
        output.add_secret(refresh_cookie)
        run_control_scenario("pause", "Paused", base_url, fixture_base_url, token, workspace, process, flow_timeout)
        report["assertions"]["pause_during_model_call"] = "passed"
        run_control_scenario("cancel", "Cancelled", base_url, fixture_base_url, token, workspace, process, flow_timeout)
        report["assertions"]["cancel_during_model_call"] = "passed"
        fixture_request(fixture_base_url, "/__control", method="POST", body={"delayMs": 0})
        test_logout_revokes_access_and_refresh(base_url, token, refresh_cookie)
        report["assertions"]["logout_revokes_access_and_refresh"] = "passed"
        report["statistics"]["extended_scenario_count"] = 7
        print(
            "PASS M1 safeguards: unauthenticated read/write, hostile Origin, expired access refresh, "
            "two-call budget enforcement, pending-approval and running-task restart recovery, pause, cancel, and logout."
        )
        return process, output, token, refresh_cookie
    except Exception:
        stop_process(process)
        raise


def launch(command: list[str], env: dict[str, str], stdout: Any = subprocess.PIPE) -> subprocess.Popen[str]:
    kwargs: dict[str, Any] = {
        "stdout": stdout,
        "stderr": subprocess.STDOUT if stdout == subprocess.PIPE else subprocess.DEVNULL,
        "text": True,
        "encoding": "utf-8",
        "errors": "replace",
        "bufsize": 1,
        "env": env,
    }
    if os.name == "nt":
        kwargs["creationflags"] = subprocess.CREATE_NEW_PROCESS_GROUP
    else:
        kwargs["start_new_session"] = True
    return subprocess.Popen(command, **kwargs)


def stop_process(process: subprocess.Popen[str] | None) -> None:
    if process is None or process.poll() is not None:
        return
    try:
        if os.name == "nt":
            process.terminate()
        else:
            os.killpg(process.pid, signal.SIGTERM)
        process.wait(timeout=5)
    except (OSError, subprocess.TimeoutExpired):
        process.kill()
        process.wait(timeout=5)


def assert_task_receipt(
    base_url: str,
    token: str,
    request_id: str,
    expected_task_id: str,
    expected_version: int,
) -> None:
    status, receipt, _ = json_request(base_url, f"/api/commands/{request_id}", token=token)
    require_status(status, 200, "GET command receipt", receipt)
    if receipt.get("requestId") != request_id or receipt.get("taskId") != expected_task_id:
        raise FlowFailure("Command receipt did not identify the accepted task request.")
    if receipt.get("version") != expected_version:
        raise FlowFailure("Command receipt version differs from the first accepted response.")


def run_flow(arguments: argparse.Namespace) -> None:
    workspace = Path(tempfile.mkdtemp(prefix="career-m1-flow-"))
    fixture = Path(__file__).resolve().parents[1] / "fixtures" / "model-server.py"
    host_process: subprocess.Popen[str] | None = None
    model_process: subprocess.Popen[str] | None = None
    host_output: HostOutput | None = None
    workspace_id: str | None = None
    refresh_cookie: str | None = None
    key_file = workspace / "vault.key"
    report: dict[str, Any] = {"status": "failed", "assertions": {}, "statistics": {}}
    cleanup_failure: str | None = None
    try:
        model_port = reserve_port()
        model_process = launch(
            [sys.executable, str(fixture), "--host", "127.0.0.1", "--port", str(model_port)],
            os.environ.copy(),
            stdout=subprocess.DEVNULL,
        )
        model_url = f"http://127.0.0.1:{model_port}/v1"
        wait_json(f"http://127.0.0.1:{model_port}", "/health", None, arguments.startup_timeout, model_process)

        host_env = os.environ.copy()
        host_env["ASPNETCORE_URLS"] = arguments.base_url
        host_env["CAREER_WORKSPACE_DIRECTORY"] = str(workspace)
        if sys.platform.startswith("linux"):
            key_file.write_bytes(os.urandom(32))
            key_file.chmod(0o600)
            host_env["CAREER_VAULT_KEY_FILE"] = str(key_file)

        command = shlex.split(arguments.host_command)
        if not command:
            raise FlowFailure("--host-command is empty.")
        if not any("{workspace}" in part for part in command):
            raise FlowFailure("--host-command must include the {workspace} placeholder to isolate this run.")
        substitutions = {
            "{workspace}": str(workspace),
            "{base_url}": arguments.base_url,
            "{model_url}": model_url,
        }
        command = [
            next_value
            for part in command
            for next_value in [
                part.replace("{workspace}", substitutions["{workspace}"])
                .replace("{base_url}", substitutions["{base_url}"])
                .replace("{model_url}", substitutions["{model_url}"])
            ]
        ]
        host_process = launch(command, host_env)
        host_output = HostOutput(host_process)
        setattr(host_process, "_career_output", host_output)

        bootstrap = wait_json(arguments.base_url, "/api/bootstrap", None, arguments.startup_timeout, host_process)
        workspace_id = bootstrap.get("workspaceId")
        if not isinstance(workspace_id, str) or not workspace_id:
            raise FlowFailure("Bootstrap omitted workspaceId.")
        if "pairingCode" in bootstrap or "code" in bootstrap:
            raise FlowFailure("Bootstrap disclosed a pairing code.")

        token = arguments.bearer_token
        if bootstrap.get("requiresPairing"):
            pairing_code = arguments.pairing_code or os.environ.get("CAREER_PAIRING_CODE")
            if not pairing_code:
                try:
                    pairing_code = host_output.code.get(timeout=min(arguments.startup_timeout, 10))
                except queue.Empty as error:
                    raise FlowFailure("Host requires pairing but printed no recognizable pairing code.") from error
            host_output.add_secret(pairing_code)
            status, session, pair_headers = json_request(
                arguments.base_url,
                "/api/auth/pair",
                method="POST",
                body={"code": pairing_code},
            )
            require_status(status, 200, "POST pair", session)
            token = session.get("accessToken")
            refresh_cookie = response_header(pair_headers, "set-cookie").split(";", 1)[0]
            if not refresh_cookie.startswith("career.refresh="):
                raise FlowFailure("Pairing did not establish the isolated refresh cookie.")
            host_output.add_secret(token)
            host_output.add_secret(refresh_cookie)
            if session.get("workspaceId") != workspace_id:
                raise FlowFailure("Pairing returned a different workspace ID.")
        if not token:
            raise FlowFailure("An authenticated Host session is required; pair the fresh workspace or pass --bearer-token.")
        host_output.add_secret(token)

        status, workspace_snapshot, _ = json_request(arguments.base_url, "/api/workspace", token=token)
        require_status(status, 200, "GET workspace", workspace_snapshot)
        if workspace_snapshot.get("workspaceId") != workspace_id:
            raise FlowFailure("Workspace snapshot ID differs from bootstrap.")

        malformed_task = {
            "requestId": str(uuid.uuid4()),
            "title": None,
            "goal": "A null non-nullable request value must be rejected.",
            "criteria": ["Return a client error"],
            "inputText": "This malformed request must not create a task.",
            "mode": "development",
            "project": None,
        }
        status, malformed_result, _ = json_request(
            arguments.base_url, "/api/tasks", method="POST", body=malformed_task, token=token
        )
        require_status(status, 400, "POST task with a null required title", malformed_result)
        report["assertions"]["null_required_title_returns_400"] = "passed"

        connection_request_id = str(uuid.uuid4())
        connection = {
            "requestId": connection_request_id,
            "name": "M1 local protocol fixture",
            "protocol": "chat-completions",
            "endpoint": model_url,
            "model": "career-test-model",
            "apiKey": "integration-fixture-key",
            "allowLocalEndpoint": True,
            "maxOutputTokens": 1024,
            "maxModelCalls": 6,
            "maxInputCharacters": 8000,
        }
        host_output.add_secret(connection["apiKey"])
        status, saved_connection, _ = json_request(
            arguments.base_url, "/api/connection", method="PUT", body=connection, token=token
        )
        require_status(status, 200, "PUT connection", saved_connection)
        status, current_connection, _ = json_request(arguments.base_url, "/api/connection", token=token)
        require_status(status, 200, "GET connection", current_connection)
        if current_connection.get("hasCredential") is not True or "integration-fixture-key" in json.dumps(current_connection):
            raise FlowFailure("Connection response omitted credential state or disclosed its key.")

        status, test_result, _ = json_request(
            arguments.base_url,
            "/api/connection/test",
            method="POST",
            body={"requestId": str(uuid.uuid4())},
            token=token,
        )
        require_status(status, (200, 202), "POST connection test", test_result)
        deadline = time.monotonic() + arguments.flow_timeout
        while time.monotonic() < deadline:
            status, current_connection, _ = json_request(arguments.base_url, "/api/connection", token=token)
            require_status(status, 200, "poll connection", current_connection)
            state = current_connection.get("testState")
            if state == "verified":
                break
            if state == "failed":
                raise FlowFailure("Local model capability test failed.")
            time.sleep(0.25)
        else:
            raise FlowFailure("Timed out waiting for the local model capability test.")
        fixture_status, _, _ = json_request(
            f"http://127.0.0.1:{model_port}",
            "/__control",
            method="POST",
            body={"reapproveAfterDenial": True},
        )
        require_status(fixture_status, 200, "configure fixture for rejected-preview recovery", {})

        create_request_id = str(uuid.uuid4())
        create_body = {
            "requestId": create_request_id,
            "title": "M1 integration result",
            "goal": "Create a reviewable local integration artifact",
            "criteria": ["Include the goal", "Keep the output in Markdown"],
            "inputText": "Use the test-only local model fixture. Name the result m1-result.md.",
            "mode": "development",
            "project": None,
        }
        status, created, _ = json_request(
            arguments.base_url, "/api/tasks", method="POST", body=create_body, token=token
        )
        require_status(status, 201, "POST task", created)
        task_id = created.get("id")
        if not isinstance(task_id, str) or created.get("status") != "AwaitingPlan":
            raise FlowFailure("New task did not enter AwaitingPlan with an ID.")

        status, replay, _ = json_request(
            arguments.base_url, "/api/tasks", method="POST", body=create_body, token=token
        )
        require_status(status, (200, 201), "replay POST task", replay)
        if replay.get("id") != task_id or replay.get("version") != created.get("version"):
            raise FlowFailure("Idempotent task replay returned a different task or version.")

        changed_body = dict(create_body, title="Changed payload for the same request ID")
        status, conflict, _ = json_request(
            arguments.base_url, "/api/tasks", method="POST", body=changed_body, token=token
        )
        require_status(status, 409, "reuse request ID with changed payload", conflict)
        if conflict.get("code") != "IDEMPOTENCY_CONFLICT":
            raise FlowFailure("Changed-payload replay did not return IDEMPOTENCY_CONFLICT.")
        assert_task_receipt(arguments.base_url, token, create_request_id, task_id, int(created["version"]))

        task = task_from_api(arguments.base_url, task_id, token)
        status, _, _ = json_request(arguments.base_url, f"/api/tasks/{task_id}", token=None)
        require_status(status, 401, "unauthenticated task read", {})

        confirm = {
            "requestId": str(uuid.uuid4()),
            "expectedVersion": task["version"],
            "taskRevision": task["revision"],
            "proposalHash": task["plan"]["proposalHash"],
        }
        status, task, _ = json_request(
            arguments.base_url,
            f"/api/tasks/{urllib.parse.quote(task_id, safe='')}/confirm-plan",
            method="POST",
            body=confirm,
            token=token,
        )
        require_status(status, (200, 202), "POST confirm plan", task)

        task = wait_for_task(
            arguments.base_url,
            task_id,
            token,
            lambda value: value.get("status") == "WaitingApproval" and bool(value.get("approvals")),
            "a fixed publish_document approval",
            arguments.flow_timeout,
            host_process,
        )
        rejected_approval = task["approvals"][-1]
        rejection = {
            "requestId": str(uuid.uuid4()),
            "expectedVersion": task["version"],
            "payloadHash": rejected_approval["payloadHash"],
            "decision": "reject",
            "note": "Revise the preview before publication",
        }
        status, _, _ = json_request(
            arguments.base_url,
            f"/api/approvals/{urllib.parse.quote(rejected_approval['id'], safe='')}/decision",
            method="POST",
            body=rejection,
            token=token,
        )
        require_status(status, (200, 202), "POST approval rejection", {})
        task = wait_for_task(
            arguments.base_url,
            task_id,
            token,
            lambda value: value.get("status") == "WaitingApproval"
            and len(value.get("approvals", [])) > 1,
            "a revised approval after rejecting the first preview",
            arguments.flow_timeout,
            host_process,
        )
        if task.get("artifacts"):
            raise FlowFailure("Rejecting a preview published an artifact before approval.")
        artifacts_directory = workspace / "artifacts"
        if artifacts_directory.exists() and any(path.is_file() for path in artifacts_directory.rglob("*")):
            raise FlowFailure("Rejecting a preview wrote a file into the published artifact area.")
        approval = task["approvals"][-1]
        if approval["payloadHash"] == rejected_approval["payloadHash"]:
            raise FlowFailure("The revised approval reused the rejected payload hash.")
        decision = {
            "requestId": str(uuid.uuid4()),
            "expectedVersion": task["version"],
            "payloadHash": approval["payloadHash"],
            "decision": "approve",
            "note": "Approve the fixed integration artifact",
        }
        status, _, _ = json_request(
            arguments.base_url,
            f"/api/approvals/{urllib.parse.quote(approval['id'], safe='')}/decision",
            method="POST",
            body=decision,
            token=token,
        )
        require_status(status, (200, 202), "POST approval decision", {})

        task = wait_for_task(
            arguments.base_url,
            task_id,
            token,
            lambda value: value.get("status") == "InReview" and bool(value.get("artifacts")),
            "the published artifact entering review",
            arguments.flow_timeout,
            host_process,
        )
        artifact = task["artifacts"][-1]
        download_path = (
            f"/api/artifacts/{urllib.parse.quote(artifact['id'], safe='')}"
            f"/versions/{int(artifact['version'])}/download"
        )
        status, artifact_bytes, download_headers = bytes_request(arguments.base_url, download_path, token)
        if status != 200:
            raise FlowFailure(f"Artifact download returned HTTP {status}.")
        disposition = next((value for key, value in download_headers.items() if key.lower() == "content-disposition"), "")
        if disposition and "attachment" not in disposition.lower():
            raise FlowFailure("Artifact download was not returned as an attachment.")
        actual_hash = hashlib.sha256(artifact_bytes).hexdigest()
        if actual_hash != artifact["contentHash"]:
            raise FlowFailure("Downloaded artifact bytes do not match contentHash.")

        acceptance = {
            "requestId": str(uuid.uuid4()),
            "expectedVersion": task["version"],
            "taskRevision": task["revision"],
            "artifactId": artifact["id"],
            "artifactVersion": artifact["version"],
            "contentHash": artifact["contentHash"],
            "decision": "accept",
        }
        status, _, _ = json_request(
            arguments.base_url,
            f"/api/tasks/{urllib.parse.quote(task_id, safe='')}/acceptances",
            method="POST",
            body=acceptance,
            token=token,
        )
        require_status(status, (200, 202), "POST artifact acceptance", {})

        completed = wait_for_task(
            arguments.base_url,
            task_id,
            token,
            lambda value: value.get("status") == "Done"
            and any(
                item.get("decision") == "accept"
                and item.get("artifactId") == artifact["id"]
                and item.get("contentHash") == artifact["contentHash"]
                for item in value.get("acceptances", [])
            ),
            "the accepted task entering Done",
            arguments.flow_timeout,
            host_process,
        )
        settled_run = completed.get("runs", [])[-1]
        if (
            settled_run.get("usageState") != "confirmed-price-unknown"
            or settled_run.get("inputTokens", 0) <= 0
            or settled_run.get("outputTokens", 0) <= 0
            or settled_run.get("modelCalls", 0) < 1
        ):
            raise FlowFailure("The completed M1 run did not report confirmed, non-zero provider token usage.")
        report["assertions"]["main_m1_lifecycle_and_confirmed_usage"] = "passed"
        report["statistics"].update(
            {
                "completed_task_version": int(completed["version"]),
                "confirmed_model_calls": int(settled_run["modelCalls"]),
                "confirmed_input_tokens": int(settled_run["inputTokens"]),
                "confirmed_output_tokens": int(settled_run["outputTokens"]),
            }
        )
        artifact_path = workspace / "artifacts" / artifact["id"] / str(artifact["version"]) / artifact["fileName"]
        tampered_bytes = b"# tampered after acceptance\n"
        artifact_path.write_bytes(tampered_bytes)
        status, problem_bytes, _ = bytes_request(arguments.base_url, download_path, token)
        if status != 409:
            raise FlowFailure(f"Tampered artifact download returned HTTP {status}; expected 409.")
        problem = parse_json(problem_bytes)
        if problem.get("code") != "ARTIFACT_INTEGRITY_FAILURE":
            raise FlowFailure("Tampered artifact returned an unexpected integrity failure.")
        if artifact_path.read_bytes() != tampered_bytes:
            raise FlowFailure("Integrity failure did not preserve the tampered bytes for recovery.")
        fixture_status, metrics, _ = json_request(f"http://127.0.0.1:{model_port}", "/__metrics")
        require_status(fixture_status, 200, "GET fixture metrics", metrics)
        if metrics.get("toolCallResponses", 0) < 1 or metrics.get("textResponses", 0) < 2:
            raise FlowFailure("The local model fixture did not complete a tool approval and text response.")
        report["assertions"]["artifact_tamper_integrity_guard"] = "passed"
        report["statistics"].update(
            {
                "fixture_requests_before_extended_scenarios": int(metrics.get("requests", 0)),
                "fixture_text_responses_before_extended_scenarios": int(metrics.get("textResponses", 0)),
                "fixture_tool_responses_before_extended_scenarios": int(metrics.get("toolCallResponses", 0)),
            }
        )

        print(
            "PASS M1 lifecycle: paired workspace, verified model, idempotent task receipt, "
            "plan, fixed tool approval, artifact download/hash, acceptance, and Done "
            f"(task={task_id}, version={completed.get('version')})."
        )
        if not refresh_cookie:
            raise FlowFailure("The full integration suite requires the refresh cookie from fresh workspace pairing.")
        host_process, host_output, token, refresh_cookie = run_extended_scenarios(
            arguments.base_url,
            f"http://127.0.0.1:{model_port}",
            model_url,
            workspace,
            token,
            refresh_cookie,
            workspace_id,
            command,
            host_env,
            host_process,
            host_output,
            arguments.startup_timeout,
            arguments.flow_timeout,
            report,
        )
        report["status"] = "passed"
    finally:
        original_error = sys.exc_info()[1]
        stop_process(host_process)
        stop_process(model_process)
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
        shutil.rmtree(workspace, ignore_errors=True)
        if arguments.report:
            report_path = Path(arguments.report).expanduser()
            if not report_path.is_absolute():
                report_path = Path.cwd() / report_path
            report_path.parent.mkdir(parents=True, exist_ok=True)
            temporary_report = report_path.with_suffix(report_path.suffix + ".tmp")
            temporary_report.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
            os.replace(temporary_report, report_path)
            print(f"M1 integration report: {report_path.resolve()}")
    if cleanup_failure:
        raise FlowFailure(cleanup_failure)


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--host-command",
        required=True,
        help="Host command as one string; it must consume {workspace}. {base_url} and {model_url} are also substituted.",
    )
    parser.add_argument("--base-url", default="http://127.0.0.1:5128", help="Isolated Host URL.")
    parser.add_argument("--pairing-code", help="Provide the one-time code if Host output does not expose it in the documented label form.")
    parser.add_argument("--bearer-token", help="Use only when the isolated workspace is already paired.")
    parser.add_argument("--startup-timeout", type=float, default=30)
    parser.add_argument("--flow-timeout", type=float, default=90)
    parser.add_argument("--report", help="Write a credential-free status/assertion/statistics JSON report to this path.")
    return parser.parse_args()


def main() -> int:
    try:
        run_flow(parse_arguments())
        return 0
    except (FlowFailure, urllib.error.URLError, OSError, KeyError, ValueError) as error:
        print(f"FAIL M1 lifecycle: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
