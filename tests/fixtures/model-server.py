#!/usr/bin/env python3
"""A test-only OpenAI Chat Completions endpoint for Career.AgentWorker."""

from __future__ import annotations

import argparse
import json
import re
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any


class ServerState:
    def __init__(self, delay_ms: int, fail_first: int) -> None:
        self.lock = threading.RLock()
        self.delay_ms = delay_ms
        self.fail_next = fail_first
        self.fail_after_publication = False
        self.reapprove_after_denial = False
        self.requests = 0
        self.failures = 0
        self.post_publication_failures = 0
        self.successes = 0
        self.tool_call_responses = 0
        self.text_responses = 0

    def snapshot(self) -> dict[str, Any]:
        with self.lock:
            return {
                "requests": self.requests,
                "failures": self.failures,
                "postPublicationFailures": self.post_publication_failures,
                "successes": self.successes,
                "toolCallResponses": self.tool_call_responses,
                "textResponses": self.text_responses,
                "failNext": self.fail_next,
                "failAfterPublication": self.fail_after_publication,
                "delayMs": self.delay_ms,
                "reapproveAfterDenial": self.reapprove_after_denial,
            }

    def configure(self, value: dict[str, Any]) -> dict[str, Any]:
        with self.lock:
            if value.get("reset"):
                self.requests = 0
                self.failures = 0
                self.post_publication_failures = 0
                self.successes = 0
                self.tool_call_responses = 0
                self.text_responses = 0
            if "delayMs" in value:
                self.delay_ms = max(0, min(int(value["delayMs"]), 600_000))
            if "failNext" in value:
                self.fail_next = max(0, min(int(value["failNext"]), 100))
            if "failAfterPublication" in value:
                self.fail_after_publication = bool(value["failAfterPublication"])
            if "reapproveAfterDenial" in value:
                self.reapprove_after_denial = bool(value["reapproveAfterDenial"])
            return self.snapshot()


def extract_text(value: Any) -> str:
    if isinstance(value, str):
        return value
    if isinstance(value, list):
        return "\n".join(extract_text(item) for item in value)
    if isinstance(value, dict):
        if value.get("type") in ("text", "input_text", "output_text") and isinstance(value.get("text"), str):
            return value["text"]
        return "\n".join(extract_text(item) for item in value.values())
    return ""


def tool_definition(payload: dict[str, Any]) -> dict[str, Any] | None:
    for tool in payload.get("tools") or []:
        if tool.get("type") != "function":
            continue
        function = tool.get("function") or {}
        if function.get("name") == "publish_document":
            return function
    return None


def contains_rejection(value: Any) -> bool:
    if isinstance(value, dict):
        approved = value.get("approved")
        if approved is False:
            return True
        if any(word in str(key).lower() for key in value for word in ("approval", "decision")):
            marker = str(value).lower()
            if any(word in marker for word in ("denied", "rejected", "declined")):
                return True
        return any(contains_rejection(item) for item in value.values())
    if isinstance(value, list):
        return any(contains_rejection(item) for item in value)
    if isinstance(value, str):
        marker = value.lower()
        return any(word in marker for word in ("approval rejected", "approval denied", "tool declined", "not approved"))
    return False


def has_successful_publication_receipt(value: Any) -> bool:
    if isinstance(value, str):
        try:
            return has_successful_publication_receipt(json.loads(value))
        except json.JSONDecodeError:
            return False
    if isinstance(value, dict):
        normalized = {str(key).casefold(): item for key, item in value.items()}
        if (
            str(normalized.get("status", "")).casefold() == "succeeded"
            and normalized.get("artifactid")
            and normalized.get("contenthash")
        ):
            return True
        return any(has_successful_publication_receipt(item) for item in value.values())
    if isinstance(value, list):
        return any(has_successful_publication_receipt(item) for item in value)
    return False


