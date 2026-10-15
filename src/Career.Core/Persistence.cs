using Career.Contracts;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Career.Core;

public sealed record WorkspaceMetadata(int SchemaVersion, string Id, string Name, DateTimeOffset CreatedAt);
public sealed record RunCheckpoint(string RunId, JsonElement? Session, JsonElement? Approval, bool? Approved, string OperationId, string? PublishedArtifactId, string AdapterVersion, string ConnectionHash, string? ControlIntent = null);
public sealed record OperationDocument(string Id, string RunId, string LogicalKey, string PayloadHash, string FileName, string Content, string State, string? ArtifactId, string? ContentHash);
public sealed record UsageEntry(string CallId, string RunId, string AttemptId, string State, long InputTokens, long OutputTokens, int ReservedOutputTokens, DateTimeOffset ReservedAt);
public sealed record TaskRevisionDocument(int Revision, string Title, string Goal, string[] Criteria, string InputText, string ProposalHash, DateTimeOffset CreatedAt);
public sealed record TaskDocument(TaskResponse Task, RunCheckpoint[] Checkpoints, OperationDocument[] Operations, UsageEntry[] Usage, TaskRevisionDocument[]? Revisions = null);
public sealed record StoredCommand(string RequestId, string Route, string PayloadHash, string TaskId, long Version, string ResponseJson, DateTimeOffset AcceptedAt);
public sealed record SessionDocument(string Id, string AccessHash, DateTimeOffset AccessExpires, string RefreshHash, DateTimeOffset RefreshExpires, bool Revoked, DateTimeOffset CreatedAt);

public interface ICareerStore
{
    string WorkspaceId { get; }
    string WorkspaceName { get; }
    string HostEpoch { get; }
    long CurrentSequence { get; }
    TaskDocument? ReadTask(string taskId);
    TaskDocument[] ReadTasks();
    TaskResponse Command(string route, string requestId, string payloadHash, string taskId, Func<TaskDocument?, TaskDocument> transition, string eventType, string message);
    TaskDocument Update(string taskId, Func<TaskDocument, TaskDocument> transition, string eventType, string message);
    EventResponse[] ReadEvents(long after, int limit = 200);
    StoredCommand? ReadCommand(string requestId);
    string? ReadSetting(string key);
    void WriteSetting(string key, string value);
    void SaveCommand(StoredCommand command);
    void PublishEvent(string type, string message);
    void SettingCommand(string route, string requestId, string payloadHash, string key, string value);
    SessionDocument? FindSession(string credentialHash, bool refresh);
    void SaveSession(SessionDocument session);
    bool RotateSession(string oldRefreshHash, SessionDocument replacement);
    void RevokeSession(string sessionId);
}

public interface IArtifactPublisher
{
    ArtifactResponse Publish(string taskId, string runId, int revision, int version, OperationDocument operation, string[] sources, string[] checks, string[] unverified);
    byte[] Read(ArtifactResponse artifact, string taskId);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(TaskDocument))]
[JsonSerializable(typeof(WorkspaceMetadata))]
[JsonSerializable(typeof(StoredCommand))]
[JsonSerializable(typeof(SessionDocument))]
[JsonSerializable(typeof(ConnectionDocument))]
[JsonSerializable(typeof(OperationDocument))]
[JsonSerializable(typeof(UsageEntry))]
[JsonSerializable(typeof(TaskRevisionDocument))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public partial class StorageJson : JsonSerializerContext;
