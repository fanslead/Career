using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace P0.Host;

// Intentionally a small probe store, not the future application data model.
public sealed class ProbeStore : IDisposable
{
    private readonly object gate = new();
    private readonly string connectionString;
    private readonly WorkspaceLock workspaceLock;
    public string Root { get; }
    public string SqliteVersion { get; }
    public bool Fts5 { get; }

    public ProbeStore(string root)
    {
        Root = root;
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "receipts"));
        Directory.CreateDirectory(Path.Combine(root, "staging"));
        Directory.CreateDirectory(Path.Combine(root, "quarantine"));
        workspaceLock = new WorkspaceLock(Path.Combine(root, ".host.lock"));
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "app.db") }.ToString();
        using var db = Open();
        Exec(db, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS runs (
              id TEXT PRIMARY KEY, prompt TEXT NOT NULL, scenario TEXT NOT NULL, status TEXT NOT NULL,
              session TEXT, approval TEXT, operation_id TEXT, args_json TEXT, args_hash TEXT, decision INTEGER,
              text TEXT NOT NULL DEFAULT '', worker_pid INTEGER, model_calls INTEGER NOT NULL DEFAULT 0,
              tool_calls INTEGER NOT NULL DEFAULT 0, error TEXT, fault TEXT NOT NULL DEFAULT '',
              cancel_requested INTEGER NOT NULL DEFAULT 0, created_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS tool_operations (
              id TEXT PRIMARY KEY, run_id TEXT NOT NULL UNIQUE REFERENCES runs(id), args_hash TEXT NOT NULL,
              state TEXT NOT NULL, result TEXT);
            CREATE TABLE IF NOT EXISTS events (
              seq INTEGER PRIMARY KEY AUTOINCREMENT, run_id TEXT NOT NULL REFERENCES runs(id),
              type TEXT NOT NULL, payload TEXT NOT NULL, created_utc TEXT NOT NULL);
            CREATE VIRTUAL TABLE IF NOT EXISTS probe_search USING fts5(text);
            """);
        if (Scalar(db, "SELECT count(*) FROM pragma_table_info('runs') WHERE name='cancel_requested'") == "0")
            Exec(db, "ALTER TABLE runs ADD COLUMN cancel_requested INTEGER NOT NULL DEFAULT 0");
        SqliteVersion = Scalar(db, "SELECT sqlite_version()")!;
        Fts5 = Scalar(db, "SELECT name FROM sqlite_master WHERE name='probe_search'") is not null;
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        Exec(db, "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;");
        return db;
    }

    private static SqliteCommand Command(SqliteConnection db, string sql, params (string, object?)[] args)
    {
        var command = db.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static int Exec(SqliteConnection db, string sql, params (string, object?)[] args)
    {
        using var cmd = Command(db, sql, args);
        return cmd.ExecuteNonQuery();
    }

    private static string? Scalar(SqliteConnection db, string sql, params (string, object?)[] args)
    {
        using var cmd = Command(db, sql, args);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Event(SqliteConnection db, string id, string type, string text) => Exec(db,
        "INSERT INTO events(run_id,type,payload,created_utc) VALUES($id,$type,$payload,$time)",
        ("$id", id), ("$type", type), ("$payload", JsonSerializer.Serialize(new EventPayload(text), ProbeJson.Default.EventPayload)),
        ("$time", DateTimeOffset.UtcNow.ToString("O")));

    public RunView Create(CreateRun input, string fault)
    {
        lock (gate)
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            var id = Guid.NewGuid().ToString("N");
            Exec(db, "INSERT INTO runs(id,prompt,scenario,status,fault,created_utc) VALUES($id,$prompt,$scenario,'Queued',$fault,$time)",
                ("$id", id), ("$prompt", input.Prompt), ("$scenario", input.Scenario), ("$fault", fault), ("$time", DateTimeOffset.UtcNow.ToString("O")));
            Event(db, id, "RunQueued", "真实 MAF + 可控模型替身");
            tx.Commit();
            return Get(id)!;
        }
    }

    public RunView? Get(string id)
    {
        lock (gate)
        {
            using var db = Open();
            using var cmd = Command(db, "SELECT id,status,prompt,operation_id,args_hash,worker_pid,model_calls,tool_calls,text,error,args_json FROM runs WHERE id=$id", ("$id", id));
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            var op = r.IsDBNull(3) ? null : r.GetString(3);
            return new RunView(r.GetString(0), r.GetString(1), r.GetString(2), op, r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetInt32(5), r.GetInt32(6), r.GetInt32(7), r.GetString(8),
                r.IsDBNull(9) ? null : r.GetString(9), op is not null && ValidReceipt(ReceiptPath(op), new Receipt(op, r.GetString(2))) ? 1 : 0, r.IsDBNull(10) ? null : r.GetString(10));
        }
    }

    public List<RunView> List()
    {
        lock (gate)
        {
            using var db = Open();
            var ids = new List<string>();
            using (var cmd = Command(db, "SELECT id FROM runs ORDER BY created_utc DESC"))
            using (var r = cmd.ExecuteReader()) while (r.Read()) ids.Add(r.GetString(0));
            return ids.Select(id => Get(id)!).ToList();
        }
    }

    public WorkerInput Begin(string id)
    {
        lock (gate)
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            WorkerInput input;
            using (var cmd = Command(db, "SELECT prompt,session,approval,decision,operation_id,scenario,status FROM runs WHERE id=$id", ("$id", id)))
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read() || r.GetString(6) is not ("Queued" or "Approved")) throw new InvalidOperationException("Run is not schedulable.");
                input = new WorkerInput(id, r.GetString(0), Parse(r, 1), Parse(r, 2), r.IsDBNull(3) ? null : r.GetInt32(3) == 1,
                    r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5));
            }
            Exec(db, "UPDATE runs SET status='Running',error=NULL WHERE id=$id", ("$id", id));
            Event(db, id, "RunStarted", input.Session is null ? "创建 MAF 会话" : "从已保存的 MAF 会话恢复");
            tx.Commit();
            return input;
        }
    }

    private static JsonElement? Parse(SqliteDataReader r, int index)
    {
        if (r.IsDBNull(index)) return null;
        using var doc = JsonDocument.Parse(r.GetString(index));
        return doc.RootElement.Clone();
    }

    public void WorkerStarted(string id, int pid)
    {
        lock (gate)
        {
            using var db = Open();
            Exec(db, "UPDATE runs SET worker_pid=$pid WHERE id=$id", ("$pid", pid), ("$id", id));
            Event(db, id, "WorkerStarted", $"pid={pid}");
        }
    }

    public void AddEvent(string id, string type, string text)
    {
        lock (gate) { using var db = Open(); Event(db, id, type, text); }
    }

    public void Complete(string id, WorkerResult result)
    {
        lock (gate)
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            if (Scalar(db, "SELECT cancel_requested FROM runs WHERE id=$id", ("$id", id)) == "1")
            {
                var state = Scalar(db, "SELECT state FROM tool_operations WHERE run_id=$id", ("$id", id));
                var status = state is "Executing" or "Quarantined" ? "UnknownOutcome" : "Cancelled";
                Exec(db, "UPDATE runs SET status=$status WHERE id=$id", ("$status", status), ("$id", id));
                Event(db, id, "Run" + status, "持久取消意图优先；保留已发生的动作");
            }
            else if (result.Status == "WaitingApproval")
            {
                if (result.ToolName != "record_note" || string.IsNullOrEmpty(result.CallId) || result.Approval is null || result.Arguments is null)
                    throw new InvalidDataException("Incomplete approval request.");
                var args = Normalize(result.Arguments.Value);
                var prompt = Scalar(db, "SELECT prompt FROM runs WHERE id=$id", ("$id", id));
                if (JsonSerializer.Deserialize(args, ProbeJson.Default.NoteArgs)!.Text != prompt)
                    throw new InvalidDataException("Probe tool arguments differ from approved input.");
                var op = Hash(id + ":" + result.CallId);
                var hash = Hash("record_note:v1\n" + id + "\n" + args);
                Exec(db, "UPDATE runs SET status='WaitingApproval',session=$session,approval=$approval,operation_id=$op,args_json=$args,args_hash=$hash WHERE id=$id",
                    ("$session", result.Session.GetRawText()), ("$approval", result.Approval.Value.GetRawText()), ("$op", op), ("$args", args), ("$hash", hash), ("$id", id));
                Event(db, id, "ApprovalRequested", "工具尚未执行；请求、参数 hash 与 MAF 会话已保存");
            }
            else
            {
                var decision = Scalar(db, "SELECT decision FROM runs WHERE id=$id", ("$id", id));
                var state = Scalar(db, "SELECT state FROM tool_operations WHERE run_id=$id", ("$id", id));
                var final = decision == "0" ? "Denied" : state == "Succeeded"
                    ? result.Status == "Succeeded" ? "Succeeded" : "FinalizationPending" : "Failed";
                Exec(db, "UPDATE runs SET status=$status,session=CASE WHEN $status='FinalizationPending' THEN session ELSE $session END,text=$text WHERE id=$id", ("$status", final),
                    ("$session", result.Session.GetRawText()), ("$text", result.Text ?? ""), ("$id", id));
                Event(db, id, "Run" + final, result.Text ?? "No final text.");
            }
            Exec(db, "UPDATE runs SET model_calls=model_calls+$models,tool_calls=tool_calls+$tools WHERE id=$id",
                ("$models", result.ModelCalls), ("$tools", result.ToolCalls), ("$id", id));
            tx.Commit();
        }
    }

    public bool Decide(string id, Decision decision)
    {
        lock (gate)
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            var changed = Exec(db, "UPDATE runs SET status='Approved',decision=$decision WHERE id=$id AND status='WaitingApproval' AND args_hash=$hash",
                ("$decision", decision.Approved ? 1 : 0), ("$id", id), ("$hash", decision.ExpectedHash));
            if (changed != 1) return false;
            if (decision.Approved)
                Exec(db, "INSERT INTO tool_operations(id,run_id,args_hash,state) SELECT operation_id,id,args_hash,'Approved' FROM runs WHERE id=$id", ("$id", id));
            Event(db, id, decision.Approved ? "ApprovalGranted" : "ApprovalDenied", "已绑定不可变操作与参数 hash");
            tx.Commit();
            return true;
        }
    }

    public bool RequestCancel(string id)
    {
        lock (gate)
        {
            using var db = Open(); using var tx = db.BeginTransaction();
            if (Exec(db, """
                UPDATE runs SET cancel_requested=1,status='Cancelling'
                WHERE id=$id AND cancel_requested=0
                  AND status IN ('Queued','Running','WaitingApproval','Approved','Executing','FinalizationPending')
                """, ("$id", id)) != 1) return false;
            Event(db, id, "RunCancelRequested", "取消意图已持久保存，停止新动作并核对在途效果");
            tx.Commit();
            return true;
        }
    }

    public async Task AfterCancellationRequested(string id, CancellationToken cancellationToken)
    {
        string fault;
        lock (gate) { using var db = Open(); fault = Scalar(db, "SELECT fault FROM runs WHERE id=$id", ("$id", id)) ?? ""; }
        await FaultBarrier(fault, "cancel-persisted", cancellationToken);
    }

    public async Task<Receipt> ExecuteToolAsync(string id, string operationId, string name, JsonElement arguments, CancellationToken cancellationToken)
    {
        var args = Normalize(arguments);
        var expected = Hash("record_note:v1\n" + id + "\n" + args);
        string fault;
        lock (gate)
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            string state;
            string? result;
            using (var cmd = Command(db, "SELECT o.state,o.result,r.fault FROM tool_operations o JOIN runs r ON r.id=o.run_id WHERE o.id=$op AND o.run_id=$id AND o.args_hash=$hash AND r.decision=1 AND r.status='Running'", ("$op", operationId), ("$id", id), ("$hash", expected)))
            using (var r = cmd.ExecuteReader())
            {
                if (name != "record_note" || !r.Read()) throw new InvalidOperationException("No matching approval for this exact tool payload.");
                state = r.GetString(0); result = r.IsDBNull(1) ? null : r.GetString(1); fault = r.GetString(2);
            }
            if (state == "Succeeded")
            {
                var cached = JsonSerializer.Deserialize(result!, ProbeJson.Default.Receipt)!;
                if (!ValidReceipt(ReceiptPath(operationId), cached))
                {
                    Exec(db, "UPDATE tool_operations SET state='Quarantined' WHERE id=$op", ("$op", operationId));
                    Exec(db, "UPDATE runs SET status='UnknownOutcome',error='receipt_corrupt_or_conflicting' WHERE id=$id", ("$id", id));
                    Event(db, id, "ReceiptQuarantined", "收尾前回执缺失或损坏，停止结果复用并保留待核对状态");
                    tx.Commit();
                    if (File.Exists(ReceiptPath(operationId))) Quarantine(ReceiptPath(operationId), operationId);
                    throw new InvalidDataException("receipt_corrupt_or_conflicting");
                }
                Event(db, id, "ToolResultReused", "已确认的结果被复用，没有再次执行副作用");
                tx.Commit();
                return cached;
            }
            if (state != "Approved") throw new InvalidOperationException("Outcome requires reconciliation; refusing replay.");
            Exec(db, "UPDATE tool_operations SET state='Executing' WHERE id=$op", ("$op", operationId));
            Exec(db, "UPDATE runs SET status='Executing' WHERE id=$id", ("$id", id));
            Event(db, id, "ToolExecuting", "意图已持久化，开始已批准的本地测试写入");
            tx.Commit();
        }
        await FaultBarrier(fault, "before-effect", cancellationToken);
        var receipt = new Receipt(operationId, JsonSerializer.Deserialize(args, ProbeJson.Default.NoteArgs)!.Text);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, ProbeJson.Default.Receipt);
        var staged = StagingPath(operationId);
        using (var file = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            file.Write(bytes.AsSpan(0, bytes.Length / 2));
            file.Flush(flushToDisk: true);
            await FaultBarrier(fault, "receipt-partial", cancellationToken);
            file.Write(bytes.AsSpan(bytes.Length / 2));
            file.Flush(flushToDisk: true);
        }
        await FaultBarrier(fault, "receipt-flushed", cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // Same-volume publication without replacing an existing result. The final
        // name is never opened for writing, even after an interrupted attempt.
        File.Move(staged, ReceiptPath(operationId), overwrite: false);
        if (!ValidReceipt(ReceiptPath(operationId), receipt)) throw new InvalidDataException("Published receipt failed validation.");
        await FaultBarrier(fault, "after-effect", cancellationToken);
        lock (gate)
        {
            using var db = Open(); using var tx = db.BeginTransaction();
            Exec(db, "UPDATE tool_operations SET state='Succeeded',result=$result WHERE id=$op", ("$result", JsonSerializer.Serialize(receipt, ProbeJson.Default.Receipt)), ("$op", operationId));
            Exec(db, "UPDATE runs SET status=CASE WHEN cancel_requested=1 THEN 'Cancelling' ELSE 'Running' END WHERE id=$id", ("$id", id));
            Event(db, id, "ToolCompleted", "测试 receipt 已持久化，结果已记账");
            tx.Commit();
        }
        await FaultBarrier(fault, "after-ledger", cancellationToken);
        return receipt;
    }

    public async Task BeforeFinalCommit(string id, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            using var db = Open();
            if (Exec(db, """
                UPDATE runs SET fault='' WHERE id=$id AND fault='before-final-commit'
                  AND EXISTS(SELECT 1 FROM tool_operations WHERE run_id=$id AND state='Succeeded')
                """, ("$id", id)) != 1) return;
        }
        await FaultBarrier("before-final-commit", "before-final-commit", cancellationToken);
    }

    private async Task FaultBarrier(string fault, string point, CancellationToken cancellationToken)
    {
        if (fault != point) return;
        await File.WriteAllTextAsync(Path.Combine(Root, "fault-ready"), point, cancellationToken);
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }

    public bool Reconcile(string id)
    {
        lock (gate)
        {
            using var db = Open(); using var tx = db.BeginTransaction();
            if (Scalar(db, "SELECT status FROM runs WHERE id=$id", ("$id", id)) != "UnknownOutcome") return false;
            var op = Scalar(db, "SELECT operation_id FROM runs WHERE id=$id", ("$id", id))!;
            var args = Scalar(db, "SELECT args_json FROM runs WHERE id=$id", ("$id", id))!;
            if (Scalar(db, "SELECT state FROM tool_operations WHERE id=$op", ("$op", op)) == "Quarantined")
            {
                if (File.Exists(ReceiptPath(op))) Quarantine(ReceiptPath(op), op);
                return false;
            }
            var path = ReceiptPath(op);
            var staged = StagingPath(op);
            var cancelled = Scalar(db, "SELECT cancel_requested FROM runs WHERE id=$id", ("$id", id)) == "1";
            var expected = new Receipt(op, JsonSerializer.Deserialize(args, ProbeJson.Default.NoteArgs)!.Text);
            if (File.Exists(path) && !ValidReceipt(path, expected))
            {
                // Persist the block BEFORE moving the bad file: a crash between
                // these steps must never turn a missing final name into permission
                // to repeat an operation whose outcome is uncertain.
                Exec(db, "UPDATE tool_operations SET state='Quarantined' WHERE id=$op", ("$op", op));
                Exec(db, "UPDATE runs SET error='receipt_corrupt_or_conflicting' WHERE id=$id", ("$id", id));
                Event(db, id, "ReceiptQuarantined", "最终回执损坏或内容冲突，保留未知结果并阻止再次执行");
                tx.Commit();
                Quarantine(path, op);
                return false;
            }
            if (!File.Exists(path) && File.Exists(staged))
            {
                if (!cancelled && ValidReceipt(staged, expected))
                {
                    File.Move(staged, path, overwrite: false);
                    Event(db, id, "ReceiptPublishedDuringRecovery", "完整暂存回执已核验并发布，没有重新执行工具");
                }
                else
                {
                    Quarantine(staged, op);
                    Event(db, id, "PartialReceiptQuarantined", "未发布的暂存文件已隔离，不作为已完成效果");
                }
            }
            if (File.Exists(path))
            {
                if (!ValidReceipt(path, expected)) return false;
                Exec(db, "UPDATE tool_operations SET state='Succeeded',result=$result WHERE id=$op", ("$result", JsonSerializer.Serialize(expected, ProbeJson.Default.Receipt)), ("$op", op));
                Event(db, id, "OutcomeReconciled", "已核对本地完整 receipt，恢复时只回传缓存结果");
            }
            else
            {
                // Valid only for this probe's exclusive, immutable local-file side effect.
                // Absence is NOT evidence of non-execution for SMTP or a remote API.
                Exec(db, "UPDATE tool_operations SET state=$state WHERE id=$op", ("$state", cancelled ? "Cancelled" : "Approved"), ("$op", op));
                Event(db, id, "OutcomeReconciled", cancelled ? "已取消；此专用目标确认没有已发布结果，不再执行" : "此专用本地测试目标确认无 receipt，允许重试一次");
            }
            Exec(db, "UPDATE runs SET status=$status,fault='',error=NULL WHERE id=$id", ("$status", cancelled ? "Cancelled" : "Approved"), ("$id", id));
            File.Delete(Path.Combine(Root, "fault-ready"));
            tx.Commit();
            return true;
        }
    }

    public bool ResumeFinalization(string id)
    {
        lock (gate)
        {
            using var db = Open(); using var tx = db.BeginTransaction();
            if (Exec(db, """
                UPDATE runs SET status='Approved',error=NULL
                WHERE id=$id AND status='FinalizationPending' AND cancel_requested=0 AND session IS NOT NULL AND approval IS NOT NULL
                  AND decision=1 AND EXISTS(SELECT 1 FROM tool_operations WHERE run_id=$id AND state='Succeeded')
                """, ("$id", id)) != 1) return false;
            Event(db, id, "FinalizationResumed", "工具已成功，仅继续整理结果；副作用不会再次执行");
            tx.Commit();
            return true;
        }
    }

    public List<string> Recover()
    {
        lock (gate)
        {
            using var db = Open(); using var tx = db.BeginTransaction();
            var quarantined = new List<string>();
            using (var cmd = Command(db, "SELECT id FROM tool_operations WHERE state='Quarantined'"))
            using (var r = cmd.ExecuteReader()) while (r.Read()) quarantined.Add(r.GetString(0));
            foreach (var op in quarantined)
                if (File.Exists(ReceiptPath(op))) Quarantine(ReceiptPath(op), op);
            var pending = new List<string>();
            using (var cmd = Command(db, "SELECT id FROM runs WHERE status IN ('Queued','Approved','Running','Executing','FinalizationPending','Cancelling')"))
            using (var r = cmd.ExecuteReader()) while (r.Read()) pending.Add(r.GetString(0));
            var resume = new List<string>();
            foreach (var id in pending)
            {
                var operationState = Scalar(db, "SELECT state FROM tool_operations WHERE run_id=$id", ("$id", id));
                var status = Scalar(db, "SELECT status FROM runs WHERE id=$id", ("$id", id));
                if (Scalar(db, "SELECT cancel_requested FROM runs WHERE id=$id", ("$id", id)) == "1")
                {
                    Exec(db, "UPDATE runs SET status=$status WHERE id=$id",
                        ("$status", operationState is "Executing" or "Quarantined" ? "UnknownOutcome" : "Cancelled"), ("$id", id));
                    Event(db, id, "CancellationRestored", "重启优先恢复取消意图，不重新调度");
                }
                else if (operationState == "Executing")
                {
                    Exec(db, "UPDATE runs SET status='UnknownOutcome',error='host_interrupted_during_tool' WHERE id=$id", ("$id", id));
                    Event(db, id, "OutcomeUnknown", "不自动重放工具，等待核对");
                }
                else if (status == "Queued" || Scalar(db, "SELECT decision FROM runs WHERE id=$id", ("$id", id)) is not null)
                {
                    Exec(db, "UPDATE runs SET status=$status WHERE id=$id", ("$status", status == "Queued" ? "Queued" : "Approved"), ("$id", id));
                    Event(db, id, "RecoveryScheduled", "从持久化安全边界恢复");
                    resume.Add(id);
                }
                else
                {
                    Exec(db, "UPDATE runs SET status='Failed',error='interrupted_before_checkpoint' WHERE id=$id", ("$id", id));
                    Event(db, id, "RunFailed", "尚未保存 MAF checkpoint，不能宣称自动恢复成功");
                }
            }
            tx.Commit();
            return resume;
        }
    }

    public void Fail(string id, string error)
    {
        lock (gate)
        {
            using var db = Open(); using var tx = db.BeginTransaction();
            var operationState = Scalar(db, "SELECT state FROM tool_operations WHERE run_id=$id", ("$id", id));
            var cancelled = Scalar(db, "SELECT cancel_requested FROM runs WHERE id=$id", ("$id", id)) == "1";
            var status = operationState is "Executing" or "Quarantined" ? "UnknownOutcome"
                : cancelled ? "Cancelled" : operationState == "Succeeded" ? "FinalizationPending" : "Failed";
            Exec(db, "UPDATE runs SET status=$status,error=$error WHERE id=$id", ("$status", status), ("$error", error), ("$id", id));
            Event(db, id, status, error);
            tx.Commit();
        }
    }

    public List<EventView> Events(string id, long after)
    {
        lock (gate)
        {
            using var db = Open();
            using var cmd = Command(db, "SELECT seq,type,payload,created_utc FROM events WHERE run_id=$id AND seq>$after ORDER BY seq LIMIT 100", ("$id", id), ("$after", after));
            using var r = cmd.ExecuteReader();
            var result = new List<EventView>();
            while (r.Read())
            {
                using var doc = JsonDocument.Parse(r.GetString(2));
                result.Add(new EventView(r.GetInt64(0), id, r.GetString(1), doc.RootElement.Clone(), r.GetString(3)));
            }
            return result;
        }
    }

    private static string Normalize(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 1 ||
            !value.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(text.GetString()) || text.GetString()!.Length > 1000)
            throw new InvalidDataException("record_note requires exactly one non-empty text argument (max 1000).");
        return JsonSerializer.Serialize(new NoteArgs(text.GetString()!), ProbeJson.Default.NoteArgs);
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool ValidReceipt(string path, Receipt expected)
    {
        var expectedBytes = JsonSerializer.SerializeToUtf8Bytes(expected, ProbeJson.Default.Receipt);
        var info = new FileInfo(path);
        return info.Exists && info.Length == expectedBytes.Length && File.ReadAllBytes(path).AsSpan().SequenceEqual(expectedBytes);
    }
    private string StagingPath(string operationId) => Path.Combine(Root, "staging", operationId + ".json.tmp");
    private void Quarantine(string path, string operationId) =>
        File.Move(path, Path.Combine(Root, "quarantine", operationId + "-" + Guid.NewGuid().ToString("N") + ".json"), overwrite: false);
    private string ReceiptPath(string operationId) => Path.Combine(Root, "receipts", operationId + ".json");
    public void Dispose() => workspaceLock.Dispose();
}
