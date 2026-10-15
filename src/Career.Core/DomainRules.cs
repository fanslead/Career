using System.Security.Cryptography;
using System.Text;
using Career.Contracts;

namespace Career.Core;

public sealed class DomainException(string code, string message, int status = 409, TaskResponse? current = null) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    public TaskResponse? Current { get; } = current;
}

public static class DomainRules
{
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string Id(string prefix) => prefix + "_" + Guid.NewGuid().ToString("N");
    public static void RequestId(string value)
    {
        if (!Guid.TryParse(value, out _)) throw new DomainException("INVALID_REQUEST_ID", "请求标识无效，请刷新后重新提交。", 400);
    }
    public static void Version(TaskResponse task, long expected)
    {
        if (task.Version != expected) throw new DomainException("VERSION_CONFLICT", "任务已被更新。你的输入仍可保留，请比较最新版本。", 409, task);
    }
    public static void Text(string? value, string label, int max, bool optional = false)
    {
        if ((!optional && string.IsNullOrWhiteSpace(value)) || (value?.Length ?? 0) > max)
            throw new DomainException("INVALID_INPUT", label + "不能为空，且不能超过 " + max + " 个字符。", 400);
    }
    public static string[] Actions(TaskResponse task, bool intakePaused)
    {
        return task.Status switch
        {
            "AwaitingPlan" => ["confirm-plan", "cancel"],
            "Queued" => ["pause", "cancel"],
            "Running" => ["pause", "cancel"],
            "WaitingApproval" => ["view-approval", "pause", "cancel"],
            "Paused" => ["resume", "cancel"],
            "Failed" => ["retry", "cancel"],
            "InReview" => ["view-artifact", "accept", "return", "cancel"],
            "Done" => ["view-artifact"],
            "Cancelled" => task.Artifacts.Length > 0 ? ["view-artifact"] : [],
            "Blocked" => ["retry", "cancel"],
            _ => []
        };
    }
    public static string PlanHash(string goal, string[] criteria, string inputText, int revision)
        => Hash(revision + "\n" + goal + "\n" + string.Join("\n", criteria) + "\n" + Hash(inputText));
    public static string PublicationHash(string fileName, string content)
        => Hash(fileName + "\n" + Hash(content));
    public static void FileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 120 || name != Path.GetFileName(name) || name.Contains('/') || name.Contains('\\') || name.Contains(':') || name.StartsWith('.') || name.Any(char.IsControl) || !name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            throw new DomainException("INVALID_ARTIFACT_NAME", "成果文件名应为不含目录的 Markdown 文件名。", 400);
    }
}
