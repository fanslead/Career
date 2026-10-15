using System.Text.Json;
using Career.Contracts;
using Career.Core;
using Microsoft.Data.Sqlite;

namespace Career.Infrastructure;

public sealed class CareerStore : ICareerStore, IDisposable
{
    private readonly object _gate = new();
    private readonly string _connectionString;
    private readonly WorkspaceLock _workspaceLock;
    public string WorkspaceId { get; }
    public string WorkspaceName { get; }
    public string HostEpoch { get; } = DomainRules.Id("epoch");

    public CareerStore(string directory, string name = "我的工作室", string? workspaceId = null, bool verifyProcessLock = true)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _workspaceLock = new WorkspaceLock(Path.Combine(directory, ".host.lock"), verifyProcessLock);
        var stateDirectory=Path.Combine(directory,"state"); Directory.CreateDirectory(stateDirectory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(stateDirectory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        var metadataPath=Path.Combine(directory,"workspace.json");
        WorkspaceMetadata? metadata=null;
        if(File.Exists(metadataPath))
        {
            metadata=JsonSerializer.Deserialize(File.ReadAllBytes(metadataPath),StorageJson.Default.WorkspaceMetadata)??throw new IOException("工作区配置为空。");
            if(metadata.SchemaVersion!=1 || !metadata.Id.StartsWith("workspace_",StringComparison.Ordinal) || string.IsNullOrWhiteSpace(metadata.Name))throw new IOException("工作区配置版本或标识无效，原文件已保留。");
            workspaceId=metadata.Id;name=metadata.Name;
        }
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(stateDirectory, "app.db"), Pooling = false, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        using var connection = Open();
        using var migration = connection.BeginTransaction();
        const string schemaSql = """
            CREATE TABLE IF NOT EXISTS schema_migrations(version INTEGER PRIMARY KEY, checksum TEXT NOT NULL, applied_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS tasks(id TEXT PRIMARY KEY, version INTEGER NOT NULL, revision INTEGER NOT NULL, status TEXT NOT NULL, updated_at TEXT NOT NULL, document TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS task_revisions(task_id TEXT NOT NULL REFERENCES tasks(id), revision INTEGER NOT NULL, proposal_hash TEXT NOT NULL, document TEXT NOT NULL, PRIMARY KEY(task_id,revision));
            CREATE TABLE IF NOT EXISTS runs(id TEXT PRIMARY KEY, task_id TEXT NOT NULL REFERENCES tasks(id), task_revision INTEGER NOT NULL, status TEXT NOT NULL, attempt_id TEXT, attempt_number INTEGER NOT NULL, document TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS one_active_run ON runs(task_id) WHERE status IN ('Queued','Running','WaitingApproval','Stopping','Paused','Reconciling');
            CREATE TABLE IF NOT EXISTS operations(id TEXT PRIMARY KEY, run_id TEXT NOT NULL REFERENCES runs(id), logical_key TEXT NOT NULL UNIQUE, state TEXT NOT NULL, payload_hash TEXT NOT NULL, document TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS approvals(id TEXT PRIMARY KEY, task_id TEXT NOT NULL REFERENCES tasks(id), operation_id TEXT NOT NULL REFERENCES operations(id), version INTEGER NOT NULL, status TEXT NOT NULL, payload_hash TEXT NOT NULL, document TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS artifacts(id TEXT NOT NULL, version INTEGER NOT NULL, task_id TEXT NOT NULL REFERENCES tasks(id), run_id TEXT NOT NULL REFERENCES runs(id), task_revision INTEGER NOT NULL, content_hash TEXT NOT NULL, document TEXT NOT NULL, PRIMARY KEY(id,version));
            CREATE TABLE IF NOT EXISTS acceptances(id TEXT PRIMARY KEY, task_id TEXT NOT NULL REFERENCES tasks(id), task_revision INTEGER NOT NULL, artifact_id TEXT NOT NULL, artifact_version INTEGER NOT NULL, content_hash TEXT NOT NULL, decision TEXT NOT NULL, document TEXT NOT NULL, FOREIGN KEY(artifact_id,artifact_version) REFERENCES artifacts(id,version));
            CREATE TABLE IF NOT EXISTS model_calls(call_id TEXT PRIMARY KEY, run_id TEXT NOT NULL REFERENCES runs(id), attempt_id TEXT NOT NULL, state TEXT NOT NULL, document TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS events(seq INTEGER PRIMARY KEY AUTOINCREMENT, event_id TEXT NOT NULL UNIQUE, schema_version INTEGER NOT NULL, occurred_at TEXT NOT NULL, task_id TEXT, run_id TEXT, type TEXT NOT NULL, message TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS commands(request_id TEXT PRIMARY KEY, route TEXT NOT NULL, payload_hash TEXT NOT NULL, task_id TEXT NOT NULL, version INTEGER NOT NULL, response TEXT NOT NULL, accepted_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY, access_hash TEXT NOT NULL UNIQUE, access_expires TEXT NOT NULL, refresh_hash TEXT NOT NULL UNIQUE, refresh_expires TEXT NOT NULL, revoked INTEGER NOT NULL, created_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS task_activity ON events(task_id,seq);
            """;
        var schemaChecksum=DomainRules.Hash(schemaSql);
        Execute(connection,migration,schemaSql);
        using (var version = Command(connection, migration, "SELECT version,checksum FROM schema_migrations ORDER BY version DESC LIMIT 1"))
        using (var reader = version.ExecuteReader())
        {
            if (reader.Read() && (reader.GetInt64(0) != 1 || reader.GetString(1) != schemaChecksum))
                throw new InvalidOperationException("工作区版本不兼容，请使用对应版本或从一致备份恢复。");
        }
        Execute(connection, migration, "INSERT OR IGNORE INTO schema_migrations VALUES(1,$checksum,$time)", ("$checksum", schemaChecksum), ("$time", DateTimeOffset.UtcNow.ToString("O")));
        Execute(connection, migration, "INSERT OR IGNORE INTO settings VALUES('workspaceId',$id)", ("$id", workspaceId ?? DomainRules.Id("workspace")));
        Execute(connection, migration, "INSERT OR IGNORE INTO settings VALUES('workspaceName',$name)", ("$name", name));
        Execute(connection, migration, "INSERT INTO settings VALUES('hostEpoch',$epoch) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$epoch", HostEpoch));
        migration.Commit();
        WorkspaceId = ReadSetting("workspaceId")!;
        WorkspaceName = metadata?.Name??ReadSetting("workspaceName")!;
        if(metadata is not null && metadata.Id!=WorkspaceId)throw new IOException("工作区配置与主数据库标识不一致，请核对恢复来源。");
        if(metadata is null)
        {
            var temp=Path.Combine(directory,".workspace-"+Guid.NewGuid().ToString("N")+".tmp");
            ArtifactPublisher.WriteDurable(temp,JsonSerializer.SerializeToUtf8Bytes(new WorkspaceMetadata(1,WorkspaceId,WorkspaceName,DateTimeOffset.UtcNow),StorageJson.Default.WorkspaceMetadata));
            File.Move(temp,metadataPath,false);
        }
        if(!OperatingSystem.IsWindows())File.SetUnixFileMode(Path.Combine(stateDirectory,"app.db"),UnixFileMode.UserRead|UnixFileMode.UserWrite);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        Execute(connection, null, "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;");
        return connection;
    }
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string, object?)[] parameters)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (key,value) in parameters) command.Parameters.AddWithValue(key,value ?? DBNull.Value);
        return command;
    }
    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string, object?)[] parameters)
    { using var command = Command(connection, transaction, sql, parameters); command.ExecuteNonQuery(); }
    private static long Sequence(SqliteConnection connection, SqliteTransaction? transaction)
    { using var command = Command(connection, transaction, "SELECT COALESCE(MAX(seq),0) FROM events"); return (long)command.ExecuteScalar()!; }
    public long CurrentSequence { get { lock(_gate) { using var connection = Open(); return Sequence(connection,null); } } }
    private static TaskDocument? ReadTask(SqliteConnection connection, SqliteTransaction? transaction, string taskId)
    {
        using var command = Command(connection,transaction,"SELECT document FROM tasks WHERE id=$id",("$id",taskId));
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize(json,StorageJson.Default.TaskDocument) : null;
    }
    public TaskDocument? ReadTask(string taskId)
    {
        lock(_gate) { using var connection=Open(); using var transaction=connection.BeginTransaction(); var document=ReadTask(connection,transaction,taskId); var seq=Sequence(connection,transaction); transaction.Commit(); return document is null ? null : document with {Task=document.Task with {SnapshotSeq=seq}}; }
    }
    public TaskDocument[] ReadTasks()
    {
        lock(_gate) { using var connection=Open(); using var transaction=connection.BeginTransaction(); using var command=Command(connection,transaction,"SELECT document FROM tasks ORDER BY updated_at DESC"); using var reader=command.ExecuteReader(); var documents=new List<TaskDocument>(); while(reader.Read()) documents.Add(JsonSerializer.Deserialize(reader.GetString(0),StorageJson.Default.TaskDocument)!); reader.Close(); var seq=Sequence(connection,transaction); transaction.Commit(); return documents.Select(d=>d with {Task=d.Task with {SnapshotSeq=seq}}).ToArray(); }
    }
    public TaskResponse Command(string route,string requestId,string payloadHash,string taskId,Func<TaskDocument?,TaskDocument> transition,string eventType,string message)
    {
        DomainRules.RequestId(requestId);
        lock(_gate)
        {
            using var connection=Open(); using var transaction=connection.BeginTransaction();
            using(var receipt=Command(connection,transaction,"SELECT route,payload_hash,response FROM commands WHERE request_id=$id",("$id",requestId)))
            using(var reader=receipt.ExecuteReader())
            {
                if(reader.Read())
                {
                    if(reader.GetString(0)!=route || reader.GetString(1)!=payloadHash) throw new DomainException("IDEMPOTENCY_CONFLICT","这个请求标识已用于其他内容，请核对原请求。",409);
                    return JsonSerializer.Deserialize(reader.GetString(2),CareerJson.Default.TaskResponse)!;
                }
            }
            var previous=ReadTask(connection,transaction,taskId);
            var next=CommitDocument(connection,transaction,previous,transition(previous),eventType,message);
            Execute(connection,transaction,"INSERT INTO commands VALUES($id,$route,$hash,$task,$version,$response,$time)",("$id",requestId),("$route",route),("$hash",payloadHash),("$task",next.Task.Id),("$version",next.Task.Version),("$response",JsonSerializer.Serialize(next.Task,CareerJson.Default.TaskResponse)),("$time",DateTimeOffset.UtcNow.ToString("O")));
            transaction.Commit(); return next.Task;
        }
    }
    public TaskDocument Update(string taskId,Func<TaskDocument,TaskDocument> transition,string eventType,string message)
    {
        lock(_gate) { using var connection=Open(); using var transaction=connection.BeginTransaction(); var previous=ReadTask(connection,transaction,taskId) ?? throw new DomainException("TASK_NOT_FOUND","任务不存在。",404); var next=CommitDocument(connection,transaction,previous,transition(previous),eventType,message); transaction.Commit(); return next; }
    }
    private static TaskDocument CommitDocument(SqliteConnection connection,SqliteTransaction transaction,TaskDocument? previous,TaskDocument next,string eventType,string message)
    {
        var now=DateTimeOffset.UtcNow; var eventId=DomainRules.Id("event");
        var revisions=next.Revisions??previous?.Revisions??[];
        var recorded=revisions.FirstOrDefault(r=>r.Revision==next.Task.Revision);
        if(recorded is not null && (recorded.Goal!=next.Task.Goal || recorded.Title!=next.Task.Title || recorded.InputText!=next.Task.InputText || !recorded.Criteria.SequenceEqual(next.Task.Criteria)))throw new DomainException("IMMUTABLE_TASK_REVISION","修改任务输入必须创建新修订，不能改变旧执行快照。");
        if(recorded is null)revisions=revisions.Append(new TaskRevisionDocument(next.Task.Revision,next.Task.Title,next.Task.Goal,next.Task.Criteria,next.Task.InputText,next.Task.Plan.ProposalHash,now)).ToArray();
        next=next with {Revisions=revisions};
        Execute(connection,transaction,"INSERT INTO events(event_id,schema_version,occurred_at,task_id,run_id,type,message) VALUES($event,1,$time,$task,$run,$type,$message)",("$event",eventId),("$time",now.ToString("O")),("$task",next.Task.Id),("$run",next.Task.Runs.LastOrDefault()?.Id),("$type",eventType),("$message",message));
        next=next with {Task=next.Task with {Version=(previous?.Task.Version??0)+1, UpdatedAt=now,SnapshotSeq=Sequence(connection,transaction),Activity=next.Task.Activity.Append(new ActivityResponse(eventId,eventType,message,now)).TakeLast(200).ToArray()}};
        if(previous?.Task.Status=="Done" && next.Task.Status=="Done" && previous.Task.Revision!=next.Task.Revision) throw new DomainException("INVALID_DONE_REVISION","已完成任务不能沿用旧版验收。");
        Execute(connection,transaction,"INSERT INTO tasks VALUES($id,$version,$revision,$status,$time,$doc) ON CONFLICT(id) DO UPDATE SET version=excluded.version,revision=excluded.revision,status=excluded.status,updated_at=excluded.updated_at,document=excluded.document",("$id",next.Task.Id),("$version",next.Task.Version),("$revision",next.Task.Revision),("$status",next.Task.Status),("$time",now.ToString("O")),("$doc",JsonSerializer.Serialize(next with {Task=next.Task with {Artifacts=next.Task.Artifacts.Select(a=>a with {Content=""}).ToArray()}},StorageJson.Default.TaskDocument)));
        foreach(var revision in next.Revisions!)
            Execute(connection,transaction,"INSERT OR IGNORE INTO task_revisions VALUES($task,$revision,$hash,$doc)",("$task",next.Task.Id),("$revision",revision.Revision),("$hash",revision.ProposalHash),("$doc",JsonSerializer.Serialize(revision,StorageJson.Default.TaskRevisionDocument)));
        foreach(var run in next.Task.Runs)
            Execute(connection,transaction,"INSERT INTO runs VALUES($id,$task,$revision,$status,$attempt,$number,$doc) ON CONFLICT(id) DO UPDATE SET status=excluded.status,attempt_id=excluded.attempt_id,attempt_number=excluded.attempt_number,document=excluded.document",("$id",run.Id),("$task",next.Task.Id),("$revision",run.TaskRevision),("$status",run.Status),("$attempt",run.AttemptId),("$number",run.AttemptNumber),("$doc",JsonSerializer.SerializeToElement(run,CareerJson.Default.RunResponse).GetRawText()));
        foreach(var operation in next.Operations)
            Execute(connection,transaction,"INSERT INTO operations VALUES($id,$run,$key,$state,$hash,$doc) ON CONFLICT(id) DO UPDATE SET state=excluded.state,document=excluded.document",("$id",operation.Id),("$run",operation.RunId),("$key",operation.LogicalKey),("$state",operation.State),("$hash",operation.PayloadHash),("$doc",JsonSerializer.SerializeToElement(operation,StorageJson.Default.OperationDocument).GetRawText()));
        foreach(var approval in next.Task.Approvals)
            Execute(connection,transaction,"INSERT INTO approvals VALUES($id,$task,$operation,$version,$status,$hash,$doc) ON CONFLICT(id) DO UPDATE SET version=excluded.version,status=excluded.status,document=excluded.document",("$id",approval.Id),("$task",next.Task.Id),("$operation",approval.OperationId),("$version",approval.Version),("$status",approval.Status),("$hash",approval.PayloadHash),("$doc",JsonSerializer.SerializeToElement(approval,CareerJson.Default.ApprovalResponse).GetRawText()));
        foreach(var artifact in next.Task.Artifacts)
            Execute(connection,transaction,"INSERT OR IGNORE INTO artifacts VALUES($id,$version,$task,$run,$revision,$hash,$doc)",("$id",artifact.Id),("$version",artifact.Version),("$task",next.Task.Id),("$run",artifact.RunId),("$revision",artifact.TaskRevision),("$hash",artifact.ContentHash),("$doc",JsonSerializer.SerializeToElement(artifact with {Content=""},CareerJson.Default.ArtifactResponse).GetRawText()));
        foreach(var acceptance in next.Task.Acceptances)
            Execute(connection,transaction,"INSERT OR IGNORE INTO acceptances VALUES($id,$task,$revision,$artifact,$version,$hash,$decision,$doc)",("$id",acceptance.Id),("$task",next.Task.Id),("$revision",acceptance.TaskRevision),("$artifact",acceptance.ArtifactId),("$version",acceptance.ArtifactVersion),("$hash",acceptance.ContentHash),("$decision",acceptance.Decision),("$doc",JsonSerializer.SerializeToElement(acceptance,CareerJson.Default.AcceptanceResponse).GetRawText()));
        foreach(var usage in next.Usage)
            Execute(connection,transaction,"INSERT INTO model_calls VALUES($id,$run,$attempt,$state,$doc) ON CONFLICT(call_id) DO UPDATE SET state=excluded.state,document=excluded.document",("$id",usage.CallId),("$run",usage.RunId),("$attempt",usage.AttemptId),("$state",usage.State),("$doc",JsonSerializer.SerializeToElement(usage,StorageJson.Default.UsageEntry).GetRawText()));
        if(next.Task.Status=="Done" && !next.Task.Acceptances.Any(a=>a.TaskRevision==next.Task.Revision && a.Decision=="accept" && next.Task.Artifacts.Any(f=>f.Id==a.ArtifactId && f.Version==a.ArtifactVersion && f.ContentHash==a.ContentHash))) throw new DomainException("ACCEPTANCE_REQUIRED","任务完成必须有当前版本的验收记录。");
        return next;
    }
    public EventResponse[] ReadEvents(long after,int limit=200)
    {
        lock(_gate) { using var connection=Open(); using var command=Command(connection,null,"SELECT seq,event_id,schema_version,occurred_at,task_id,run_id,type,message FROM events WHERE seq>$after ORDER BY seq LIMIT $limit",("$after",after),("$limit",Math.Clamp(limit,1,1000))); using var reader=command.ExecuteReader(); var events=new List<EventResponse>(); while(reader.Read()) events.Add(new(reader.GetInt64(0),reader.GetString(1),reader.GetInt32(2),DateTimeOffset.Parse(reader.GetString(3)),reader.IsDBNull(4)?null:reader.GetString(4),reader.IsDBNull(5)?null:reader.GetString(5),reader.GetString(6),reader.GetString(7))); return events.ToArray(); }
    }
    public StoredCommand? ReadCommand(string requestId)
    { lock(_gate) { using var connection=Open(); using var command=Command(connection,null,"SELECT request_id,route,payload_hash,task_id,version,response,accepted_at FROM commands WHERE request_id=$id",("$id",requestId)); using var r=command.ExecuteReader(); return r.Read()?new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetInt64(4),r.GetString(5),DateTimeOffset.Parse(r.GetString(6))):null; } }
    public void PublishEvent(string type,string message)
    { lock(_gate) { using var c=Open(); Execute(c,null,"INSERT INTO events(event_id,schema_version,occurred_at,task_id,run_id,type,message) VALUES($id,1,$time,NULL,NULL,$type,$message)",("$id",DomainRules.Id("event")),("$time",DateTimeOffset.UtcNow.ToString("O")),("$type",type),("$message",message)); } }
    public void SettingCommand(string route,string requestId,string payloadHash,string key,string value)
    {
        DomainRules.RequestId(requestId);
        lock(_gate)
        {
            using var c=Open();using var tx=c.BeginTransaction();
            using(var existing=Command(c,tx,"SELECT route,payload_hash FROM commands WHERE request_id=$id",("$id",requestId)))
            using(var r=existing.ExecuteReader())
            { if(r.Read()){if(r.GetString(0)!=route||r.GetString(1)!=payloadHash)throw new DomainException("IDEMPOTENCY_CONFLICT","这个请求标识已用于其他内容。");return;} }
            var now=DateTimeOffset.UtcNow.ToString("O");
            Execute(c,tx,"INSERT INTO settings VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value",("$key",key),("$value",value));
            Execute(c,tx,"INSERT INTO events(event_id,schema_version,occurred_at,task_id,run_id,type,message) VALUES($id,1,$time,NULL,NULL,'workspace.intake',$message)",("$id",DomainRules.Id("event")),("$time",now),("$message",value=="true"?"已暂停新任务派发，当前任务继续。":"已恢复新任务派发。"));
            Execute(c,tx,"INSERT INTO commands VALUES($id,$route,$hash,'',0,$response,$time)",("$id",requestId),("$route",route),("$hash",payloadHash),("$response",JsonSerializer.Serialize(value,StorageJson.Default.Options.GetTypeInfo(typeof(string)))),("$time",now));
            tx.Commit();
        }
    }
    public void SaveCommand(StoredCommand receipt)
    { lock(_gate) { using var c=Open(); Execute(c,null,"INSERT INTO commands VALUES($id,$route,$hash,$task,$version,$response,$time)",("$id",receipt.RequestId),("$route",receipt.Route),("$hash",receipt.PayloadHash),("$task",receipt.TaskId),("$version",receipt.Version),("$response",receipt.ResponseJson),("$time",receipt.AcceptedAt.ToString("O"))); } }
    public string? ReadSetting(string key)
    { lock(_gate) { using var connection=Open(); using var command=Command(connection,null,"SELECT value FROM settings WHERE key=$key",("$key",key)); return command.ExecuteScalar() as string; } }
    public void WriteSetting(string key,string value)
    { lock(_gate) { using var connection=Open(); Execute(connection,null,"INSERT INTO settings VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value",("$key",key),("$value",value)); } }
    public SessionDocument? FindSession(string hash,bool refresh)
    { lock(_gate) { using var c=Open(); using var command=Command(c,null,"SELECT id,access_hash,access_expires,refresh_hash,refresh_expires,revoked,created_at FROM sessions WHERE "+(refresh?"refresh_hash":"access_hash")+"=$hash",("$hash",hash)); using var r=command.ExecuteReader(); return r.Read()?new(r.GetString(0),r.GetString(1),DateTimeOffset.Parse(r.GetString(2)),r.GetString(3),DateTimeOffset.Parse(r.GetString(4)),r.GetBoolean(5),DateTimeOffset.Parse(r.GetString(6))):null; } }
    public void SaveSession(SessionDocument session)
    { lock(_gate) { using var c=Open(); SaveSession(c,null,session); } }
    private static void SaveSession(SqliteConnection c,SqliteTransaction? tx,SessionDocument s)
        => Execute(c,tx,"INSERT INTO sessions VALUES($id,$access,$expires,$refresh,$refreshExpires,$revoked,$time) ON CONFLICT(id) DO UPDATE SET access_hash=excluded.access_hash,access_expires=excluded.access_expires,refresh_hash=excluded.refresh_hash,refresh_expires=excluded.refresh_expires,revoked=excluded.revoked",("$id",s.Id),("$access",s.AccessHash),("$expires",s.AccessExpires.ToString("O")),("$refresh",s.RefreshHash),("$refreshExpires",s.RefreshExpires.ToString("O")),("$revoked",s.Revoked?1:0),("$time",s.CreatedAt.ToString("O")));
    public bool RotateSession(string oldHash,SessionDocument replacement)
    { lock(_gate) { using var c=Open(); using var tx=c.BeginTransaction(); using var command=Command(c,tx,"SELECT COUNT(*) FROM sessions WHERE id=$id AND refresh_hash=$hash AND revoked=0 AND refresh_expires>$now",("$id",replacement.Id),("$hash",oldHash),("$now",DateTimeOffset.UtcNow.ToString("O"))); if((long)command.ExecuteScalar()! !=1)return false; SaveSession(c,tx,replacement);tx.Commit();return true; } }
    public void RevokeSession(string sessionId)
    { lock(_gate) { using var c=Open(); Execute(c,null,"UPDATE sessions SET revoked=1 WHERE id=$id",("$id",sessionId)); } }
    public void ApplyRestoreBarrier()
    {
        lock(_gate)
        {
            using var c=Open();using var tx=c.BeginTransaction();
            Execute(c,tx,"UPDATE sessions SET revoked=1;");
            Execute(c,tx,"INSERT INTO settings VALUES('intakePaused','true') ON CONFLICT(key) DO UPDATE SET value='true';");
            Execute(c,tx,"INSERT INTO events(event_id,schema_version,occurred_at,task_id,run_id,type,message) VALUES($id,1,$time,NULL,NULL,'workspace.restored','工作区由一致备份恢复；旧会话已撤销，新派发保持暂停，请先核对已有任务和成果。')",("$id",DomainRules.Id("event")),("$time",DateTimeOffset.UtcNow.ToString("O")));
            tx.Commit();
        }
    }
    public void BackupDatabase(string targetPath)
    {
        lock(_gate)
        {
            if(File.Exists(targetPath))throw new IOException("数据库备份目标已经存在。");
            using var source=Open();
            using var target=new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=targetPath,Pooling=false,Mode=SqliteOpenMode.ReadWriteCreate}.ToString());
            target.Open();source.BackupDatabase(target);
            using var verify=target.CreateCommand();verify.CommandText="PRAGMA journal_mode=DELETE";
            if(verify.ExecuteScalar() is not string journal || journal!="delete")throw new IOException("数据库备份无法固化为独立文件。");
            verify.CommandText="PRAGMA integrity_check";
            if(verify.ExecuteScalar() is not string result || result!="ok")throw new IOException("数据库备份完整性验证失败。");
            if(!OperatingSystem.IsWindows())File.SetUnixFileMode(targetPath,UnixFileMode.UserRead|UnixFileMode.UserWrite);
        }
    }
    public void Dispose()=>_workspaceLock.Dispose();
}
