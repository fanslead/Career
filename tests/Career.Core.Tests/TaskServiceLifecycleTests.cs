using System.Text;
using System.Text.Json;
using Career.Contracts;
using Career.Core;
using Career.Infrastructure;

namespace Career.Core.Tests;

[TestClass]
public sealed class TaskServiceLifecycleTests
{
    private const string FirstFileName = "verified-result.md";
    private const string FirstContent = "# Verified result\nThe requested work is complete.\n";

    [TestMethod]
    public void PublishCompleteAndAccept_RequiresSeparateAcceptanceAfterRunSucceeds()
    {
        using var fixture = new LifecycleFixture();
        TaskResponse task = fixture.CreateConfirmedTask();
        RunResponse firstAttempt = fixture.StartNext(task.Id);
        ApprovalResponse approval = fixture.Propose(task.Id, firstAttempt, FirstFileName, FirstContent);

        fixture.Approve(task.Id, approval);
        RunResponse publishingAttempt = fixture.StartNext(task.Id);
        WorkerToolResponse receipt = fixture.Publish(task.Id, approval, publishingAttempt, FirstFileName, FirstContent);

        TaskResponse beforeCompletion = fixture.Read(task.Id);
        Assert.AreEqual("Running", beforeCompletion.Status);
        Assert.AreEqual("Running", beforeCompletion.Runs.Last().Status);
        Assert.AreEqual("Succeeded", receipt.Status);
        Assert.HasCount(1, beforeCompletion.Artifacts);
        Assert.IsEmpty(beforeCompletion.Acceptances);

        DomainException earlyAcceptance = Assert.ThrowsExactly<DomainException>(() => fixture.Service.Accept(
            task.Id,
            fixture.Acceptance(beforeCompletion, beforeCompletion.Artifacts.Single(), "accept", "Reviewed before run completion.")));

        Assert.AreEqual("ARTIFACT_VERSION_CHANGED", earlyAcceptance.Code);
        TaskResponse afterEarlyAcceptance = fixture.Read(task.Id);
        Assert.AreEqual("Running", afterEarlyAcceptance.Status);
        Assert.AreEqual("Running", afterEarlyAcceptance.Runs.Last().Status);
        Assert.IsEmpty(afterEarlyAcceptance.Acceptances);
        Assert.HasCount(1, fixture.MarkdownFiles());

        fixture.Complete(task.Id, publishingAttempt);
        TaskResponse inReview = fixture.Read(task.Id);
        Assert.AreEqual("InReview", inReview.Status);
        Assert.AreEqual("Succeeded", inReview.Runs.Last().Status);
        Assert.AreNotEqual("Done", inReview.Status);
        Assert.AreEqual(FirstContent, Encoding.UTF8.GetString(fixture.Publisher.Read(inReview.Artifacts.Single(), task.Id)));

        TaskResponse done = fixture.Service.Accept(
            task.Id,
            fixture.Acceptance(inReview, inReview.Artifacts.Single(), "accept", "Reviewed against the acceptance criteria."));

        Assert.AreEqual("Done", done.Status);
        Assert.AreEqual("Succeeded", done.Runs.Last().Status);
        Assert.HasCount(1, done.Acceptances);
        Assert.AreEqual("accept", done.Acceptances.Single().Decision);
        Assert.HasCount(1, fixture.MarkdownFiles());
    }

    [TestMethod]
    public void DecideApproval_RejectsOldTaskVersionAndPayloadHashWithoutPublishing()
    {
        using var fixture = new LifecycleFixture();
        TaskResponse task = fixture.CreateConfirmedTask();
        RunResponse run = fixture.StartNext(task.Id);
        ApprovalResponse approval = fixture.Propose(task.Id, run, FirstFileName, FirstContent);
        TaskResponse waiting = fixture.Read(task.Id);
        long sequenceBefore = fixture.Store.CurrentSequence;

        DomainException oldVersion = Assert.ThrowsExactly<DomainException>(() => fixture.Service.DecideApproval(
            approval.Id,
            new ApprovalDecisionRequest(
                LifecycleFixture.NewRequestId(),
                waiting.Version - 1,
                approval.PayloadHash,
                "approve")));
        DomainException oldHash = Assert.ThrowsExactly<DomainException>(() => fixture.Service.DecideApproval(
            approval.Id,
            new ApprovalDecisionRequest(
                LifecycleFixture.NewRequestId(),
                waiting.Version,
                new string('0', 64),
                "approve")));

        Assert.AreEqual("VERSION_CONFLICT", oldVersion.Code);
        Assert.AreEqual("APPROVAL_EXPIRED_OR_CHANGED", oldHash.Code);
        Assert.AreEqual(sequenceBefore, fixture.Store.CurrentSequence);
        TaskResponse unchanged = fixture.Read(task.Id);
        Assert.AreEqual("WaitingApproval", unchanged.Status);
        Assert.AreEqual("Pending", unchanged.Approvals.Single().Status);
        Assert.IsEmpty(unchanged.Artifacts);
        Assert.IsEmpty(fixture.MarkdownFiles());
    }

