# P0 MAF Worker

This is a .NET 10 console worker for the isolated P0 MAF probe. It pins `Microsoft.Agents.AI` 1.24.0, the stable version verified in the [NuGet Gallery](https://www.nuget.org/packages/Microsoft.Agents.AI/1.24.0) on 2026-10-14. `packages.lock.json` pins the complete restore graph.

The worker uses the real `ChatClientAgent`, `FunctionInvokingChatClient`, `AIFunction`, and `ApprovalRequiredAIFunction` pipeline. Its local `IChatClient` is scripted, so the probe needs no provider key or network model. It requests `add(2, 3)`, then `record_note(text: prompt)`, and finally returns text containing the host receipt. Function schemas are runtime JSON; the dispatcher itself remains fixed C# code.

`record_note` has no local write path. On invocation, it sends one `tool.execute` request to the host and waits for the matching response. A host error or malformed response makes the worker exit nonzero without returning a successful `agent.run` result.

## Build and smoke probe

From this directory or the repository root:

```sh
dotnet restore --locked-mode experiments/p0/P0.Worker/P0.Worker.csproj
dotnet build --no-restore experiments/p0/P0.Worker/P0.Worker.csproj
python3 experiments/p0/P0.Worker/smoke.py
```

## Stdio contract

The process reads one JSON-RPC request line:

```json
{"jsonrpc":"2.0","id":"run","method":"agent.run","params":{"runId":"r1","operationId":"op1","prompt":"...","session":null,"approval":null,"approved":null}}
```

It emits the final `id: "run"` result line on stdout. While invoking `record_note`, it emits a separate `id: "tool-1"`, `method: "tool.execute"` line and waits for the host to answer with `result.receiptId` and `result.text`. Diagnostics go to stderr. The response includes the serialized MAF session and, when waiting, the native `ToolApprovalRequestContent` JSON.

Persist both `session` and `approval` from `WaitingApproval`. To resume after process restart, send them back with `approved: true` or `false`; the worker deserializes the session through `AIAgent.DeserializeSessionAsync` and recreates the native approval response from the approval request. `modelCalls` and `toolCalls` are counts for that process invocation, and `workerPid` identifies the process so a restart can be observed.

Optional `scenario` values are `normal`, `invalid-arguments`, and `endless`. The endless case configures MAF's own function invocation cap to six; if MAF returns a still-unresolved function call at that cap, the worker fails closed rather than marking the run successful.
