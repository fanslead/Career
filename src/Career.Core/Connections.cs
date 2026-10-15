using Career.Contracts;

namespace Career.Core;

public sealed record ConnectionDocument(ConnectionResponse Connection, string SecretRef, string ConfigurationHash, string? LastRequestId = null, string? LastPayloadHash = null);
public interface IConnectionService
{
    ConnectionResponse? Read();
    string? CurrentHash { get; }
    ValueTask<ConnectionResponse> SaveAsync(SaveConnectionRequest request, CancellationToken cancellationToken);
    ValueTask<WorkerRequest> CreateWorkerRequestAsync(TaskResponse task, RunResponse run, RunCheckpoint checkpoint, string hostEpoch, long fencingToken, bool testOnly, CancellationToken cancellationToken);
    void RecordTest(string configurationHash, bool succeeded, string message);
}
