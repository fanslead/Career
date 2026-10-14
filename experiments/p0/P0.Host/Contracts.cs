using System.Text.Json;
using System.Text.Json.Serialization;

namespace P0.Host;

public sealed record CreateRun(string Prompt, string Scenario = "normal");
public sealed record Decision(bool Approved, string ExpectedHash);
public sealed record NoteArgs(string Text);
public sealed record Receipt(string ReceiptId, string Text);
public sealed record ApiError(string Error);
public sealed record Health(string Status, bool NativeAot, string SqliteVersion, bool Fts5, int ProcessId, bool WorkerConfigured);
public sealed record RunView(string Id, string Status, string Prompt, string? OperationId,
    string? ArgumentsHash, int? WorkerPid, int ModelCalls, int ToolCalls, string Text,
    string? Error, int ReceiptCount, string? ArgumentsJson);
public sealed record EventView(long Seq, string RunId, string Type, JsonElement Payload, string CreatedUtc);
public sealed record EventPayload(string Text);
public sealed record WorkerInput(string RunId, string Prompt, JsonElement? Session, JsonElement? Approval,
    bool? Approved, string? OperationId, string Scenario);
public sealed record RpcRequest(string Jsonrpc, string Id, string Method, WorkerInput Params);
public sealed record RpcReceipt(string Jsonrpc, string Id, Receipt Result);
public sealed record RpcErrorDetail(int Code, string Message);
public sealed record RpcFailure(string Jsonrpc, string Id, RpcErrorDetail Error);
public sealed record WorkerResult(string Status, JsonElement Session, JsonElement? Approval,
    string? CallId, string? ToolName, JsonElement? Arguments, string? Text, int ModelCalls, int ToolCalls, int WorkerPid);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CreateRun))]
[JsonSerializable(typeof(Decision))]
[JsonSerializable(typeof(NoteArgs))]
[JsonSerializable(typeof(Receipt))]
[JsonSerializable(typeof(ApiError))]
[JsonSerializable(typeof(Health))]
[JsonSerializable(typeof(RunView))]
[JsonSerializable(typeof(List<RunView>))]
[JsonSerializable(typeof(EventView))]
[JsonSerializable(typeof(EventPayload))]
[JsonSerializable(typeof(RpcRequest))]
[JsonSerializable(typeof(RpcReceipt))]
[JsonSerializable(typeof(RpcFailure))]
[JsonSerializable(typeof(WorkerResult))]
public partial class ProbeJson : JsonSerializerContext;