    [TestMethod]
    public void Publish_RejectsUnapprovedOperationWithoutCreatingFile()
    {
        using var fixture = new LifecycleFixture();
        TaskResponse task = fixture.CreateConfirmedTask();
        RunResponse firstAttempt = fixture.StartNext(task.Id);
        ApprovalResponse approval = fixture.Propose(task.Id, firstAttempt, FirstFileName, FirstContent);
        fixture.Decide(task.Id, approval, "reject");
        RunResponse retryAttempt = fixture.StartNext(task.Id);
        long sequenceBefore = fixture.Store.CurrentSequence;

        DomainException failure = Assert.ThrowsExactly<DomainException>(() => fixture.Publish(
            task.Id,
            approval,
            retryAttempt,
            FirstFileName,
            FirstContent));

        Assert.AreEqual("OPERATION_NOT_AUTHORIZED", failure.Code);
        Assert.AreEqual(sequenceBefore, fixture.Store.CurrentSequence);
        Assert.AreEqual("Rejected", fixture.Store.ReadTask(task.Id)!.Operations.Single().State);
        Assert.AreEqual("Running", fixture.Read(task.Id).Status);
        Assert.IsEmpty(fixture.MarkdownFiles());
    }

    [TestMethod]
    public void Publish_RejectsChangedContentAndReplayReturnsOriginalArtifactWithoutDuplicateFile()
    {
        using var fixture = new LifecycleFixture();
        TaskResponse task = fixture.CreateConfirmedTask();
        RunResponse firstAttempt = fixture.StartNext(task.Id);
        ApprovalResponse approval = fixture.Propose(task.Id, firstAttempt, FirstFileName, FirstContent);
        fixture.Approve(task.Id, approval);
        RunResponse publishingAttempt = fixture.StartNext(task.Id);

        DomainException changedPayload = Assert.ThrowsExactly<DomainException>(() => fixture.Publish(
            task.Id,
            approval,
            publishingAttempt,
            FirstFileName,
            FirstContent + "changed after approval\n"));

        Assert.AreEqual("OPERATION_PAYLOAD_MISMATCH", changedPayload.Code);
        Assert.IsEmpty(fixture.MarkdownFiles());
        Assert.AreEqual("Approved", fixture.Store.ReadTask(task.Id)!.Operations.Single().State);

        WorkerToolResponse firstReceipt = fixture.Publish(task.Id, approval, publishingAttempt, FirstFileName, FirstContent);
        WorkerToolResponse replayReceipt = fixture.Publish(task.Id, approval, publishingAttempt, FirstFileName, FirstContent);

        Assert.AreEqual("Succeeded", firstReceipt.Status);
        Assert.AreEqual(firstReceipt.OperationId, replayReceipt.OperationId);
        Assert.AreEqual(firstReceipt.ArtifactId, replayReceipt.ArtifactId);
        Assert.AreEqual(firstReceipt.ContentHash, replayReceipt.ContentHash);
        Assert.HasCount(1, fixture.MarkdownFiles());
        Assert.AreEqual(FirstContent, File.ReadAllText(fixture.MarkdownFiles().Single(), Encoding.UTF8));
        Assert.HasCount(1, fixture.Read(task.Id).Artifacts);
    }

    [TestMethod]
    public void AwaitApproval_RejectsSucceededOperationWithoutResettingArtifactOrOperation()
    {
        using var fixture = new LifecycleFixture();
        TaskResponse task = fixture.CreateConfirmedTask();
        RunResponse firstAttempt = fixture.StartNext(task.Id);
        ApprovalResponse approval = fixture.Propose(task.Id, firstAttempt, FirstFileName, FirstContent);
        fixture.Approve(task.Id, approval);
        RunResponse publishingAttempt = fixture.StartNext(task.Id);
        WorkerToolResponse receipt = fixture.Publish(task.Id, approval, publishingAttempt, FirstFileName, FirstContent);
        TaskDocument beforeReplay = fixture.Store.ReadTask(task.Id)!;
        long sequenceBeforeReplay = fixture.Store.CurrentSequence;

        DomainException failure = Assert.ThrowsExactly<DomainException>(() => fixture.Propose(
            task.Id,
            publishingAttempt,
            FirstFileName,
            FirstContent));

        Assert.AreEqual("OPERATION_ALREADY_COMPLETED", failure.Code);
        Assert.AreEqual(sequenceBeforeReplay, fixture.Store.CurrentSequence);
        TaskDocument afterReplay = fixture.Store.ReadTask(task.Id)!;
        Assert.AreEqual("Succeeded", afterReplay.Operations.Single().State);
        Assert.AreEqual(receipt.OperationId, afterReplay.Operations.Single().Id);
        Assert.AreEqual(receipt.ArtifactId, afterReplay.Operations.Single().ArtifactId);
        Assert.AreEqual(receipt.ContentHash, afterReplay.Operations.Single().ContentHash);
        Assert.AreEqual(beforeReplay.Task.Artifacts.Single().Id, afterReplay.Task.Artifacts.Single().Id);
        Assert.HasCount(1, afterReplay.Task.Artifacts);
        Assert.HasCount(1, afterReplay.Task.Approvals);
        Assert.HasCount(1, fixture.MarkdownFiles());
        Assert.AreEqual(FirstContent, File.ReadAllText(fixture.MarkdownFiles().Single(), Encoding.UTF8));
    }