def latest_publication_outcome(messages: list[dict[str, Any]]) -> str | None:
    """Return only the latest approval/tool outcome, so an old rejection cannot undo a receipt."""
    for message in reversed(messages):
        if message.get("role") == "tool" and has_successful_publication_receipt(message.get("content")):
            return "published"
        if message.get("role") == "tool" and any(
            marker in extract_text(message.get("content")).casefold()
            for marker in ("denied", "rejected", "declined")
        ):
            return "rejected"
        if contains_rejection(message):
            return "rejected"
    return None


def current_document(messages: list[dict[str, Any]]) -> tuple[str, str]:
    user_texts = [extract_text(message.get("content")) for message in messages if message.get("role") == "user"]
    relevant = [text for text in user_texts if text.strip()]
    source = relevant[-1] if relevant else "Complete the current task based on the available input."
    title = "Task result"
    goal = re.search(r"Task goal:\s*([^\n]+)", source)
    if goal:
        title = goal.group(1).strip()[:100] or title
    filename = "deliverable.md"
    requested = re.search(r"(?:fileName|file name|named)\s*['\"`]?([A-Za-z0-9_.-]+\.md)", source, re.IGNORECASE)
    if requested:
        filename = requested.group(1)

    if filename == "connection-test.md":
        content = "# Connection capability check\n\nThe local model server produced this harmless approval preview."
    else:
        content = f"# {title}\n\n{source.strip()}\n"
    return filename, content


def response_payload(model: str, message: dict[str, Any], finish_reason: str, prompt_tokens: int, completion_tokens: int) -> dict[str, Any]:
    return {
        "id": f"chatcmpl-test-{int(time.time() * 1000)}",
        "object": "chat.completion",
        "created": int(time.time()),
        "model": model,
        "choices": [{"index": 0, "message": message, "finish_reason": finish_reason}],
        "usage": {
            "prompt_tokens": prompt_tokens,
            "completion_tokens": completion_tokens,
            "total_tokens": prompt_tokens + completion_tokens,
        },
    }


