using System.Text.Json;
using System.Text.Json.Serialization;

namespace Career.Contracts;

/// <summary>A new task and the information explicitly supplied by its owner.</summary>
public sealed record CreateTaskRequest(string RequestId, string Title, string Goal, string[] Criteria, string InputText, string Mode = "office", string? Project = null);
/// <summary>Revises a draft or paused task; current runs are never mutated in place.</summary>
public sealed record UpdateTaskRequest(string RequestId, long ExpectedVersion, string Title, string Goal, string[] Criteria, string InputText);
/// <summary>Confirms one fixed plan and its input scope.</summary>
public sealed record ConfirmPlanRequest(string RequestId, long ExpectedVersion, int TaskRevision, string ProposalHash);
/// <summary>A versioned task control command.</summary>
public sealed record TaskCommandRequest(string RequestId, long ExpectedVersion);
/// <summary>A decision for one immutable tool payload.</summary>
public sealed record ApprovalDecisionRequest(string RequestId, long ExpectedVersion, string PayloadHash, string Decision, string? Note = null);
/// <summary>Accepts or returns a particular artifact and task revision.</summary>
public sealed record AcceptanceRequest(string RequestId, long ExpectedVersion, int TaskRevision, string ArtifactId, int ArtifactVersion, string ContentHash, string Decision, string? Note = null);
/// <summary>Pairs this browser with the local workspace owner.</summary>
public sealed record PairRequest(string Code);
/// <summary>A short lived bearer credential, held only in browser memory.</summary>
public sealed record SessionResponse(string AccessToken, DateTimeOffset ExpiresAt, string WorkspaceId);
/// <summary>Initial connection information; does not disclose the pairing code.</summary>
public sealed record BootstrapResponse(string Product, string WorkspaceId, string WorkspaceName, bool HasOwner, bool RequiresPairing, string ProtocolVersion);
/// <summary>Non-secret connection details and the status of a scoped capability test.</summary>
public sealed record ConnectionResponse(string Id, string Name, string Protocol, string Endpoint, string Model, bool HasCredential, bool AllowLocalEndpoint, string TestState, string? TestMessage, DateTimeOffset? TestedAt, int MaxOutputTokens, int MaxModelCalls, int MaxInputCharacters);
/// <summary>A connection update. The credential is never returned or placed in events.</summary>
public sealed record SaveConnectionRequest(string RequestId, string Name, string Protocol, string Endpoint, string Model, string? ApiKey, bool AllowLocalEndpoint = false, int MaxOutputTokens = 4096, int MaxModelCalls = 6, int MaxInputCharacters = 32000);
/// <summary>A versioned command for testing the current connection.</summary>
public sealed record TestConnectionRequest(string RequestId);
/// <summary>A fixed task plan and its decision.</summary>
public sealed record PlanResponse(string Id, int Version, string ProposalHash, string Status, string ScopeDescription);
/// <summary>One real execution, including its current attempt and recovery status.</summary>
public sealed record RunResponse(string Id, int TaskRevision, string Status, string? AttemptId, int AttemptNumber, string? Progress, string? FailureCode, string? FailureMessage, int ModelCalls, long InputTokens, long OutputTokens, string UsageState, DateTimeOffset CreatedAt, DateTimeOffset? EndedAt);
/// <summary>An immutable tool proposal and the corresponding decision.</summary>
public sealed record ApprovalResponse(string Id, string RunId, string OperationId, int Version, string ToolName, string PayloadHash, string Status, string Target, string Content, string Reason, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string? DecisionNote);
/// <summary>A published, immutable document version.</summary>
public sealed record ArtifactResponse(string Id, string RunId, int TaskRevision, int Version, string Title, string FileName, string ContentHash, long SizeBytes, string MediaType, string Content, string[] Sources, string[] Checks, string[] Unverified, DateTimeOffset CreatedAt);
/// <summary>An acceptance decision for a fixed artifact.</summary>
public sealed record AcceptanceResponse(string Id, int TaskRevision, string ArtifactId, int ArtifactVersion, string ContentHash, string Decision, string? Note, DateTimeOffset CreatedAt);
/// <summary>A user-facing record of a committed event.</summary>
public sealed record ActivityResponse(string Id, string Type, string Message, DateTimeOffset OccurredAt);
/// <summary>The authoritative task snapshot. Opening it cannot change business state.</summary>
public sealed record TaskResponse(string Id, string Title, string Goal, string[] Criteria, string InputText, string Mode, string? Project, string Owner, string Status, int Revision, long Version, PlanResponse Plan, RunResponse[] Runs, ApprovalResponse[] Approvals, ArtifactResponse[] Artifacts, AcceptanceResponse[] Acceptances, ActivityResponse[] Activity, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, long SnapshotSeq, string[] AvailableActions);
/// <summary>A compact task summary used by the workbench and inbox.</summary>
public sealed record TaskSummaryResponse(string Id, string Title, string Status, int Revision, long Version, string Owner, string? Project, DateTimeOffset UpdatedAt, string? Progress, int PendingApprovals, int PendingArtifacts);
/// <summary>A consistent workbench snapshot and its event watermark.</summary>
public sealed record WorkspaceResponse(string WorkspaceId, string Name, bool IntakePaused, ConnectionResponse? Connection, TaskSummaryResponse[] Tasks, long SnapshotSeq, bool ExecutionAvailable = true, string? ExecutionMessage = null);
/// <summary>A durable workspace event; payloads do not contain secrets.</summary>
public sealed record EventResponse(long Seq, string EventId, int SchemaVersion, DateTimeOffset OccurredAt, string? TaskId, string? RunId, string Type, string Message);
/// <summary>An API problem with a stable code and optional authoritative task snapshot.</summary>
public sealed record ProblemResponse(string Type, string Title, int Status, string Detail, string Code, string? RequestId = null, TaskResponse? Current = null);
/// <summary>A persisted command receipt, used after an uncertain response.</summary>
public sealed record CommandReceiptResponse(string RequestId, string Route, string TaskId, long Version, DateTimeOffset AcceptedAt);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(CreateTaskRequest))]
[JsonSerializable(typeof(ConfirmPlanRequest))]
[JsonSerializable(typeof(UpdateTaskRequest))]
[JsonSerializable(typeof(TaskCommandRequest))]
[JsonSerializable(typeof(ApprovalDecisionRequest))]
[JsonSerializable(typeof(AcceptanceRequest))]
[JsonSerializable(typeof(PairRequest))]
[JsonSerializable(typeof(SessionResponse))]
[JsonSerializable(typeof(BootstrapResponse))]
[JsonSerializable(typeof(ConnectionResponse))]
[JsonSerializable(typeof(SaveConnectionRequest))]
[JsonSerializable(typeof(TestConnectionRequest))]
[JsonSerializable(typeof(WorkspaceResponse))]
[JsonSerializable(typeof(TaskResponse))]
[JsonSerializable(typeof(RunResponse))]
[JsonSerializable(typeof(ApprovalResponse))]
[JsonSerializable(typeof(ArtifactResponse))]
[JsonSerializable(typeof(AcceptanceResponse))]
[JsonSerializable(typeof(TaskResponse[]))]
[JsonSerializable(typeof(EventResponse))]
[JsonSerializable(typeof(EventResponse[]))]
[JsonSerializable(typeof(ProblemResponse))]
[JsonSerializable(typeof(CommandReceiptResponse))]
[JsonSerializable(typeof(WorkerRequest))]
[JsonSerializable(typeof(WorkerEnvelope))]
[JsonSerializable(typeof(WorkerResponse))]
[JsonSerializable(typeof(WorkerToolRequest))]
[JsonSerializable(typeof(WorkerToolResponse))]
[JsonSerializable(typeof(ModelCallRequest))]
[JsonSerializable(typeof(ModelCallResponse))]
public partial class CareerJson : JsonSerializerContext;
