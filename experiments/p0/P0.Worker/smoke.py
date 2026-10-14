#!/usr/bin/env python3
"""Black-box stdio probe for a restart-safe MAF approval round trip."""
import asyncio
import argparse
import hashlib
import json
import math
import os
import signal
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
PROJECT = Path(__file__).resolve().parent / "P0.Worker.csproj"
PROMPT = "Summarize the project README"

parser = argparse.ArgumentParser(description="Exercise the P0.Worker stdio protocol.")
parser.add_argument(
    "--worker",
    type=Path,
    help="absolute path to a published Worker DLL (default: dotnet run --no-build development entry point)",
)
parser.add_argument(
    "--timeout-seconds",
    type=float,
    default=15.0,
    help="maximum total time for each Worker process (default: 15 seconds)",
)
args = parser.parse_args()
if not math.isfinite(args.timeout_seconds) or args.timeout_seconds <= 0:
    parser.error("--timeout-seconds must be a positive finite number")
WORKER_DLL = args.worker
if WORKER_DLL is not None:
    if not WORKER_DLL.is_absolute():
        parser.error("--worker must be an absolute path to a published DLL")
    if WORKER_DLL.suffix.lower() != ".dll":
        parser.error("--worker must point to a .dll file")
    try:
        WORKER_DLL = WORKER_DLL.resolve(strict=True)
    except FileNotFoundError:
        parser.error(f"published Worker DLL does not exist: {WORKER_DLL}")
    if not WORKER_DLL.is_file():
        parser.error(f"published Worker path is not a file: {WORKER_DLL}")
    WORKER_SHA256 = hashlib.sha256(WORKER_DLL.read_bytes()).hexdigest()
    print(f"Worker binary: {WORKER_DLL} (sha256={WORKER_SHA256})")
else:
    print(f"Worker binary: development entry point (dotnet run --no-build --project {PROJECT})")


def worker_command():
    if WORKER_DLL is not None:
        return ["dotnet", str(WORKER_DLL)]
    return ["dotnet", "run", "--no-build", "--project", str(PROJECT)]


async def _stop_owned_process(process, force_group_cleanup=False):
    if process.returncode is not None and (os.name == "nt" or not force_group_cleanup):
        return
    try:
        if os.name == "nt":
            process.terminate()
        else:
            os.killpg(process.pid, signal.SIGTERM)
    except ProcessLookupError:
        pass
    if os.name != "nt" and force_group_cleanup:
        try:
            if process.returncode is None:
                await asyncio.wait_for(process.wait(), timeout=0.5)
        except asyncio.TimeoutError:
            pass
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        if process.returncode is None:
            try:
                await asyncio.wait_for(process.wait(), timeout=1.0)
            except asyncio.TimeoutError as error:
                raise RuntimeError(f"Could not reap Worker process {process.pid} after forced termination") from error
        return
    if process.returncode is None:
        try:
            if os.name == "nt":
                process.kill()
            else:
                os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        try:
            await asyncio.wait_for(process.wait(), timeout=1.0)
        except asyncio.TimeoutError as error:
            raise RuntimeError(f"Could not reap Worker process {process.pid} after forced termination") from error


async def run_worker(request, host_result=None, host_error=None, expect_failure=False):
    loop = asyncio.get_running_loop()
    deadline = loop.time() + args.timeout_seconds

    async def within_deadline(awaitable):
        remaining = deadline - loop.time()
        if remaining <= 0:
            raise TimeoutError(f"Worker exceeded the {args.timeout_seconds:g}s process deadline")
        try:
            return await asyncio.wait_for(awaitable, timeout=remaining)
        except asyncio.TimeoutError as error:
            raise TimeoutError(f"Worker exceeded the {args.timeout_seconds:g}s process deadline") from error

    process_options = {"creationflags": subprocess.CREATE_NEW_PROCESS_GROUP} if os.name == "nt" else {"start_new_session": True}
    process = await within_deadline(
        asyncio.create_subprocess_exec(
            *worker_command(),
            cwd=ROOT,
            stdin=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
            **process_options,
        )
    )
    stderr_task = asyncio.create_task(process.stderr.read())
    lines = []
    completed = False
    try:
        process.stdin.write((json.dumps(request) + "\n").encode("utf-8"))
        await within_deadline(process.stdin.drain())
        while True:
            raw_line = await within_deadline(process.stdout.readline())
            if not raw_line:
                break
            line = raw_line.decode("utf-8")
            item = json.loads(line)
            lines.append(item)
            if item.get("method") == "tool.execute":
                assert item["id"] == "tool-1"
                assert host_result is not None or host_error is not None, f"unexpected host side effect request: {item}"
                host_response = {"jsonrpc": "2.0", "id": "tool-1"}
                if host_error is not None:
                    host_response["error"] = host_error
                else:
                    host_response["result"] = host_result
                process.stdin.write((json.dumps(host_response) + "\n").encode("utf-8"))
                await within_deadline(process.stdin.drain())
        process.stdin.close()
        try:
            await within_deadline(process.stdin.wait_closed())
        except (BrokenPipeError, ConnectionResetError):
            pass
        code = await within_deadline(process.wait())
        stderr = (await within_deadline(stderr_task)).decode("utf-8", errors="replace")
        completed = True
    finally:
        if not process.stdin.is_closing():
            process.stdin.close()
        if not completed:
            await _stop_owned_process(process, force_group_cleanup=True)
        if not stderr_task.done():
            try:
                await asyncio.wait_for(stderr_task, timeout=1.0)
            except asyncio.TimeoutError:
                stderr_task.cancel()
                try:
                    await stderr_task
                except asyncio.CancelledError:
                    pass
    if code and expect_failure:
        return None, lines, stderr
    if code:
        raise RuntimeError(f"worker exit={code}; stdout={lines}; stderr={stderr}")
    responses = [item for item in lines if item.get("id") == "run" and "result" in item]
    assert len(responses) == 1, f"expected one final agent.run result: {lines}; stderr={stderr}"
    return responses[0]["result"], lines


