using Career.Core;

namespace Career.Core.Tests;

[TestClass]
public sealed class DomainRulesTests
{
    [TestMethod]
    public void PlanHashChangesWhenRevisionOrInputChanges()
    {
        const string goal = "Prepare implementation";
        string[] criteria = ["include tests", "include risks"];
        const string input = "Repository and task context";

        string original = DomainRules.PlanHash(goal, criteria, input, revision: 3);

        Assert.AreEqual(original, DomainRules.PlanHash(goal, criteria, input, revision: 3));
        Assert.AreNotEqual(original, DomainRules.PlanHash(goal, criteria, input, revision: 4));
        Assert.AreNotEqual(original, DomainRules.PlanHash(goal, criteria, input + " updated", revision: 3));
    }

    [TestMethod]
    public void PublicationHashChangesWhenContentOrTargetChanges()
    {
        const string content = "# Result\nVerified output";
        string original = DomainRules.PublicationHash("result.md", content);

        Assert.AreNotEqual(original, DomainRules.PublicationHash("renamed.md", content));
        Assert.AreNotEqual(original, DomainRules.PublicationHash("result.md", content + " changed"));
    }

    [TestMethod]
    [DataRow("../result.md")]
    [DataRow("reports/result.md")]
    [DataRow("reports\\result.md")]
    [DataRow("C:result.md")]
    [DataRow(".draft.md")]
    [DataRow("result.txt")]
    [DataRow("bad\nname.md")]
    [DataRow("this-filename-is-more-than-one-hundred-and-twenty-characters-long-which-the-domain-rejects-to-keep-publication-names-bounded.md")]
    public void FileNameRejectsUnsafeOrUnsupportedNames(string fileName)
    {
        DomainException exception = Assert.ThrowsExactly<DomainException>(() => DomainRules.FileName(fileName));

        Assert.AreEqual("INVALID_ARTIFACT_NAME", exception.Code);
        Assert.AreEqual(400, exception.Status);
    }

    [TestMethod]
    public void RequestIdRejectsMalformedValueAndAcceptsGuid()
    {
        DomainException exception = Assert.ThrowsExactly<DomainException>(() => DomainRules.RequestId("request-123"));

        Assert.AreEqual("INVALID_REQUEST_ID", exception.Code);
        Assert.AreEqual(400, exception.Status);
        DomainRules.RequestId("64c62702-98af-44e7-8741-52c574532e80");
    }
}
