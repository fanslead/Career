using System.Text.Json;

namespace Career.Contracts;

public static class Protocol { public const string Version = "career-worker/1"; public const int MaxMessageBytes = 1024 * 1024; }
/// <summary>The fixed input for one isolated worker attempt. Secret fields never enter snapshots.</summary>
public sealed record WorkerRequest(string ProtocolVersion, string RunId, string AttemptId, string HostEpoch, long FencingToken, string OperationId, string Goal, string[] Criteria, string InputText, string Protocol, string Endpoint, string Model, string ApiKey, int MaxOutputTokens, int MaxModelCalls, int MaxInputCharacters, JsonElement? Session = null, JsonElement? Approval = null, bool? Approved = null, string? PublishedArtifactId = null, bool TestOnly = false, bool AllowLocalEndpoint = false);
/// <summary>A worker line carrying exactly one typed notification, request or result.</summary>
public sealed record WorkerEnvelope(string Jsonrpc, string? Id, string? Method, WorkerResponse? Result, JsonElement? Params, WorkerToolResponse? ToolResult, string? Error, ModelCallResponse? ModelResult = null);
/// <summary>The resumable MAF response, not a declaration that the user task is done.</summary>
public sealed record WorkerResponse(string ProtocolVersion, string RunId, string AttemptId, string HostEpoch, long FencingToken, string Status, JsonElement? Session, JsonElement? Approval, string? CallId, string? ToolName, string? FileName, string? Content, string Text, int ModelCalls, long InputTokens, long OutputTokens, string UsageState, string? ErrorCode = null, string? ErrorMessage = null);
/// <summary>A brokered publish request. The host verifies the fixed operation and approved payload.</summary>
public sealed record WorkerToolRequest(string ProtocolVersion, string RunId, string AttemptId, string HostEpoch, long FencingToken, string OperationId, string FileName, string Content);
/// <summary>A host receipt for a completed publication.</summary>
public sealed record WorkerToolResponse(string OperationId, string ArtifactId, string ContentHash, string Status, string Message);

/// <summary>A budget reservation or usage settlement for one provider request.</summary>
public sealed record ModelCallRequest(string ProtocolVersion, string RunId, string AttemptId, string HostEpoch, long FencingToken, string CallId, int MaxOutputTokens, long? InputTokens = null, long? OutputTokens = null, string UsageState = "reserved");
/// <summary>The host authorizes one bounded model request or acknowledges its usage.</summary>
public sealed record ModelCallResponse(string CallId, bool Allowed, int RemainingCalls, string Message);
