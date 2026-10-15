using System.Security.Cryptography;
using System.Text;
using Career.Core;
using Career.Infrastructure;

namespace Career.Core.Tests;

[TestClass]
public sealed class ArtifactPublisherTests
{
    [TestMethod]
    public void PublishReturnsHashOfWrittenFileBytes()
    {
        using var workspace = new TestWorkspace();
        var publisher = new ArtifactPublisher(workspace.DirectoryPath);
        const string content = "# 验证结果\n已完成并检查。\n";
        OperationDocument operation = TestDocuments.Operation("operation_publish_1", "result.md", content);

        var artifact = publisher.Publish(
            "task_1",
            "run_1",
            revision: 3,
            version: 1,
            operation,
            ["source.md"],
            ["build passed"],
            []);

        string artifactId = "artifact_" + DomainRules.Hash(operation.Id)[..24];
        string publishedPath = Path.Combine(workspace.DirectoryPath, "artifacts", artifactId, "1", "result.md");
        byte[] publishedBytes = File.ReadAllBytes(publishedPath);
        byte[] readBytes = publisher.Read(artifact, "task_1");

        Assert.AreEqual(DomainRules.Hash(content), artifact.ContentHash);
        Assert.AreEqual(artifact.ContentHash, Convert.ToHexStringLower(SHA256.HashData(publishedBytes)));
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(content), publishedBytes);
        CollectionAssert.AreEqual(publishedBytes, readBytes);
        Assert.AreEqual(Encoding.UTF8.GetByteCount(content), artifact.SizeBytes);
    }

    [TestMethod]
    public void RetryQuarantinesPartialStageAndPublishes()
    {
        using var workspace = new TestWorkspace();
        var publisher = new ArtifactPublisher(workspace.DirectoryPath);
        const string operationId = "operation_partial_1";
        const string content = "# Recovered publication\n";
        string stageDirectory = Path.Combine(workspace.DirectoryPath, "state", "staging", operationId);
        Directory.CreateDirectory(stageDirectory);
        File.WriteAllText(Path.Combine(stageDirectory, "partial.tmp"), "incomplete stage");

        var artifact = publisher.Publish(
            "task_2",
            "run_2",
            revision: 1,
            version: 1,
            TestDocuments.Operation(operationId, "recovered.md", content),
            [],
            [],
            []);

        string[] quarantined = Directory.GetDirectories(Path.Combine(workspace.DirectoryPath, "state", "quarantine"));
        Assert.HasCount(1, quarantined);
        Assert.AreEqual("incomplete stage", File.ReadAllText(Path.Combine(quarantined[0], "partial.tmp")));
        Assert.IsFalse(Directory.Exists(stageDirectory));
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(content), publisher.Read(artifact, "task_2"));
    }

    [TestMethod]
    public void ReadRejectsTamperingAndPreservesFileBytes()
    {
        using var workspace = new TestWorkspace();
        var publisher = new ArtifactPublisher(workspace.DirectoryPath);
        const string operationId = "operation_tamper_1";
        var artifact = publisher.Publish(
            "task_3",
            "run_3",
            revision: 1,
            version: 1,
            TestDocuments.Operation(operationId, "final.md", "# Original\n"),
            [],
            [],
            []);

        string artifactId = "artifact_" + DomainRules.Hash(operationId)[..24];
        string path = Path.Combine(workspace.DirectoryPath, "artifacts", artifactId, "1", "final.md");
        byte[] tampered = Encoding.UTF8.GetBytes("# Tampered\n");
        File.WriteAllBytes(path, tampered);

        DomainException failure = Assert.ThrowsExactly<DomainException>(() => publisher.Read(artifact, "task_3"));

        Assert.AreEqual("ARTIFACT_INTEGRITY_FAILURE", failure.Code);
        CollectionAssert.AreEqual(tampered, File.ReadAllBytes(path));
    }
}
