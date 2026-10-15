#!/usr/bin/env python3
"""Check that switching macOS app identity cannot hang or replace an existing vault key.

The SDK host creates an isolated workspace and exercises the same-process Keychain
round-trip. A separate native host then tries to read that workspace's credential;
the read must fail promptly without contacting the model fixture or changing the
encrypted vault. Finally, the original SDK host identity must still be able to read
and use the stored credential.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import queue
import shlex
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import uuid
from pathlib import Path
from typing import Any

import m1_flow as flow
from keychain_cleanup import cleanup_keychain_item


class KeychainFlowFailure(RuntimeError):
    pass


def command_for(template: str, workspace: Path, base_url: str, model_url: str) -> list[str]:
    parts = shlex.split(template)
    if not parts or not any("{workspace}" in part for part in parts):
        raise KeychainFlowFailure("Each Host command must include the {workspace} placeholder.")
    substitutions = {
        "{workspace}": str(workspace),
        "{base_url}": base_url,
        "{model_url}": model_url,
    }
    return [
        part.replace("{workspace}", substitutions["{workspace}"])
        .replace("{base_url}", substitutions["{base_url}"])
        .replace("{model_url}", substitutions["{model_url}"])
        for part in parts
    ]


def start_host(
    template: str,
    workspace: Path,
    base_url: str,
    model_url: str,
    timeout: float,
) -> tuple[subprocess.Popen[str], flow.HostOutput]:
    environment = os.environ.copy()
    environment["ASPNETCORE_URLS"] = base_url
    process = flow.launch(command_for(template, workspace, base_url, model_url), environment)
    output = flow.HostOutput(process)
    setattr(process, "_career_output", output)
    try:
        bootstrap = flow.wait_json(base_url, "/api/bootstrap", None, timeout, process)
        if not isinstance(bootstrap.get("workspaceId"), str) or not bootstrap["workspaceId"]:
            raise KeychainFlowFailure("Host bootstrap did not expose the isolated workspace identity.")
        return process, output
    except Exception:
        flow.stop_process(process)
        raise


def pair_fresh_host(
    base_url: str,
    workspace_id: str,
    output: flow.HostOutput,
    timeout: float,
) -> str:
    try:
        pairing_code = output.code.get(timeout=min(timeout, 10))
    except queue.Empty as error:
        raise KeychainFlowFailure("Fresh SDK workspace did not produce its one-time pairing code.") from error
    output.add_secret(pairing_code)
    status, session, _ = flow.json_request(
        base_url,
        "/api/auth/pair",
        method="POST",
        body={"code": pairing_code},
        timeout=timeout,
    )
    flow.require_status(status, 200, "pair fresh SDK workspace", session)
    token = session.get("accessToken")
    if not isinstance(token, str) or not token or session.get("workspaceId") != workspace_id:
        raise KeychainFlowFailure("Pairing did not establish an owner session for the isolated workspace.")
    output.add_secret(token)
    return token


def wait_for_execution(base_url: str, token: str, process: subprocess.Popen[str], timeout: float) -> None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise KeychainFlowFailure(f"Host exited before execution recovery completed ({process.returncode}).")
        status, snapshot, _ = flow.json_request(base_url, "/api/workspace", token=token, timeout=1)
        if status == 200 and snapshot.get("executionAvailable") is True:
            return
        time.sleep(0.1)
    raise KeychainFlowFailure("Host did not finish workspace execution recovery in time.")


def save_fixture_connection(base_url: str, token: str, model_url: str, output: flow.HostOutput) -> None:
    api_key = "keychain-noninteractive-fixture-key"
    output.add_secret(api_key)
    request = {
        "requestId": str(uuid.uuid4()),
        "name": "Keychain identity fixture",
        "protocol": "chat-completions",
        "endpoint": model_url,
        "model": "career-test-model",
        "apiKey": api_key,
        "allowLocalEndpoint": True,
        "maxOutputTokens": 1024,
        "maxModelCalls": 6,
        "maxInputCharacters": 8000,
    }
    status, connection, _ = flow.json_request(
        base_url,
        "/api/connection",
        method="PUT",
        body=request,
        token=token,
        timeout=10,
    )
    flow.require_status(status, 200, "save same-identity model fixture", connection)
    if connection.get("hasCredential") is not True:
        raise KeychainFlowFailure("SDK host did not round-trip its new Keychain item while saving the connection.")


def test_connection(base_url: str, token: str, timeout: float) -> dict[str, Any]:
    status, result, _ = flow.json_request(
        base_url,
        "/api/connection/test",
        method="POST",
        body={"requestId": str(uuid.uuid4())},
        token=token,
        timeout=timeout,
    )
    flow.require_status(status, 200, "test stored Keychain credential", result)
    return result


def has_worker_child(process_id: int) -> bool:
    children = subprocess.run(
        ["/usr/bin/pgrep", "-P", str(process_id)],
        check=False,
        capture_output=True,
        text=True,
        timeout=1,
    ).stdout.split()
    for child in children:
        command = subprocess.run(
            ["/bin/ps", "-p", child, "-o", "command="],
            check=False,
            capture_output=True,
            text=True,
            timeout=1,
        ).stdout
        if "Career.AgentWorker" in command:
            return True
    return False


def test_native_connection_without_worker(
    base_url: str,
    token: str,
    process: subprocess.Popen[str],
    timeout: float,
) -> tuple[dict[str, Any], int]:
    results: queue.Queue[tuple[dict[str, Any] | None, BaseException | None, int]] = queue.Queue(maxsize=1)

    def request() -> None:
        started = time.monotonic()
        try:
            status, result, _ = flow.json_request(
                base_url,
                "/api/connection/test",
                method="POST",
                body={"requestId": str(uuid.uuid4())},
                token=token,
                timeout=timeout,
            )
            flow.require_status(status, 200, "native test of stored Keychain credential", result)
            results.put((result, None, int((time.monotonic() - started) * 1000)))
        except BaseException as error:
            results.put((None, error, int((time.monotonic() - started) * 1000)))

    caller = threading.Thread(target=request, daemon=True)
    caller.start()
    observed_worker = False
    deadline = time.monotonic() + timeout + 1
    while caller.is_alive() and time.monotonic() < deadline:
        if process.poll() is not None:
            raise KeychainFlowFailure(f"Native Host exited during the Keychain-read request ({process.returncode}).")
        observed_worker |= has_worker_child(process.pid)
        caller.join(0.025)
    caller.join(0)
    if caller.is_alive():
        raise KeychainFlowFailure("Native Keychain-read request did not return within the noninteractive time bound.")
    result, error, elapsed_ms = results.get_nowait()
    if error is not None:
        raise KeychainFlowFailure(f"Native Keychain-read request failed before returning an API result: {error!r}") from error
    if observed_worker or has_worker_child(process.pid):
        raise KeychainFlowFailure("Native Host started a Worker process even though its Keychain read should be denied.")
    assert result is not None
    return result, elapsed_ms


def vault_hash(workspace: Path) -> str:
    path = workspace / "state" / "vault.enc"
    if not path.is_file():
        raise KeychainFlowFailure("Connection save did not create the isolated encrypted vault.")
    return hashlib.sha256(path.read_bytes()).hexdigest()


def reset_fixture(model_base_url: str) -> None:
    flow.fixture_request(
        model_base_url,
        "/__control",
        method="POST",
        body={"reset": True, "delayMs": 0},
    )


def write_report(path_text: str | None, report: dict[str, Any]) -> None:
    if not path_text:
        return
    path = Path(path_text).expanduser()
    if not path.is_absolute():
        path = Path.cwd() / path
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    os.replace(temporary, path)
    print(f"Keychain noninteractive report: {path.resolve()}")


def run(arguments: argparse.Namespace) -> None:
    if not sys.platform.startswith("darwin"):
        raise KeychainFlowFailure("The noninteractive Keychain integration check requires macOS.")
    workspace = Path(tempfile.mkdtemp(prefix="career-keychain-noninteractive-"))
    fixture = Path(__file__).resolve().parents[1] / "fixtures" / "model-server.py"
    model_port = flow.reserve_port()
    model_process: subprocess.Popen[str] | None = None
    sdk_process: subprocess.Popen[str] | None = None
    native_process: subprocess.Popen[str] | None = None
    sdk_output: flow.HostOutput | None = None
    native_output: flow.HostOutput | None = None
    workspace_id: str | None = None
    report: dict[str, Any] = {"status": "failed", "assertions": {}, "statistics": {}}
    cleanup_failure: str | None = None
    model_base_url = f"http://127.0.0.1:{model_port}"
    model_url = f"{model_base_url}/v1"
    try:
        model_process = flow.launch(
            [sys.executable, str(fixture), "--host", "127.0.0.1", "--port", str(model_port)],
            os.environ.copy(),
            stdout=subprocess.DEVNULL,
        )
        flow.wait_json(model_base_url, "/health", None, arguments.startup_timeout, model_process)

        sdk_process, sdk_output = start_host(
            arguments.sdk_host_command,
            workspace,
            arguments.base_url,
            model_url,
            arguments.startup_timeout,
        )
        bootstrap = flow.wait_json(arguments.base_url, "/api/bootstrap", None, arguments.startup_timeout, sdk_process)
        workspace_id = bootstrap["workspaceId"]
        token = pair_fresh_host(arguments.base_url, workspace_id, sdk_output, arguments.startup_timeout)
        save_fixture_connection(arguments.base_url, token, model_url, sdk_output)
        sdk_test = test_connection(arguments.base_url, token, arguments.flow_timeout)
        if sdk_test.get("testState") != "verified":
            raise KeychainFlowFailure("The original SDK host could not read and verify its newly stored Keychain credential.")
        sdk_metrics = flow.fixture_metrics(model_base_url)
        if sdk_metrics.get("requests", 0) < 1:
            raise KeychainFlowFailure("The same-identity capability test did not reach the model fixture.")
        report["assertions"]["sdk_same_caller_roundtrip"] = "passed"
        report["statistics"]["sdk_fixture_requests"] = int(sdk_metrics.get("requests", 0))
        original_vault_hash = vault_hash(workspace)
        vault_size = (workspace / "state" / "vault.enc").stat().st_size
        flow.stop_process(sdk_process)
        sdk_process = None

        reset_fixture(model_base_url)
        native_process, native_output = start_host(
            arguments.native_host_command,
            workspace,
            arguments.base_url,
            model_url,
            arguments.startup_timeout,
        )
        native_bootstrap = flow.wait_json(
            arguments.base_url, "/api/bootstrap", None, arguments.startup_timeout, native_process
        )
        if native_bootstrap.get("workspaceId") != workspace_id or native_bootstrap.get("hasOwner") is not True:
            raise KeychainFlowFailure("Native Host did not reopen the same workspace with its owner initialized.")
        if native_output is not None:
            native_output.add_secret(token)
        wait_for_execution(arguments.base_url, token, native_process, arguments.startup_timeout)
        native_result, elapsed_ms = test_native_connection_without_worker(
            arguments.base_url,
            token,
            native_process,
            min(arguments.interaction_timeout, 5.0),
        )
        if native_result.get("testState") != "failed":
            raise KeychainFlowFailure("The different native binary unexpectedly read the SDK-owned Keychain credential.")
        if elapsed_ms > arguments.interaction_timeout * 1000:
            raise KeychainFlowFailure("The denied native Keychain read exceeded the noninteractive time bound.")
        metrics_after_native = flow.fixture_metrics(model_base_url)
        if metrics_after_native.get("requests", 0) != 0:
            raise KeychainFlowFailure("The native binary reached the provider instead of failing at Keychain access.")
        report["assertions"]["different_binary_denied_without_provider_call"] = "passed"
        report["statistics"]["native_denial_elapsed_ms"] = elapsed_ms
        report["statistics"]["provider_requests_after_native_attempt"] = 0
        flow.stop_process(native_process)
        native_process = None

        sdk_process, sdk_output = start_host(
            arguments.sdk_host_command,
            workspace,
            arguments.base_url,
            model_url,
            arguments.startup_timeout,
        )
        reopened_bootstrap = flow.wait_json(
            arguments.base_url, "/api/bootstrap", None, arguments.startup_timeout, sdk_process
        )
        if reopened_bootstrap.get("workspaceId") != workspace_id or reopened_bootstrap.get("hasOwner") is not True:
            raise KeychainFlowFailure("Original SDK Host did not reopen the same owner workspace after native denial.")
        sdk_output.add_secret(token)
        wait_for_execution(arguments.base_url, token, sdk_process, arguments.startup_timeout)
        recovered_sdk_test = test_connection(arguments.base_url, token, arguments.flow_timeout)
        if recovered_sdk_test.get("testState") != "verified":
            raise KeychainFlowFailure("The original SDK identity could not read its credential after the native denial.")
        if vault_hash(workspace) != original_vault_hash:
            raise KeychainFlowFailure("The encrypted vault bytes changed during the denied native Keychain read.")
        final_metrics = flow.fixture_metrics(model_base_url)
        if final_metrics.get("requests", 0) < 1:
            raise KeychainFlowFailure("The original SDK identity did not reach the fixture after native denial.")
        report["assertions"]["original_key_and_vault_preserved"] = "passed"
        report["assertions"]["vault_hash_unchanged"] = "passed"
        report["statistics"]["vault_size_bytes"] = vault_size
        report["statistics"]["provider_requests_after_sdk_recovery"] = int(final_metrics.get("requests", 0))
        report["status"] = "passed"
        print("PASS Keychain noninteractive: same-identity round-trip, cross-binary fast denial, unchanged vault, SDK recovery.")
    finally:
        original_error = sys.exc_info()[1]
        flow.stop_process(sdk_process)
        flow.stop_process(native_process)
        flow.stop_process(model_process)
        keychain_result = cleanup_keychain_item(workspace_id)
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
        write_report(arguments.report, report)
    if cleanup_failure:
        raise KeychainFlowFailure(cleanup_failure)


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sdk-host-command", required=True, help="SDK host command, including the {workspace} placeholder.")
    parser.add_argument("--native-host-command", required=True, help="Different native host command, including {workspace}.")
    parser.add_argument("--base-url", default="http://127.0.0.1:5129", help="Isolated Host URL reused for both binaries.")
    parser.add_argument("--startup-timeout", type=float, default=30)
    parser.add_argument("--flow-timeout", type=float, default=60)
    parser.add_argument("--interaction-timeout", type=float, default=3)
    parser.add_argument("--report", help="Write a credential-free status/assertion/statistics report to this path.")
    return parser.parse_args()


def main() -> int:
    try:
        run(parse_arguments())
        return 0
    except (
        KeychainFlowFailure,
        flow.FlowFailure,
        urllib.error.URLError,
        OSError,
        KeyError,
        ValueError,
        TimeoutError,
        subprocess.SubprocessError,
    ) as error:
        print(f"FAIL Keychain noninteractive: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