def request(**params):
    return {"jsonrpc": "2.0", "id": "run", "method": "agent.run", "params": params}


async def main():
    initial, _ = await run_worker(request(runId="smoke-run", operationId="smoke-op", prompt=PROMPT, session=None, approval=None, approved=None))
    assert initial["status"] == "WaitingApproval", initial
    assert initial["toolName"] == "record_note", initial
    assert initial["callId"] == "call-note-1", initial
    assert initial["arguments"] == {"text": PROMPT}, initial

    resumed, host_lines = await run_worker(
        request(
            runId="smoke-run",
            operationId="smoke-op",
            prompt=PROMPT,
            session=initial["session"],
            approval=initial["approval"],
            approved=True,
        ),
        {"receiptId": "receipt-123", "text": "host recorded the note"},
    )
    assert resumed["status"] == "Succeeded", resumed
    assert "receipt-123" in resumed["text"], resumed
    assert resumed["toolCalls"] == 1, resumed
    assert len([item for item in host_lines if item.get("method") == "tool.execute"]) == 1, host_lines
    assert initial["workerPid"] != resumed["workerPid"], (initial["workerPid"], resumed["workerPid"])

    denied, denied_lines = await run_worker(
        request(
            runId="smoke-run",
            operationId="smoke-op",
            prompt=PROMPT,
            session=initial["session"],
            approval=initial["approval"],
            approved=False,
        )
    )
    assert denied["status"] == "Denied", denied
    assert not any(item.get("method") == "tool.execute" for item in denied_lines), denied_lines

    host_failed, failed_lines, failed_stderr = await run_worker(
        request(
            runId="smoke-run",
            operationId="smoke-op",
            prompt=PROMPT,
            session=initial["session"],
            approval=initial["approval"],
            approved=True,
        ),
        host_error={"code": -32001, "message": "simulated host rejection"},
        expect_failure=True,
    )
    assert host_failed is None and "Host rejected tool.execute" in failed_stderr, failed_stderr
    assert not any(item.get("id") == "run" and "result" in item for item in failed_lines), failed_lines

    invalid_initial, _ = await run_worker(
        request(runId="invalid-run", operationId="invalid-op", prompt=PROMPT, scenario="invalid-arguments", session=None, approval=None, approved=None)
    )
    assert invalid_initial["status"] == "WaitingApproval", invalid_initial
    invalid_resume, invalid_lines = await run_worker(
        request(
            runId="invalid-run",
            operationId="invalid-op",
            prompt=PROMPT,
            scenario="invalid-arguments",
            session=invalid_initial["session"],
            approval=invalid_initial["approval"],
            approved=True,
        )
    )
    assert not any(item.get("method") == "tool.execute" for item in invalid_lines), invalid_lines
    assert invalid_resume["toolCalls"] == 1, invalid_resume
    assert "rejected" in invalid_resume["text"], invalid_resume

    endless, endless_lines, endless_stderr = await run_worker(
        request(runId="endless-run", operationId="endless-op", prompt=PROMPT, scenario="endless", session=None, approval=None, approved=None),
        expect_failure=True,
    )
    assert endless is None and not endless_lines and "unresolved tool call 'add'" in endless_stderr, endless_stderr

    print("PASS: MAF tool loop, approval JSON round-trip, session restart, host receipt continuation")
    print("PASS: approval denial avoids tool execution; host errors return no success result")
    print("PASS: malformed record_note arguments rejected before host side effect")
    print("PASS: repeated calls stop at MAF cap and unresolved tool call fails closed")


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except Exception as error:
        raise SystemExit(f"FAIL: {error}") from error
