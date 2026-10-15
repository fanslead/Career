using Career.Contracts;
using Career.Core;

namespace Career.Core.Tests;

internal sealed class TestWorkspace : IDisposable
{
    public string DirectoryPath { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), $"career-test-{Guid.NewGuid():N}");

    public TestWorkspace() => Directory.CreateDirectory(DirectoryPath);

    public void Dispose()
    {
        if (Directory.Exists(DirectoryPath))
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}

internal static class TestDocuments
{
    private static readonly DateTimeOffset FixedTime = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    public static TaskDocument Task(string taskId, string status = "Queued")
    {
        var task = new TaskResponse(
            taskId,
            "Review repository",
            "Produce a verified change",
            ["Include tests"],
            "Repository input",
            "office",
            null,
            "workspace owner",
            status,
            1,
            0,
            new PlanResponse("plan_1", 1, "plan-hash", "Fixed", "Bounded repository work"),
            [],
            [],
            [],
            [],
            [],
            FixedTime,
            FixedTime,
            0,
            []);
        return new TaskDocument(task, [], [], []);
    }

    public static RunResponse Run(string runId, string status) => new(
        runId,
        1,
        status,
        "attempt_1",
        1,
        null,
        null,
        null,
        0,
        0,
        0,
        "none",
        FixedTime,
        null);

    public static AcceptanceResponse Acceptance(
        string acceptanceId,
        string artifactId,
        int version,
        string contentHash) => new(
        acceptanceId,
        1,
        artifactId,
        version,
        contentHash,
        "accept",
        null,
        FixedTime);

    public static OperationDocument Operation(string operationId, string fileName, string content) => new(
        operationId,
        "run_1",
        "publish-final",
        "payload-hash",
        fileName,
        content,
        "pending",
        null,
        null);
}