def create_handler(state: ServerState, max_request_bytes: int) -> type[BaseHTTPRequestHandler]:
    class Handler(BaseHTTPRequestHandler):
        server_version = "CareerTestModel/1"

        def log_message(self, format: str, *args: Any) -> None:
            return

        def _send_json(self, status: int, value: Any) -> None:
            body = json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def _read_json(self) -> dict[str, Any]:
            length = int(self.headers.get("Content-Length", "0"))
            if length <= 0 or length > max_request_bytes:
                raise ValueError("invalid request size")
            data = self.rfile.read(length)
            if len(data) != length:
                raise ValueError("incomplete request")
            decoded = json.loads(data)
            if not isinstance(decoded, dict):
                raise ValueError("request must be an object")
            return decoded

        def do_GET(self) -> None:
            if self.path in ("/health", "/healthz"):
                self._send_json(200, {"status": "ok", "testOnly": True})
            elif self.path == "/__metrics":
                self._send_json(200, state.snapshot())
            else:
                self._send_json(404, {"error": {"message": "not found", "type": "invalid_request_error"}})

        def do_POST(self) -> None:
            if self.path == "/__control":
                try:
                    self._send_json(200, state.configure(self._read_json()))
                except (ValueError, json.JSONDecodeError):
                    self._send_json(400, {"error": {"message": "invalid test control", "type": "invalid_request_error"}})
                return
            if self.path != "/v1/chat/completions":
                self._send_json(404, {"error": {"message": "not found", "type": "invalid_request_error"}})
                return

            try:
                payload = self._read_json()
            except (ValueError, json.JSONDecodeError):
                self._send_json(400, {"error": {"message": "invalid request body", "type": "invalid_request_error"}})
                return

            messages = payload.get("messages") or []
            if not isinstance(messages, list):
                self._send_json(400, {"error": {"message": "messages must be an array", "type": "invalid_request_error"}})
                return
            latest_outcome = latest_publication_outcome(messages)
            with state.lock:
                state.requests += 1
                delay_ms = state.delay_ms
                reapprove_after_denial = state.reapprove_after_denial
                post_publication_failure = state.fail_after_publication and latest_outcome == "published"
                should_fail = state.fail_next > 0 or post_publication_failure
                failure_status: int | None = None
                if state.fail_next > 0:
                    state.fail_next -= 1
                    state.failures += 1
                    failure_status = 503
                elif post_publication_failure:
                    state.fail_after_publication = False
                    state.post_publication_failures += 1
                    state.failures += 1
                    failure_status = 500
            if delay_ms:
                time.sleep(delay_ms / 1000)
            if should_fail:
                code = "fixture_post_publication_failure" if failure_status == 500 else "fixture_failure"
                self._send_json(failure_status or 503, {"error": {"message": "controlled fixture failure", "type": "server_error", "code": code}})
                return

            model = str(payload.get("model") or "fixture-model")
            prompt_tokens = max(1, sum(len(extract_text(item)) for item in messages) // 4)

            latest_user_text = "\n".join(
                extract_text(message.get("content")) for message in messages if message.get("role") == "user"
            )
            if "connection works" in latest_user_text.lower() and not payload.get("tools"):
                with state.lock:
                    state.successes += 1
                    state.text_responses += 1
                response = response_payload(model, {"role": "assistant", "content": "The model connection is working."}, "stop", prompt_tokens, 8)
                self._send_json(200, response)
                return

            tool_result_texts = [
                extract_text(message.get("content"))
                for message in messages
                if message.get("role") == "tool"
            ]
            latest_tool_result = tool_result_texts[-1] if tool_result_texts else ""
            function = tool_definition(payload)
            if latest_outcome == "published":
                text = "The document was published and the host returned a persistent receipt."
                if latest_tool_result:
                    text += " " + latest_tool_result[:200]
                with state.lock:
                    state.successes += 1
                    state.text_responses += 1
                self._send_json(200, response_payload(model, {"role": "assistant", "content": text}, "stop", prompt_tokens, 16))
                return

            should_propose = latest_outcome is None and not tool_result_texts or (
                latest_outcome == "rejected" and reapprove_after_denial
            )
            if function is not None and should_propose:
                file_name, content = current_document(messages)
                if latest_outcome == "rejected":
                    content += "\n\n## Revision\n\nThis revision follows the user's rejection feedback.\n"
                call_id = f"call_publish_{state.snapshot()['requests']}"
                arguments = json.dumps({"fileName": file_name, "content": content}, ensure_ascii=False, separators=(",", ":"))
                message = {
                    "role": "assistant",
                    "content": None,
                    "tool_calls": [{
                        "id": call_id,
                        "type": "function",
                        "function": {"name": function["name"], "arguments": arguments},
                    }],
                }
                with state.lock:
                    state.successes += 1
                    state.tool_call_responses += 1
                self._send_json(200, response_payload(model, message, "tool_calls", prompt_tokens, 25))
                return

            if latest_outcome == "rejected":
                text = "The publication request was rejected. I kept the work available for revision."
            else:
                text = "The document was published and the host returned a persistent receipt."
                if latest_tool_result:
                    text += " " + latest_tool_result[:200]
            with state.lock:
                state.successes += 1
                state.text_responses += 1
            self._send_json(200, response_payload(model, {"role": "assistant", "content": text}, "stop", prompt_tokens, 16))

    return Handler


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8765)
    parser.add_argument("--delay-ms", type=int, default=0)
    parser.add_argument("--fail-first", type=int, default=0)
    parser.add_argument("--max-request-bytes", type=int, default=1024 * 1024)
    arguments = parser.parse_args()

    state = ServerState(max(0, arguments.delay_ms), max(0, arguments.fail_first))
    server = ThreadingHTTPServer((arguments.host, arguments.port), create_handler(state, arguments.max_request_bytes))
    print(f"test-only model server listening on http://{arguments.host}:{arguments.port}/v1", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