    [TestMethod]
    public void Return_CreatesNewRevisionAndRunPreservesPriorArtifactAndRejectsItsHashAndVersion()
    {
        using var fixture = new LifecycleFixture();
        TaskResponse task = fixture.CreateConfirmedTask();
        RunResponse firstAttempt = fixture.StartNext(task.Id);
        ApprovalResponse firstApproval = fixture.Propose(task.Id, firstAttempt, FirstFileName, FirstContent);
        fixture.Approve(task.Id, firstApproval);
        RunResponse firstPublishingAttempt = fixture.StartNext(task.Id);
        fixture.Publish(task.Id, firstApproval, firstPublishingAttempt, FirstFileName, FirstContent);
        fixture.Complete(task.Id, firstPublishingAttempt);

        TaskResponse firstReview = fixture.Read(task.Id);
        ArtifactResponse firstArtifact = firstReview.Artifacts.Single();
        TaskResponse revised = fixture.Service.Accept(
            task.Id,
            fixture.Acceptance(firstReview, firstArtifact, "return", "Add the missing source detail."));

        Assert.AreEqual("Queued", revised.Status);
        Assert.AreEqual(2, revised.Revision);
        Assert.AreEqual("Confirmed", revised.Plan.Status);
        Assert.HasCount(2, revised.Runs);
        Assert.AreEqual(2, revised.Runs.Last().TaskRevision);
        Assert.AreNotEqual(revised.Runs[0].Id, revised.Runs[1].Id);
        Assert.AreEqual(firstArtifact.Id, revised.Artifacts.Single().Id);
        Assert.AreEqual("return", revised.Acceptances.Single().Decision);
        Assert.AreEqual(FirstContent, File.ReadAllText(fixture.MarkdownFiles().Single(), Encoding.UTF8));

        RunResponse secondAttempt = fixture.StartNext(task.Id);
        const string secondFileName = "revised-result.md";
        const string secondContent = "# Revised result\nThe requested source detail is included.\n";
        ApprovalResponse secondApproval = fixture.Propose(task.Id, secondAttempt, secondFileName, secondContent);
        fixture.Approve(task.Id, secondApproval);
        RunResponse secondPublishingAttempt = fixture.StartNext(task.Id);
        fixture.Publish(task.Id, secondApproval, secondPublishingAttempt, secondFileName, secondContent);
        fixture.Complete(task.Id, secondPublishingAttempt);

        TaskResponse secondReview = fixture.Read(task.Id);
        ArtifactResponse secondArtifact = secondReview.Artifacts.Single(artifact => artifact.TaskRevision == 2);
        Assert.AreEqual(2, secondArtifact.Version);

        DomainException staleHash = Assert.ThrowsExactly<DomainException>(() => fixture.Service.Accept(
            task.Id,
            fixture.Acceptance(secondReview, secondArtifact, "accept", "Reviewed.") with
            {
                ContentHash = firstArtifact.ContentHash,
            }));
        DomainException staleVersion = Assert.ThrowsExactly<DomainException>(() => fixture.Service.Accept(
            task.Id,
            fixture.Acceptance(secondReview, secondArtifact, "accept", "Reviewed.") with
            {
                ArtifactVersion = firstArtifact.Version,
            }));

        Assert.AreEqual("ARTIFACT_VERSION_CHANGED", staleHash.Code);
        Assert.AreEqual("ARTIFACT_VERSION_CHANGED", staleVersion.Code);
        TaskResponse stillInReview = fixture.Read(task.Id);
        Assert.AreEqual("InReview", stillInReview.Status);
        Assert.HasCount(1, stillInReview.Acceptances);
        Assert.HasCount(2, stillInReview.Artifacts);
        Assert.HasCount(2, fixture.MarkdownFiles());

        TaskResponse done = fixture.Service.Accept(
            task.Id,
            fixture.Acceptance(stillInReview, secondArtifact, "accept", "The revised version is complete."));

        Assert.AreEqual("Done", done.Status);
        Assert.HasCount(2, done.Acceptances);
        Assert.AreEqual("return", done.Acceptances[0].Decision);
        Assert.AreEqual("accept", done.Acceptances[1].Decision);
        Assert.AreEqual(FirstContent, File.ReadAllText(fixture.MarkdownFiles().Single(path => path.EndsWith(FirstFileName, StringComparison.Ordinal)), Encoding.UTF8));
        Assert.AreEqual(secondContent, File.ReadAllText(fixture.MarkdownFiles().Single(path => path.EndsWith(secondFileName, StringComparison.Ordinal)), Encoding.UTF8));
    }

