#!/usr/bin/env python3
"""Prove an acknowledged publication survives loss of the worker's final response."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import sys
import tempfile
import uuid

import m1_flow as flow
from keychain_cleanup import cleanup_keychain_item


def write_wrapper(path: Path, worker_command: list[str], marker: Path) -> None:
    code = '''import json, pathlib, subprocess, sys, threading
child = subprocess.Popen(WORKER_COMMAND, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True)
def forward_input():
    try:
        for line in sys.stdin:
            child.stdin.write(line)
            child.stdin.flush()
    except (BrokenPipeError, ValueError):
        pass
threading.Thread(target=forward_input, daemon=True).start()
try:
    for line in child.stdout:
        value = json.loads(line)
        if value.get('id') == 'run' and (value.get('result') or {}).get('status') == 'Succeeded':
            pathlib.Path(MARKER).write_text('final-response-dropped')
            break
        sys.stdout.write(line)
        sys.stdout.flush()
finally:
    if child.poll() is None:
        child.kill()
    child.wait(timeout=5)
'''
    path.write_text(
        f"#!{sys.executable}\nWORKER_COMMAND = {worker_command!r}\nMARKER = {str(marker)!r}\n" + code,
        encoding="utf-8",
    )
    path.chmod(0o700)


def run(args: argparse.Namespace) -> dict[str, object]:
    if "{workspace}" not in args.host_command or "{worker}" not in args.host_command:
        raise flow.FlowFailure("Host command must consume isolated {workspace} and {worker} placeholders.")
    root = Path(tempfile.mkdtemp(prefix="career-finalization-"))
    workspace = root / "workspace"
    marker = root / "dropped-final"
    wrapper = root / "drop-final-worker"
    write_wrapper(wrapper, shlex.split(args.worker_command), marker)
    report: dict[str, object] = {"status": "failed", "assertions": {}}
    model = host = None
    workspace_id = None
    try:
        fixture = Path(__file__).resolve().parents[1] / "fixtures" / "model-server.py"
        model_port, host_port = flow.reserve_port(), flow.reserve_port()
        model_url, base_url = f"http://127.0.0.1:{model_port}", f"http://127.0.0.1:{host_port}"
        env = os.environ.copy()
        model = flow.launch([sys.executable, str(fixture), "--port", str(model_port)], env, subprocess.DEVNULL)
        flow.wait_json(model_url, "/__metrics", None, 15, model)
        host_command = [part.replace("{workspace}", str(workspace)).replace("{worker}", str(wrapper)).replace("{port}", str(host_port))
                        for part in shlex.split(args.host_command)]
        host = flow.launch(host_command, env)
        output = flow.HostOutput(host)
        setattr(host, "_career_output", output)
        bootstrap = flow.wait_json(base_url, "/api/bootstrap", None, 30, host)
        workspace_id = bootstrap["workspaceId"]
        status, session, _ = flow.json_request(base_url, "/api/auth/pair", "POST", {"code": output.code.get(timeout=10)})
        flow.require_status(status, 200, "owner pairing", session)
        token = session["accessToken"]
        output.add_secret(token)
        connection = {"requestId": str(uuid.uuid4()), "name": "Final response loss fixture", "protocol": "chat-completions",
                      "endpoint": model_url + "/v1", "model": "career-test-model", "apiKey": "fixture-only",
                      "allowLocalEndpoint": True, "maxOutputTokens": 1024, "maxModelCalls": 6, "maxInputCharacters": 8000}
        status, saved, _ = flow.json_request(base_url, "/api/connection", "PUT", connection, token)
        flow.require_status(status, 200, "save fixture connection", saved)
        status, tested, _ = flow.json_request(base_url, "/api/connection/test", "POST", {"requestId": str(uuid.uuid4())}, token, 65)
        flow.require_status(status, 200, "test fixture connection", tested)
        if tested.get("testState") != "verified":
            raise flow.FlowFailure("The isolated fixture connection did not pass its native approval test.")
        flow.fixture_request(model_url, "/__control", method="POST", body={"reset": True, "failAfterPublication": True})
        task_id = flow.create_confirmed_task(base_url, token, "Publication with missing worker finale", "finalization.md")
        pending = flow.wait_for_task(base_url, task_id, token, lambda t: t.get("status") == "WaitingApproval", "fixed publication approval", 30, host)
        flow.decide_approval(base_url, pending, pending["approvals"][-1], token, "approve", "Check recovery after publication.")
        review = flow.wait_for_task(base_url, task_id, token, lambda t: t.get("status") == "InReview", "Host reconciliation after missing final response", 30, host)
        if not marker.is_file() or marker.read_text() != "final-response-dropped":
            raise flow.FlowFailure("The fixture did not actually discard the worker final response.")
        if flow.fixture_metrics(model_url).get("postPublicationFailures") != 1:
            raise flow.FlowFailure("The provider fixture did not actually fail after the acknowledged publication.")
        artifacts = review.get("artifacts", [])
        if len(artifacts) != 1 or review["runs"][-1]["status"] != "Succeeded":
            raise flow.FlowFailure("Reconciliation changed publication count or execution result.")
        artifact = artifacts[0]
        files = list((workspace / "artifacts").rglob("*.md"))
        if len(files) != 1 or hashlib.sha256(files[0].read_bytes()).hexdigest() != artifact["contentHash"]:
            raise flow.FlowFailure("The original publication bytes were changed or duplicated.")
        acceptance = {"requestId": str(uuid.uuid4()), "expectedVersion": review["version"], "taskRevision": review["revision"],
                      "artifactId": artifact["id"], "artifactVersion": artifact["version"], "contentHash": artifact["contentHash"], "decision": "accept"}
        status, done, _ = flow.json_request(base_url, f"/api/tasks/{task_id}/acceptances", "POST", acceptance, token)
        flow.require_status(status, 200, "accept reconciled artifact", done)
        if done.get("status") != "Done" or len(done.get("artifacts", [])) != 1 or len(done.get("acceptances", [])) != 1:
            raise flow.FlowFailure("The reconciled fixed version could not complete independent acceptance.")
        report.update(status="passed", assertions={"post_publication_provider_failure_preserves_success": "passed", "worker_final_response_actually_dropped": "passed", "host_reconciles_original_publication": "passed",
                      "one_original_file_and_hash": "passed", "independent_fixed_version_acceptance": "passed"})
        print("PASS final response loss: original publication reconciled, one fixed file, independent acceptance.")
    finally:
        primary_error = sys.exc_info()[1]
        cleanup_errors = []
        for process in (host, model):
            try:
                flow.stop_process(process)
            except Exception as error:
                cleanup_errors.append("process cleanup: " + type(error).__name__)
        try:
            cleanup = cleanup_keychain_item(workspace_id) if sys.platform == "darwin" else {"status": "not-applicable"}
            report["keychainCleanup"] = cleanup
            if cleanup["status"] == "failed":
                cleanup_errors.append("Temporary Keychain item cleanup could not be verified.")
        except Exception as error:
            cleanup_errors.append("Keychain cleanup: " + type(error).__name__)
            report.setdefault("keychainCleanup", {"status": "failed"})
        finally:
            shutil.rmtree(root, ignore_errors=True)
            if root.exists():
                cleanup_errors.append("Temporary workspace cleanup could not be verified.")
            if cleanup_errors:
                report["status"] = "failed"
                report["cleanupErrors"] = cleanup_errors
            if primary_error is not None:
                report["primaryFailureType"] = type(primary_error).__name__
            if args.report:
                destination = Path(args.report).resolve()
                destination.parent.mkdir(parents=True, exist_ok=True)
                destination.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        if cleanup_errors and primary_error is None:
            raise flow.FlowFailure("; ".join(cleanup_errors))
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host-command", required=True)
    parser.add_argument("--worker-command", required=True)
    parser.add_argument("--report")
    arguments = parser.parse_args()
    try:
        run(arguments)
    except Exception as error:
        print(f"FAIL final response loss: {type(error).__name__}: {error}", file=sys.stderr)
        raise SystemExit(1)
