using Career.Core;
using Career.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Career.Core.Tests;

[TestClass]
public sealed class CareerStoreTests
{
    [TestMethod]
    public void CommandReceiptReplaysOnceAndRejectsPayloadReuse()
    {
        using var workspace = new TestWorkspace();
        using var store = new CareerStore(workspace.DirectoryPath, workspaceId: "workspace_test", verifyProcessLock: false);
        const string requestId = "1c056858-5ba8-40ed-a665-0fa8ddc32b9d";
        var transitionCalls = 0;

        var first = store.Command(
            "POST /tasks",
            requestId,
            "payload-1",
            "task_1",
            _ =>
            {
                transitionCalls++;
                return TestDocuments.Task("task_1");
            },
            "task.created",
            "Task created");
        long firstSequence = store.CurrentSequence;

        var replay = store.Command(
            "POST /tasks",
            requestId,
            "payload-1",
            "task_1",
            _ => throw new AssertFailedException("A replay must not execute the transition."),
            "task.created",
            "Task created again");

        Assert.AreEqual(1, transitionCalls);
        Assert.AreEqual(first.Id, replay.Id);
        Assert.AreEqual(first.Version, replay.Version);
        Assert.AreEqual(firstSequence, store.CurrentSequence);
        Assert.HasCount(1, store.ReadEvents(0));
        Assert.AreEqual(first.Id, store.ReadTask("task_1")!.Task.Id);

        DomainException conflict = Assert.ThrowsExactly<DomainException>(() => store.Command(
            "POST /tasks",
            requestId,
            "different-payload",
            "task_1",
            _ => throw new AssertFailedException("A conflicting request must not execute the transition."),
            "task.created",
            "Changed payload"));

        Assert.AreEqual("IDEMPOTENCY_CONFLICT", conflict.Code);
        Assert.AreEqual(firstSequence, store.CurrentSequence);

        DomainException routeConflict = Assert.ThrowsExactly<DomainException>(() => store.Command(
            "POST /tasks/task_1/confirm-plan",
            requestId,
            "payload-1",
            "task_1",
            _ => throw new AssertFailedException("A cross-route request-ID conflict must not execute the transition."),
            "plan.confirmed",
            "Reused on another route"));

        Assert.AreEqual("IDEMPOTENCY_CONFLICT", routeConflict.Code);
        Assert.AreEqual(1, transitionCalls);
        Assert.AreEqual(firstSequence, store.CurrentSequence);
        Assert.AreEqual(first.Id, store.ReadTask("task_1")!.Task.Id);
    }

    [TestMethod]
    public void UniqueActiveRunConflictRollsBackEventAndTask()
    {
        using var workspace = new TestWorkspace();
        using var store = new CareerStore(workspace.DirectoryPath, workspaceId: "workspace_test", verifyProcessLock: false);
        CreateTask(store, "task_runs");

        store.Update(
            "task_runs",
            document => document with
            {
                Task = document.Task with { Runs = [TestDocuments.Run("run_1", "Running")] },
            },
            "run.started",
            "First run started");

        TaskDocument beforeConflict = store.ReadTask("task_runs")!;
        long sequenceBeforeConflict = store.CurrentSequence;

        SqliteException failure = Assert.ThrowsExactly<SqliteException>(() => store.Update(
            "task_runs",
            document => document with
            {
                Task = document.Task with
                {
                    Runs = [.. document.Task.Runs, TestDocuments.Run("run_2", "Running")],
                },
            },
            "run.started",
            "Second run started"));

        Assert.AreEqual(19, failure.SqliteErrorCode);
        Assert.AreEqual(sequenceBeforeConflict, store.CurrentSequence);
        Assert.HasCount(2, store.ReadEvents(0));
        TaskDocument afterConflict = store.ReadTask("task_runs")!;
        Assert.AreEqual(beforeConflict.Task.Version, afterConflict.Task.Version);
        Assert.HasCount(1, afterConflict.Task.Runs.Where(run => run.Status == "Running"));
        Assert.AreEqual("run_1", afterConflict.Task.Runs.Single().Id);
    }

    [TestMethod]
    public void IncompatibleMigrationRollsBackCreatedTables()
    {
        using var workspace = new TestWorkspace();
        string stateDirectory = Path.Combine(workspace.DirectoryPath, "state");
        Directory.CreateDirectory(stateDirectory);
        string databasePath = Path.Combine(stateDirectory, "app.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();

        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY, checksum TEXT NOT NULL, applied_at TEXT NOT NULL); INSERT INTO schema_migrations VALUES(1, 'wrong-checksum', '2026-01-01T00:00:00Z');";
            command.ExecuteNonQuery();
        }

        InvalidOperationException failure = Assert.ThrowsExactly<InvalidOperationException>(
            () => _ = new CareerStore(workspace.DirectoryPath, verifyProcessLock: false));

        StringAssert.Contains(failure.Message, "版本不兼容");
        using var verification = new SqliteConnection(connectionString);
        verification.Open();
        using var query = verification.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'settings';";
        Assert.AreEqual(0L, query.ExecuteScalar());
    }

    [TestMethod]
    public void AcceptanceInsertRequiresPublishedArtifactForeignKey()
    {
        using var workspace = new TestWorkspace();
        using var store = new CareerStore(workspace.DirectoryPath, workspaceId: "workspace_test", verifyProcessLock: false);
        CreateTask(store, "task_acceptance");
        long sequenceBeforeFailure = store.CurrentSequence;

        SqliteException failure = Assert.ThrowsExactly<SqliteException>(() => store.Update(
            "task_acceptance",
            document => document with
            {
                Task = document.Task with
                {
                    Acceptances = [TestDocuments.Acceptance("acceptance_1", "artifact_missing", 1, "missing-hash")],
                },
            },
            "artifact.accepted",
            "Unpublished artifact accepted"));

        Assert.AreEqual(19, failure.SqliteErrorCode);
        Assert.AreEqual(sequenceBeforeFailure, store.CurrentSequence);
        Assert.HasCount(1, store.ReadEvents(0));
        TaskDocument unchanged = store.ReadTask("task_acceptance")!;
        Assert.AreEqual(1, unchanged.Task.Version);
        Assert.IsEmpty(unchanged.Task.Acceptances);
    }

    private static void CreateTask(CareerStore store, string taskId)
    {
        store.Command(
            "POST /tasks",
            Guid.NewGuid().ToString(),
            "payload-" + taskId,
            taskId,
            _ => TestDocuments.Task(taskId),
            "task.created",
            "Task created");
    }
}