    [TestMethod]
    public void Pause_WaitsForStopAcknowledgementAndRevisedUnconfirmedPlanCannotResume()
    {
        using var fixture = new LifecycleFixture();
        TaskResponse task = fixture.CreateConfirmedTask();
        RunResponse run = fixture.StartNext(task.Id);
        TaskResponse running = fixture.Read(task.Id);

        TaskResponse stopping = fixture.Service.Control(
            task.Id,
            "pause",
            new TaskCommandRequest(LifecycleFixture.NewRequestId(), running.Version));

        Assert.AreEqual("Stopping", stopping.Status);
        Assert.AreEqual("Stopping", stopping.Runs.Last().Status);
        Assert.AreEqual("pause", fixture.Store.ReadTask(task.Id)!.Checkpoints.Last().ControlIntent);

        fixture.Service.StopAcknowledged(task.Id, cancelled: false);
        TaskResponse paused = fixture.Read(task.Id);
        Assert.AreEqual("Paused", paused.Status);
        Assert.AreEqual("Paused", paused.Runs.Last().Status);
        Assert.AreEqual(run.Id, paused.Runs.Last().Id);

        TaskResponse revised = fixture.Service.Revise(
            task.Id,
            new UpdateTaskRequest(
                LifecycleFixture.NewRequestId(),
                paused.Version,
                paused.Title,
                paused.Goal + " with a clarified scope",
                paused.Criteria,
                paused.InputText));

        Assert.AreEqual("AwaitingPlan", revised.Status);
        Assert.AreEqual("Pending", revised.Plan.Status);
        DomainException failure = Assert.ThrowsExactly<DomainException>(() => fixture.Service.Control(
            task.Id,
            "resume",
            new TaskCommandRequest(LifecycleFixture.NewRequestId(), revised.Version)));

        Assert.AreEqual("INVALID_RESUME_STATE", failure.Code);
        Assert.AreEqual("AwaitingPlan", fixture.Read(task.Id).Status);
        Assert.IsEmpty(fixture.MarkdownFiles());
    }

    [TestMethod]
    public void StartNext_SkipsQueuedRunWithStaleConnectionAndAllowsResumeWithNewRun()
    {
        using var fixture = new LifecycleFixture();
        TaskResponse staleTask = fixture.CreateConfirmedTask();
        string staleRunId = staleTask.Runs.Last().Id;
        fixture.Store.Update(
            staleTask.Id,
            document => document with
            {
                Checkpoints = document.Checkpoints.Select(checkpoint => checkpoint.RunId == staleRunId
                    ? checkpoint with { ConnectionHash = new string('b', 64) }
                    : checkpoint).ToArray(),
            },
            "test.connection.changed",
            "Simulate a queued task confirmed against an older connection.");
        TaskResponse currentTask = fixture.CreateConfirmedTask();

        TaskDocument? blockedClaim = fixture.Service.StartNext();

        Assert.IsNull(blockedClaim);
        TaskResponse pausedStaleTask = fixture.Read(staleTask.Id);
        Assert.AreEqual("Paused", pausedStaleTask.Status);
        Assert.AreEqual("Paused", pausedStaleTask.Runs.Last().Status);
        StringAssert.Contains(pausedStaleTask.Runs.Last().Progress, "连接");
        Assert.AreEqual("Queued", fixture.Read(currentTask.Id).Status);

        TaskDocument? currentClaim = fixture.Service.StartNext();

        Assert.IsNotNull(currentClaim);
        Assert.AreEqual(currentTask.Id, currentClaim.Task.Id);
        Assert.AreEqual("Running", currentClaim.Task.Status);
        Assert.AreEqual("Running", currentClaim.Task.Runs.Last().Status);

        TaskResponse resumedStaleTask = fixture.Service.Control(
            staleTask.Id,
            "resume",
            new TaskCommandRequest(LifecycleFixture.NewRequestId(), pausedStaleTask.Version));

        Assert.AreEqual("Queued", resumedStaleTask.Status);
        Assert.HasCount(2, resumedStaleTask.Runs);
        Assert.AreEqual("Cancelled", resumedStaleTask.Runs[0].Status);
        Assert.AreEqual("Queued", resumedStaleTask.Runs[1].Status);
        Assert.AreNotEqual(staleRunId, resumedStaleTask.Runs[1].Id);
        Assert.AreEqual(fixture.Connections.CurrentHash, fixture.Store.ReadTask(staleTask.Id)!.Checkpoints.Last().ConnectionHash);
    }

    [TestMethod]
    public void CancelWhileStopping_PreservesStoppingUntilWorkerAcknowledges()
    {
        using var fixture = new LifecycleFixture();
        TaskResponse task = fixture.CreateConfirmedTask();
        _ = fixture.StartNext(task.Id);
        TaskResponse running = fixture.Read(task.Id);

        TaskResponse stopping = fixture.Service.Control(
            task.Id,
            "pause",
            new TaskCommandRequest(LifecycleFixture.NewRequestId(), running.Version));
        Assert.AreEqual("Stopping", stopping.Status);

        TaskResponse cancelling = fixture.Service.Control(
            task.Id,
            "cancel",
            new TaskCommandRequest(LifecycleFixture.NewRequestId(), stopping.Version));

        Assert.AreEqual("Stopping", cancelling.Status);
        Assert.AreEqual("Stopping", cancelling.Runs.Last().Status);
        Assert.AreEqual("cancel", fixture.Store.ReadTask(task.Id)!.Checkpoints.Last().ControlIntent);

        TaskResponse repeatedCancel = fixture.Service.Control(
            task.Id,
            "cancel",
            new TaskCommandRequest(LifecycleFixture.NewRequestId(), cancelling.Version));

        Assert.AreEqual("Stopping", repeatedCancel.Status);
        Assert.AreEqual("Stopping", repeatedCancel.Runs.Last().Status);
        Assert.AreEqual("cancel", fixture.Store.ReadTask(task.Id)!.Checkpoints.Last().ControlIntent);

        fixture.Service.StopAcknowledged(task.Id, cancelled: true);

        TaskResponse cancelled = fixture.Read(task.Id);
        Assert.AreEqual("Cancelled", cancelled.Status);
        Assert.AreEqual("Cancelled", cancelled.Runs.Last().Status);
        Assert.IsNotNull(cancelled.Runs.Last().EndedAt);
    }

    [TestMethod]
    public void CancelWhileReconciling_PreservesStoppingUntilWorkerAcknowledges()
    {
        using var fixture = new LifecycleFixture();
        TaskResponse task = fixture.CreateConfirmedTask();
        _ = fixture.StartNext(task.Id);
        fixture.Service.Fail(task.Id, "WORKER_STOP_UNCONFIRMED", "The worker stop could not be confirmed.", blocked: true);

        TaskResponse reconciling = fixture.Read(task.Id);
        Assert.AreEqual("Blocked", reconciling.Status);
        Assert.AreEqual("Reconciling", reconciling.Runs.Last().Status);
        Assert.AreEqual("WORKER_STOP_UNCONFIRMED", reconciling.Runs.Last().FailureCode);

        TaskResponse cancelling = fixture.Service.Control(
            task.Id,
            "cancel",
            new TaskCommandRequest(LifecycleFixture.NewRequestId(), reconciling.Version));

        Assert.AreEqual("Stopping", cancelling.Status);
        Assert.AreEqual("Stopping", cancelling.Runs.Last().Status);
        Assert.AreEqual("cancel", fixture.Store.ReadTask(task.Id)!.Checkpoints.Last().ControlIntent);

        fixture.Service.StopAcknowledged(task.Id, cancelled: true);

        TaskResponse cancelled = fixture.Read(task.Id);
        Assert.AreEqual("Cancelled", cancelled.Status);
        Assert.AreEqual("Cancelled", cancelled.Runs.Last().Status);
        Assert.IsNotNull(cancelled.Runs.Last().EndedAt);
    }

    [TestMethod]
    public void FailAfterPause_PreservesPauseIntent()
    {
        using var fixture = new LifecycleFixture();
        TaskResponse task = fixture.CreateConfirmedTask();
        _ = fixture.StartNext(task.Id);
        TaskResponse running = fixture.Read(task.Id);

        TaskResponse stopping = fixture.Service.Control(
            task.Id,
            "pause",
            new TaskCommandRequest(LifecycleFixture.NewRequestId(), running.Version));
        Assert.AreEqual("Stopping", stopping.Status);

        fixture.Service.Fail(task.Id, "TEST_STOP", "The worker exited after a pause request.");

        TaskResponse paused = fixture.Read(task.Id);
        Assert.AreEqual("Paused", paused.Status);
        Assert.AreEqual("Paused", paused.Runs.Last().Status);
        Assert.AreEqual("pause", fixture.Store.ReadTask(task.Id)!.Checkpoints.Last().ControlIntent);
    }

    [TestMethod]
    public void FailAfterCancel_PreservesCancelIntent()
    {
        using var fixture = new LifecycleFixture();
        TaskResponse task = fixture.CreateConfirmedTask();
        _ = fixture.StartNext(task.Id);
        TaskResponse running = fixture.Read(task.Id);

        TaskResponse stopping = fixture.Service.Control(
            task.Id,
            "cancel",
            new TaskCommandRequest(LifecycleFixture.NewRequestId(), running.Version));
        Assert.AreEqual("Stopping", stopping.Status);

        fixture.Service.Fail(task.Id, "TEST_STOP", "The worker exited after a cancel request.");

        TaskResponse cancelled = fixture.Read(task.Id);
        Assert.AreEqual("Cancelled", cancelled.Status);
        Assert.AreEqual("Cancelled", cancelled.Runs.Last().Status);
        Assert.AreEqual("cancel", fixture.Store.ReadTask(task.Id)!.Checkpoints.Last().ControlIntent);
        Assert.IsNotNull(cancelled.Runs.Last().EndedAt);
    }

    [TestMethod]
    public void StopAcknowledgedAfterCancel_UsesLatestControlIntent()
    {
        using var fixture = new LifecycleFixture();
        TaskResponse task = fixture.CreateConfirmedTask();
        _ = fixture.StartNext(task.Id);
        TaskResponse running = fixture.Read(task.Id);

        TaskResponse stopping = fixture.Service.Control(
            task.Id,
            "pause",
            new TaskCommandRequest(LifecycleFixture.NewRequestId(), running.Version));
        TaskResponse cancelling = fixture.Service.Control(
            task.Id,
            "cancel",
            new TaskCommandRequest(LifecycleFixture.NewRequestId(), stopping.Version));
        Assert.AreEqual("Stopping", cancelling.Status);
        Assert.AreEqual("cancel", fixture.Store.ReadTask(task.Id)!.Checkpoints.Last().ControlIntent);

        fixture.Service.StopAcknowledged(task.Id, cancelled: false);

        TaskResponse cancelled = fixture.Read(task.Id);
        Assert.AreEqual("Cancelled", cancelled.Status);
        Assert.AreEqual("Cancelled", cancelled.Runs.Last().Status);
        Assert.IsNotNull(cancelled.Runs.Last().EndedAt);
    }

    [TestMethod]
    public void BlockedFailureAfterCancel_RemainsReconciling()
    {
        using var fixture = new LifecycleFixture();
        TaskResponse task = fixture.CreateConfirmedTask();
        _ = fixture.StartNext(task.Id);
        TaskResponse running = fixture.Read(task.Id);
        TaskResponse stopping = fixture.Service.Control(
            task.Id,
            "cancel",
            new TaskCommandRequest(LifecycleFixture.NewRequestId(), running.Version));
        Assert.AreEqual("Stopping", stopping.Status);

        fixture.Service.Fail(task.Id, "WORKER_STOP_UNCONFIRMED", "The worker stop could not be confirmed.", blocked: true);

        TaskResponse blocked = fixture.Read(task.Id);
        Assert.AreEqual("Blocked", blocked.Status);
        Assert.AreEqual("Reconciling", blocked.Runs.Last().Status);
        Assert.AreEqual("WORKER_STOP_UNCONFIRMED", blocked.Runs.Last().FailureCode);
        Assert.AreEqual("cancel", fixture.Store.ReadTask(task.Id)!.Checkpoints.Last().ControlIntent);
    }

    [TestMethod]
    public void CancelAfterSuccessfulRun_PreservesRunResultAndArtifact()
    {
        using var fixture = new LifecycleFixture();
        TaskResponse task = fixture.CreateConfirmedTask();
        RunResponse firstAttempt = fixture.StartNext(task.Id);
        ApprovalResponse approval = fixture.Propose(task.Id, firstAttempt, FirstFileName, FirstContent);
        fixture.Approve(task.Id, approval);
        RunResponse publishingAttempt = fixture.StartNext(task.Id);
        fixture.Publish(task.Id, approval, publishingAttempt, FirstFileName, FirstContent);
        fixture.Complete(task.Id, publishingAttempt);

        TaskResponse inReview = fixture.Read(task.Id);
        RunResponse succeededRun = inReview.Runs.Last();
        ArtifactResponse artifact = inReview.Artifacts.Single();
        byte[] originalBytes = fixture.Publisher.Read(artifact, task.Id);
        Assert.AreEqual("InReview", inReview.Status);
        Assert.AreEqual("Succeeded", succeededRun.Status);
        Assert.IsNotNull(succeededRun.EndedAt);

        TaskResponse cancelled = fixture.Service.Control(
            task.Id,
            "cancel",
            new TaskCommandRequest(LifecycleFixture.NewRequestId(), inReview.Version));

        Assert.AreEqual("Cancelled", cancelled.Status);
        Assert.AreEqual("Succeeded", cancelled.Runs.Last().Status);
        Assert.AreEqual(succeededRun.EndedAt, cancelled.Runs.Last().EndedAt);
        Assert.AreEqual(artifact.Id, cancelled.Artifacts.Single().Id);
        Assert.AreEqual(artifact.ContentHash, cancelled.Artifacts.Single().ContentHash);
        CollectionAssert.AreEqual(originalBytes, fixture.Publisher.Read(artifact, task.Id));
        CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(fixture.MarkdownFiles().Single()));
    }

    [TestMethod]
    public void ModelCallLimitPersistsAcrossAttemptsAndRejectsOldAttemptFence()
    {
        using var fixture = new LifecycleFixture();
        TaskResponse task = fixture.CreateConfirmedTask();
        RunResponse firstAttempt = fixture.StartNext(task.Id);

        ModelCallResponse first = fixture.Service.Reserve(task.Id, fixture.ModelCall(firstAttempt, "call_first"), maxCalls: 2);
        ModelCallResponse second = fixture.Service.Reserve(task.Id, fixture.ModelCall(firstAttempt, "call_second"), maxCalls: 2);

        Assert.IsTrue(first.Allowed);
        Assert.AreEqual(1, first.RemainingCalls);
        Assert.IsTrue(second.Allowed);
        Assert.AreEqual(0, second.RemainingCalls);

        fixture.Service.Fail(task.Id, "TEST_RETRY", "Retry the same run.");
        TaskResponse failed = fixture.Read(task.Id);
        TaskResponse queued = fixture.Service.Control(
            task.Id,
            "retry",
            new TaskCommandRequest(LifecycleFixture.NewRequestId(), failed.Version));
        Assert.AreEqual("Queued", queued.Status);
        RunResponse secondAttempt = fixture.StartNext(task.Id);
        Assert.AreEqual(firstAttempt.Id, secondAttempt.Id);
        Assert.AreEqual(firstAttempt.AttemptNumber + 1, secondAttempt.AttemptNumber);
        Assert.AreNotEqual(firstAttempt.AttemptId, secondAttempt.AttemptId);

        ModelCallResponse afterRetry = fixture.Service.Reserve(task.Id, fixture.ModelCall(secondAttempt, "call_after_retry"), maxCalls: 2);
        Assert.IsFalse(afterRetry.Allowed);
        Assert.AreEqual(0, afterRetry.RemainingCalls);

        DomainException staleFence = Assert.ThrowsExactly<DomainException>(() => fixture.Service.Reserve(
            task.Id,
            fixture.ModelCall(firstAttempt, "call_old_fence"),
            maxCalls: 3));

        Assert.AreEqual("STALE_WORKER", staleFence.Code);
        TaskDocument persisted = fixture.Store.ReadTask(task.Id)!;
        Assert.HasCount(2, persisted.Usage);
        Assert.AreEqual(2, persisted.Task.Runs.Last().ModelCalls);
        Assert.AreEqual("Running", persisted.Task.Status);
    }

    [TestMethod]
    public void RecoverAfterRestart_PreservesPendingApprovalWithoutStartingOrPublishing()
    {
        using var fixture = new LifecycleFixture();
        TaskResponse task = fixture.CreateConfirmedTask();
        RunResponse oldAttempt = fixture.StartNext(task.Id);
        ApprovalResponse approval = fixture.Propose(task.Id, oldAttempt, FirstFileName, FirstContent);
        string oldHostEpoch = fixture.Store.HostEpoch;

        fixture.RestartAndRecover();

        TaskResponse recovered = fixture.Read(task.Id);
        Assert.AreEqual("WaitingApproval", recovered.Status);
        Assert.AreEqual("WaitingApproval", recovered.Runs.Last().Status);
        Assert.AreEqual(approval.Id, recovered.Approvals.Single().Id);
        Assert.AreEqual("Pending", recovered.Approvals.Single().Status);
        Assert.AreEqual(approval.PayloadHash, recovered.Approvals.Single().PayloadHash);
        Assert.IsNull(fixture.Service.StartNext());

        DomainException staleWorker = Assert.ThrowsExactly<DomainException>(() => fixture.Publish(
            task.Id,
            approval,
            oldAttempt,
            FirstFileName,
            FirstContent,
            oldHostEpoch));

        Assert.AreEqual("STALE_WORKER", staleWorker.Code);
        Assert.AreEqual("WaitingApproval", fixture.Read(task.Id).Status);
        Assert.IsEmpty(fixture.MarkdownFiles());
    }

    private sealed class LifecycleFixture : IDisposable
    {
        private readonly TestWorkspace workspace = new();

        public CareerStore Store { get; private set; }
        public ArtifactPublisher Publisher { get; }
        public VerifiedConnectionService Connections { get; } = new();
        public TaskService Service { get; private set; }

        public LifecycleFixture()
        {
            Store = OpenStore();
            Publisher = new ArtifactPublisher(workspace.DirectoryPath);
            Service = new TaskService(Store, Publisher, Connections);
        }

        public TaskResponse CreateConfirmedTask()
        {
            TaskResponse created = Service.Create(new CreateTaskRequest(
                NewRequestId(),
                "Lifecycle test",
                "Produce a verified report",
                ["Include a real published file"],
                "Only use this fixture input."));

            return Service.ConfirmPlan(
                created.Id,
                new ConfirmPlanRequest(NewRequestId(), created.Version, created.Revision, created.Plan.ProposalHash));
        }

        public RunResponse StartNext(string taskId)
        {
            TaskDocument started = Service.StartNext() ?? throw new AssertFailedException("A confirmed task should be claimable.");
            if (started.Task.Id != taskId)
                throw new AssertFailedException("The store claimed an unexpected task.");
            return started.Task.Runs.Last();
        }

        public ApprovalResponse Propose(string taskId, RunResponse attempt, string fileName, string content)
        {
            Service.AwaitApproval(taskId, WorkerResponse(
                attempt,
                status: "ApprovalRequired",
                session: OpaqueJson("{\"session\":\"opaque\"}"),
                approval: OpaqueJson("{\"approval\":\"opaque\"}"),
                fileName,
                content));
            return Read(taskId).Approvals.Last();
        }

        public TaskResponse Approve(string taskId, ApprovalResponse approval) => Decide(taskId, approval, "approve");

        public TaskResponse Decide(string taskId, ApprovalResponse approval, string decision)
        {
            TaskResponse current = Read(taskId);
            return Service.DecideApproval(
                approval.Id,
                new ApprovalDecisionRequest(NewRequestId(), current.Version, approval.PayloadHash, decision));
        }

        public WorkerToolResponse Publish(
            string taskId,
            ApprovalResponse approval,
            RunResponse attempt,
            string fileName,
            string content,
            string? hostEpoch = null) => Service.Publish(taskId, new WorkerToolRequest(
                Protocol.Version,
                attempt.Id,
                attempt.AttemptId!,
                hostEpoch ?? Store.HostEpoch,
                attempt.AttemptNumber,
                approval.OperationId,
                fileName,
                content));

        public void Complete(string taskId, RunResponse attempt) => Service.Complete(
            taskId,
            WorkerResponse(attempt, "Succeeded", OpaqueJson("{\"session\":\"complete\"}"), null, null, null));

        public AcceptanceRequest Acceptance(
            TaskResponse task,
            ArtifactResponse artifact,
            string decision,
            string note) => new(
                NewRequestId(),
                task.Version,
                task.Revision,
                artifact.Id,
                artifact.Version,
                artifact.ContentHash,
                decision,
                note);

        public ModelCallRequest ModelCall(RunResponse attempt, string callId) => new(
            Protocol.Version,
            attempt.Id,
            attempt.AttemptId!,
            Store.HostEpoch,
            attempt.AttemptNumber,
            callId,
            MaxOutputTokens: 256);

        public TaskResponse Read(string taskId) => Service.Read(taskId);

        public string[] MarkdownFiles()
        {
            string artifactDirectory = Path.Combine(workspace.DirectoryPath, "artifacts");
            return Directory.Exists(artifactDirectory)
                ? Directory.GetFiles(artifactDirectory, "*.md", SearchOption.AllDirectories)
                : [];
        }

        public void RestartAndRecover()
        {
            Store.Dispose();
            Store = OpenStore();
            Service = new TaskService(Store, Publisher, Connections);
            Service.RecoverAfterRestart();
        }

        public static string NewRequestId() => Guid.NewGuid().ToString("D");

        private CareerStore OpenStore() => new(workspace.DirectoryPath, workspaceId: "workspace_lifecycle", verifyProcessLock: false);

        private WorkerResponse WorkerResponse(
            RunResponse attempt,
            string status,
            JsonElement? session,
            JsonElement? approval,
            string? fileName,
            string? content) => new(
                Protocol.Version,
                attempt.Id,
                attempt.AttemptId!,
                Store.HostEpoch,
                attempt.AttemptNumber,
                status,
                session,
                approval,
                CallId: null,
                ToolName: fileName is null ? null : "publish_document",
                FileName: fileName,
                Content: content,
                Text: "Lifecycle fixture response",
                ModelCalls: 0,
                InputTokens: 0,
                OutputTokens: 0,
                UsageState: "none");

        private static JsonElement OpaqueJson(string json)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        public void Dispose()
        {
            Store.Dispose();
            workspace.Dispose();
        }
    }

    private sealed class VerifiedConnectionService : IConnectionService
    {
        private readonly ConnectionResponse connection = new(
            "connection_test",
            "Verified test connection",
            "openai-compatible",
            "http://127.0.0.1:1/v1",
            "test-model",
            HasCredential: true,
            AllowLocalEndpoint: true,
            TestState: "verified",
            TestMessage: "Fake verified configuration; no network is used.",
            TestedAt: DateTimeOffset.UnixEpoch,
            MaxOutputTokens: 256,
            MaxModelCalls: 6,
            MaxInputCharacters: 32000);

        public ConnectionResponse? Read() => connection;
        public string? CurrentHash => new string('a', 64);
        public ValueTask<ConnectionResponse> SaveAsync(SaveConnectionRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(connection);

        public ValueTask<WorkerRequest> CreateWorkerRequestAsync(
            TaskResponse task,
            RunResponse run,
            RunCheckpoint checkpoint,
            string hostEpoch,
            long fencingToken,
            bool testOnly,
            CancellationToken cancellationToken) => throw new NotSupportedException("Lifecycle tests do not call a worker adapter.");

        public void RecordTest(string configurationHash, bool succeeded, string message)
        {
        }
    }
}
