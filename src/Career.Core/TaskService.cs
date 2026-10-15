using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Career.Contracts;

namespace Career.Core;

public interface ITaskService
{
    WorkspaceResponse Workspace();
    TaskResponse Read(string taskId);
    TaskResponse Create(CreateTaskRequest request);
    TaskResponse Revise(string taskId, UpdateTaskRequest request);
    TaskResponse ConfirmPlan(string taskId, ConfirmPlanRequest request);
    TaskResponse Control(string taskId, string action, TaskCommandRequest request);
    TaskResponse DecideApproval(string approvalId, ApprovalDecisionRequest request);
    TaskResponse Accept(string taskId, AcceptanceRequest request);
}

public sealed class TaskService(ICareerStore store,IArtifactPublisher publisher,IConnectionService connections) : ITaskService
{
    public bool IntakePaused => store.ReadSetting("intakePaused")=="true";
    public WorkspaceResponse Workspace()
    {
        var docs=store.ReadTasks();
        var summaries=docs.Select(d=>new TaskSummaryResponse(d.Task.Id,d.Task.Title,d.Task.Status,d.Task.Revision,d.Task.Version,d.Task.Owner,d.Task.Project,d.Task.UpdatedAt,d.Task.Status=="Done"?"固定版本已通过验收。":d.Task.Status=="Cancelled"?"后续执行已停止，已有工作保留。":d.Task.Runs.LastOrDefault()?.Progress,d.Task.Approvals.Count(a=>a.Status=="Pending"),d.Task.Status=="InReview"?1:0)).ToArray();
        return new(store.WorkspaceId,store.WorkspaceName,IntakePaused,connections.Read(),summaries,docs.Length==0?store.CurrentSequence:docs[0].Task.SnapshotSeq,store.ReadSetting("executionReady")!="false",store.ReadSetting("executionError"));
    }
    public TaskResponse Read(string id)=>Decorate((store.ReadTask(id)??throw Missing()).Task,true);
    private TaskResponse Decorate(TaskResponse task,bool hydrate=false)
    {
        var integrityFailed=false;
        if(hydrate)task=task with {Artifacts=task.Artifacts.Select(a=>
        {
            try{return a with {Content=Encoding.UTF8.GetString(publisher.Read(a,task.Id))};}
            catch(Exception error) when(error is IOException or DomainException)
            {
                if(a.TaskRevision==task.Revision)integrityFailed=true;
                return a with {Content="当前无法安全读取这一版成果。原版本和验收记录保留，请检查文件或恢复来源。",Unverified=a.Unverified.Append("成果文件缺失或校验失败，当前不可验收或下载。").ToArray()};
            }
        }).ToArray()};
        var actions=DomainRules.Actions(task,IntakePaused).ToList();
        if(task.Status is "AwaitingPlan" or "Paused" or "Failed")actions.Add("edit-plan");
        if(task.Artifacts.Length>0&&!actions.Contains("view-artifact"))actions.Add("view-artifact");
        if(integrityFailed){actions.Remove("accept");actions.Remove("return");}
        return task with {AvailableActions=actions.ToArray()};
    }
    private static DomainException Missing()=>new("TASK_NOT_FOUND","任务不存在或已被移除。",404);
    private static TaskDocument Require(TaskDocument? d)=>d??throw Missing();
    private static string Payload<T>(T request,JsonTypeInfo<T> type)=>DomainRules.Hash(JsonSerializer.Serialize(request,type));
    private static void ValidateInput(string title,string goal,string[] criteria,string input)
    {
        DomainRules.Text(title,"任务名称",160);DomainRules.Text(goal,"任务目标",4000);DomainRules.Text(input,"输入资料",100000,true);
        if(criteria is null || criteria.Length is <1 or >20 || criteria.Any(c=>string.IsNullOrWhiteSpace(c)||c.Length>1000))throw new DomainException("INVALID_CRITERIA","请填写至少一项明确的验收要求，每项最多 1000 个字符。",400);
    }
    public TaskResponse Create(CreateTaskRequest r)
    {
        ValidateInput(r.Title,r.Goal,r.Criteria,r.InputText);
        if(r.Mode is not ("office" or "development" or "business"))throw new DomainException("INVALID_MODE","工作模式无效。",400);
        DomainRules.Text(r.Project,"项目名称",160,true);
        var id=DomainRules.Id("task");var now=DateTimeOffset.UtcNow;
        var plan=new PlanResponse(DomainRules.Id("plan"),1,DomainRules.PlanHash(r.Goal.Trim(),r.Criteria.Select(c=>c.Trim()).ToArray(),r.InputText,1),"Pending","仅使用这次输入的资料；仅在本任务成果区发布文件。发布内容另行确认，不包含外部发送、终端执行或仓库写入。");
        var task=new TaskResponse(id,r.Title.Trim(),r.Goal.Trim(),r.Criteria.Select(c=>c.Trim()).ToArray(),r.InputText,r.Mode,r.Project,"默认助理","AwaitingPlan",1,0,plan,[],[],[],[],[],now,now,0,[]);
        var response=store.Command("POST /api/tasks",r.RequestId,Payload(r,CareerJson.Default.CreateTaskRequest),id,_=>new TaskDocument(task,[],[],[]),"task.created","任务草稿已保存，等待确认计划。");
        return Decorate(response);
    }
    public TaskResponse Revise(string id,UpdateTaskRequest r)
    {
        ValidateInput(r.Title,r.Goal,r.Criteria,r.InputText);
        return Decorate(store.Command("PATCH /api/tasks/"+id,r.RequestId,Payload(r,CareerJson.Default.UpdateTaskRequest),id,current=>
        {
            var d=Require(current);DomainRules.Version(d.Task,r.ExpectedVersion);
            if(d.Task.Status is not ("AwaitingPlan" or "Paused" or "Failed"))throw new DomainException("TASK_MUST_PAUSE","请先暂停并等待执行器停止，再修改计划。",409,d.Task);
            if(d.Operations.Any(o=>o.State is "Executing" or "UnknownOutcome"))throw new DomainException("OPERATION_NEEDS_RECONCILIATION","未决操作尚待核对，暂不能改变执行范围。",409,d.Task);
            var revision=d.Task.Revision+1;
            var plan=new PlanResponse(DomainRules.Id("plan"),d.Task.Plan.Version+1,DomainRules.PlanHash(r.Goal.Trim(),r.Criteria.Select(c=>c.Trim()).ToArray(),r.InputText,revision),"Pending",d.Task.Plan.ScopeDescription);
            return d with {Task=d.Task with {Title=r.Title.Trim(),Goal=r.Goal.Trim(),Criteria=r.Criteria.Select(c=>c.Trim()).ToArray(),InputText=r.InputText,Revision=revision,Status="AwaitingPlan",Plan=plan,Runs=d.Task.Runs.Select(run=>run.Status is "Queued" or "Paused" or "WaitingApproval"?run with {Status="Cancelled",EndedAt=DateTimeOffset.UtcNow}:run).ToArray(),Approvals=d.Task.Approvals.Select(a=>a.Status=="Pending"?a with {Status="Expired",DecisionNote="任务计划已修订。"}:a).ToArray()}};
        },"task.revised","计划已修订，旧许可不沿用，请重新确认。"));
    }
    private void RequireModel()
    {
        if(store.ReadSetting("executionReady")=="false")throw new DomainException("EXECUTION_RECOVERY_REQUIRED","执行恢复尚未完成。草稿和已有工作保留，请先核对工作区恢复提示。",409);
        var c=connections.Read();
        if(c is null)throw new DomainException("MODEL_NOT_CONFIGURED","任务已保留。先在设置中连接模型，再回到计划确认。",409);
        if(c.TestState!="verified")throw new DomainException("MODEL_NOT_VERIFIED","任务已保留。请先完成模型连接与工具能力测试。",409);
    }
    private TaskDocument AddRun(TaskDocument d,string progress)
    {
        var now=DateTimeOffset.UtcNow;var runId=DomainRules.Id("run");
        var run=new RunResponse(runId,d.Task.Revision,"Queued",null,0,progress,null,null,0,0,0,"not-started",now,null);
        var checkpoint=new RunCheckpoint(runId,null,null,null,DomainRules.Id("operation"),null,"maf-1.24.0/career-1",connections.CurrentHash!);
        return d with {Task=d.Task with {Status="Queued",Runs=d.Task.Runs.Append(run).ToArray()},Checkpoints=d.Checkpoints.Append(checkpoint).ToArray()};
    }
    public TaskResponse ConfirmPlan(string id,ConfirmPlanRequest r)
    {
        return Decorate(store.Command("POST /api/tasks/"+id+"/confirm-plan",r.RequestId,Payload(r,CareerJson.Default.ConfirmPlanRequest),id,current=>
        {
            var d=Require(current);DomainRules.Version(d.Task,r.ExpectedVersion);
            if(d.Task.Status!="AwaitingPlan" || d.Task.Plan.Status!="Pending" || d.Task.Revision!=r.TaskRevision || d.Task.Plan.ProposalHash!=r.ProposalHash)throw new DomainException("PLAN_CHANGED","这份计划已改变或已经处理，请检查最新计划。",409,d.Task);
            RequireModel();
            return AddRun(d with {Task=d.Task with {Plan=d.Task.Plan with {Status="Confirmed"}}},IntakePaused?"计划已确认，等待恢复派发。":"计划已确认，等待执行槽。");
        },"plan.confirmed",IntakePaused?"计划已确认，等待恢复派发。":"计划已确认，准备开始执行。"));
    }
    public TaskResponse Control(string id,string action,TaskCommandRequest r)
    {
        return Decorate(store.Command("POST /api/tasks/"+id+"/"+action,r.RequestId,Payload(r,CareerJson.Default.TaskCommandRequest),id,current=>
        {
            var d=Require(current);DomainRules.Version(d.Task,r.ExpectedVersion);var run=d.Task.Runs.LastOrDefault();
            if(action=="cancel")
            {
                if(d.Task.Status is "Done" or "Cancelled")throw new DomainException("TASK_ALREADY_FINISHED","任务已结束，不能再次停止。",409,d.Task);
                var status=run?.Status is "Running" or "Stopping" or "Reconciling"?"Stopping":"Cancelled";
                return d with {Task=d.Task with {Status=status,Runs=d.Task.Runs.Select(x=>x.Id==run?.Id&&x.Status is not ("Succeeded" or "Failed" or "Cancelled")?x with {Status=status,Progress=status=="Stopping"?"正在请求停止，已发生的操作会保留。":"已停止，已有成果保留。",EndedAt=status=="Cancelled"?DateTimeOffset.UtcNow:x.EndedAt}:x).ToArray(),Approvals=d.Task.Approvals.Select(a=>a.Status=="Pending"?a with {Status="Cancelled",DecisionNote="任务已停止。"}:a).ToArray()},Checkpoints=d.Checkpoints.Select(c=>c.RunId==run?.Id?c with {ControlIntent="cancel"}:c).ToArray()};
            }
            if(run is null)throw new DomainException("RUN_NOT_FOUND","任务还没有已确认的执行，请先确认计划。",409,d.Task);
            if(action=="pause")
            {
                if(d.Task.Status is not ("Running" or "WaitingApproval" or "Queued"))throw new DomainException("TASK_NOT_RUNNING","当前任务不在可暂停状态。",409,d.Task);
                var status=d.Task.Status=="Running"?"Stopping":"Paused";
                return d with {Task=d.Task with {Status=status,Runs=d.Task.Runs.Select(x=>x.Id==run!.Id?x with {Status=status,Progress=status=="Stopping"?"等待执行器确认停止。":"任务已暂停，输入和待处理许可保留。"}:x).ToArray()},Checkpoints=d.Checkpoints.Select(c=>c.RunId==run!.Id?c with {ControlIntent="pause"}:c).ToArray()};
            }
            if(action is "resume" or "retry")
            {
                if(action=="resume"&&d.Task.Status!="Paused" || action=="retry"&&d.Task.Status is not ("Failed" or "Blocked"))throw new DomainException("INVALID_RESUME_STATE","先完成计划确认，或检查当前执行状态。",409,d.Task);
                if(d.Operations.Any(o=>o.State is "Executing" or "UnknownOutcome"))throw new DomainException("OPERATION_NEEDS_RECONCILIATION","有操作结果待核对，不能直接重试。",409,d.Task);
                RequireModel();
                var pending=d.Task.Approvals.LastOrDefault(a=>a.RunId==run!.Id&&a.Status=="Pending");
                if(pending is not null)return d with {Task=d.Task with {Status="WaitingApproval",Runs=d.Task.Runs.Select(x=>x.Id==run!.Id?x with {Status="WaitingApproval",Progress="等待你处理固定内容的工具许可。"}:x).ToArray()}};
                var checkpoint=d.Checkpoints.Last(c=>c.RunId==run!.Id);
                if(checkpoint.ConnectionHash!=connections.CurrentHash)
                {
                    if(d.Operations.Any(o=>o.RunId==run.Id&&o.State=="Succeeded"))throw new DomainException("CONNECTION_CHANGED_WITH_EFFECT","连接已改变且已有完成操作。先查看已有成果，不直接改变恢复配置。",409,d.Task);
                    d=d with {Task=d.Task with {Runs=d.Task.Runs.Select(x=>x.Id==run.Id?x with {Status="Cancelled",EndedAt=DateTimeOffset.UtcNow}:x).ToArray()}};
                    return AddRun(d,"使用已测试的新连接开始新一轮执行。");
                }
                return d with {Task=d.Task with {Status="Queued",Runs=d.Task.Runs.Select(x=>x.Id==run!.Id?x with {Status="Queued",FailureCode=null,FailureMessage=null,EndedAt=null,Progress=IntakePaused?"等待恢复派发。":"从已保存内容继续，已完成工具不会重复执行。"}:x).ToArray()},Checkpoints=d.Checkpoints.Select(c=>c.RunId==run.Id?c with {ControlIntent=null}:c).ToArray()};
            }
            throw new DomainException("UNKNOWN_COMMAND","不支持这个任务操作。",400);
        },"task."+action,action switch {"pause"=>"已接纳暂停请求。","cancel"=>"已接纳停止请求，已有工作保留。",_=>"已接纳继续执行请求。"}));
    }
    public TaskResponse DecideApproval(string approvalId,ApprovalDecisionRequest r)
    {
        var initial=store.ReadTasks().FirstOrDefault(d=>d.Task.Approvals.Any(a=>a.Id==approvalId))??throw new DomainException("APPROVAL_NOT_FOUND","许可记录不存在。",404);
        if(r.Decision is not ("approve" or "reject"))throw new DomainException("INVALID_DECISION","许可决定无效。",400);
        DomainRules.Text(r.Note,"决定说明",2000,true);
        return Decorate(store.Command("POST /api/approvals/"+approvalId+"/decision",r.RequestId,Payload(r,CareerJson.Default.ApprovalDecisionRequest),initial.Task.Id,current=>
        {
            var d=Require(current);DomainRules.Version(d.Task,r.ExpectedVersion);var a=d.Task.Approvals.First(x=>x.Id==approvalId);var run=d.Task.Runs.Last();
            if(d.Task.Status!="WaitingApproval" || a.RunId!=run.Id || a.Status!="Pending" || a.PayloadHash!=r.PayloadHash || a.ExpiresAt<=DateTimeOffset.UtcNow)throw new DomainException("APPROVAL_EXPIRED_OR_CHANGED","许可已改变、处理或过期，请检查当前待处理内容。",409,d.Task);
            var operation=d.Operations.First(o=>o.Id==a.OperationId);
            if(operation.PayloadHash!=a.PayloadHash)throw new DomainException("APPROVAL_PAYLOAD_MISMATCH","许可正文与操作不一致。",409,d.Task);
            var approved=r.Decision=="approve";
            if(approved)RequireModel();
            var checkpoint=d.Checkpoints.Last(c=>c.RunId==run.Id);
            checkpoint=checkpoint with {Approved=approved,OperationId=approved?operation.Id:DomainRules.Id("operation")};
            return d with {Task=d.Task with {Status="Queued",Approvals=d.Task.Approvals.Select(x=>x.Id==a.Id?x with {Status=approved?"Approved":"Rejected",DecisionNote=r.Note}:x).ToArray(),Runs=d.Task.Runs.Select(x=>x.Id==run.Id?x with {Status="Queued",Progress=approved?"固定内容已允许，准备发布。":"当前发布已拒绝，保留工作并交回执行器调整。"}:x).ToArray()},Operations=d.Operations.Select(x=>x.Id==operation.Id?x with {State=approved?"Approved":"Rejected"}:x).ToArray(),Checkpoints=d.Checkpoints.Select(x=>x.RunId==run.Id?checkpoint:x).ToArray()};
        },"approval."+r.Decision,r.Decision=="approve"?"已允许这一次固定内容的成果发布。":"已拒绝当前发布，既有工作保留。"));
    }
    public TaskResponse Accept(string id,AcceptanceRequest r)
    {
        if(r.Decision is not ("accept" or "return"))throw new DomainException("INVALID_ACCEPTANCE","验收决定无效。",400);
        DomainRules.Text(r.Note,"修改意见",4000,r.Decision=="accept");
        return Decorate(store.Command("POST /api/tasks/"+id+"/acceptances",r.RequestId,Payload(r,CareerJson.Default.AcceptanceRequest),id,current=>
        {
            var d=Require(current);DomainRules.Version(d.Task,r.ExpectedVersion);
            var artifact=d.Task.Artifacts.LastOrDefault(a=>a.TaskRevision==d.Task.Revision);
            if(d.Task.Status!="InReview" || d.Task.Revision!=r.TaskRevision || artifact is null || artifact.Id!=r.ArtifactId || artifact.Version!=r.ArtifactVersion || artifact.ContentHash!=r.ContentHash)
                throw new DomainException("ARTIFACT_VERSION_CHANGED","成果已改变或当前不在待验收阶段，请检查最新固定版本。",409,d.Task);
            publisher.Read(artifact,d.Task.Id);
            var acceptance=new AcceptanceResponse(DomainRules.Id("acceptance"),r.TaskRevision,r.ArtifactId,r.ArtifactVersion,r.ContentHash,r.Decision,r.Note,DateTimeOffset.UtcNow);
            d=d with {Task=d.Task with {Acceptances=d.Task.Acceptances.Append(acceptance).ToArray()}};
            if(r.Decision=="accept")return d with {Task=d.Task with {Status="Done"}};
            RequireModel();
            var goal=d.Task.Goal+"\n\n本轮修改意见："+r.Note;
            var revision=d.Task.Revision+1;
            return AddRun(d with {Task=d.Task with {Revision=revision,Goal=goal,Plan=d.Task.Plan with {Version=d.Task.Plan.Version+1,ProposalHash=DomainRules.PlanHash(goal,d.Task.Criteria,d.Task.InputText,revision),Status="Confirmed"}}},"根据退回意见修订；旧成果与验收决定保留，新版需要重新验收。");
        },"artifact."+r.Decision,r.Decision=="accept"?"固定版本已通过验收，任务完成。":"成果已退回，新一轮修订已排队，旧版验收不沿用。"));
    }

    public TaskDocument? StartNext()
    {
        if(IntakePaused || store.ReadSetting("executionReady")=="false")return null;
        var candidate=store.ReadTasks().Where(d=>d.Task.Status=="Queued").OrderBy(d=>d.Task.UpdatedAt).FirstOrDefault();
        if(candidate is null)return null;
        var claimed=store.Update(candidate.Task.Id,d=>
        {
            if(d.Task.Status!="Queued")throw new DomainException("CLAIM_CHANGED","任务已改变，不能领取。");
            var run=d.Task.Runs.Last();var checkpoint=d.Checkpoints.Last(c=>c.RunId==run.Id);
            if(run.Status!="Queued")throw new DomainException("CLAIM_NOT_COMPATIBLE","执行状态已改变，请检查后继续。",409,d.Task);
            if(checkpoint.ConnectionHash!=connections.CurrentHash)
                return d with {Task=d.Task with {Status="Paused",Runs=d.Task.Runs.Select(x=>x.Id==run.Id?x with {Status="Paused",Progress="模型连接已改变。任务已保留，请检查已测试的新连接后继续。"}:x).ToArray()}};
            var next=run with {Status="Running",AttemptId=DomainRules.Id("attempt"),AttemptNumber=run.AttemptNumber+1,Progress="执行器正在读取已确认的任务和输入。"};
            return d with {Task=d.Task with {Status="Running",Runs=d.Task.Runs.Select(x=>x.Id==run.Id?next:x).ToArray()}};
        },"run.dispatch.checked","已核对排队任务与当前连接；兼容的任务开始执行，连接改变的任务保留为暂停。");
        return claimed.Task.Status=="Running"?claimed:null;
    }
    private void ValidateIdentity(TaskDocument d,string runId,string attemptId,string epoch,long fence)
    {
        var run=d.Task.Runs.LastOrDefault();
        if(epoch!=store.HostEpoch || run is null || run.Id!=runId || run.AttemptId!=attemptId || run.AttemptNumber!=fence || d.Task.Status!="Running" || run.Status!="Running")throw new DomainException("STALE_WORKER","执行身份已经过期，拒绝旧执行器继续操作。",409,d.Task);
    }
    public ModelCallResponse Reserve(string taskId,ModelCallRequest request,int maxCalls)
    {
        var result=new ModelCallResponse(request.CallId,false,0,"调用限制已达到。");
        store.Update(taskId,d=>
        {
            ValidateIdentity(d,request.RunId,request.AttemptId,request.HostEpoch,request.FencingToken);
            if(request.ProtocolVersion!=Protocol.Version || request.MaxOutputTokens is <1 or >16000)throw new DomainException("INVALID_MODEL_RESERVATION","模型调用许可无效。");
            if(d.Usage.Any(x=>x.CallId==request.CallId))throw new DomainException("MODEL_CALL_ALREADY_RESERVED","此调用已经接纳，请查询原调用结果，不能再次派发。");
            var count=d.Usage.Count(u=>u.RunId==request.RunId);
            if(count>=maxCalls)return d;
            var usage=new UsageEntry(request.CallId,request.RunId,request.AttemptId,"reserved",0,0,request.MaxOutputTokens,DateTimeOffset.UtcNow);
            result=new(request.CallId,true,maxCalls-count-1,"已允许一次模型调用。");
            return d with {Usage=d.Usage.Append(usage).ToArray(),Task=d.Task with {Runs=d.Task.Runs.Select(run=>run.Id==request.RunId?run with {ModelCalls=count+1,UsageState="pending",Progress="模型正在分析输入并形成下一步。"}:run).ToArray()}};
        },"model.reserved","已接纳一次有上限的模型调用。");
        return result;
    }
    public ModelCallResponse Settle(string taskId,ModelCallRequest request)
    {
        store.Update(taskId,d=>
        {
            var entry=d.Usage.FirstOrDefault(x=>x.CallId==request.CallId&&x.RunId==request.RunId&&x.AttemptId==request.AttemptId)??throw new DomainException("MODEL_CALL_NOT_FOUND","未找到这次模型调用许可。");
            if(request.InputTokens is <0 or >1000000000 || request.OutputTokens is <0 or >1000000000)throw new DomainException("INVALID_USAGE","模型用量无效。");
            var known=request.UsageState is "known" or "confirmed" && request.InputTokens.HasValue && request.OutputTokens.HasValue;
            var next=entry with {State=known?"confirmed":"unknown",InputTokens=request.InputTokens??0,OutputTokens=request.OutputTokens??0};
            if(entry.State=="confirmed" && next!=entry)throw new DomainException("USAGE_ALREADY_CONFIRMED","这次调用用量已经确认，不能被重复或不同结算覆盖。");
            var ledger=d.Usage.Select(u=>u.CallId==entry.CallId?next:u).ToArray();var entries=ledger.Where(u=>u.RunId==request.RunId).ToArray();
            return d with {Usage=ledger,Task=d.Task with {Runs=d.Task.Runs.Select(run=>run.Id==request.RunId?run with {InputTokens=entries.Sum(x=>x.InputTokens),OutputTokens=entries.Sum(x=>x.OutputTokens),UsageState=entries.All(x=>x.State=="confirmed")?"confirmed-price-unknown":"pending-or-unknown"}:run).ToArray()}};
        },"model.settled","模型调用用量已记入原账项；未配置价格时费用保持未知。");
        return new(request.CallId,true,0,"用量已记录。");
    }
    public void AwaitApproval(string taskId,WorkerResponse response)
    {
        DomainRules.FileName(response.FileName!);DomainRules.Text(response.Content,"待发布正文",800000);
        if(response.Session is null || response.Approval is null || response.ToolName!="publish_document")throw new DomainException("INVALID_WORKER_APPROVAL","执行器未提供可恢复的固定内容许可。");
        store.Update(taskId,d=>
        {
            ValidateIdentity(d,response.RunId,response.AttemptId,response.HostEpoch,response.FencingToken);
            var checkpoint=d.Checkpoints.Last(c=>c.RunId==response.RunId);var hash=DomainRules.PublicationHash(response.FileName!,response.Content!);
            var old=d.Operations.FirstOrDefault(o=>o.Id==checkpoint.OperationId);
            if(old?.State=="Succeeded")throw new DomainException("OPERATION_ALREADY_COMPLETED","这个发布操作已经完成，不能将原回执重置成新的待批准操作。请先检查已有成果。");
            if(old is not null&&old.PayloadHash!=hash)throw new DomainException("OPERATION_PAYLOAD_CONFLICT","同一操作标识对应了不同内容，已停止派发。");
            var operation=new OperationDocument(checkpoint.OperationId,response.RunId,response.RunId+"/publish/"+checkpoint.OperationId,hash,response.FileName!,response.Content!,"Proposed",null,null);
            var approval=new ApprovalResponse(DomainRules.Id("approval"),response.RunId,operation.Id,1,"publish_document",hash,"Pending","本任务成果区 / "+response.FileName,response.Content!,"发布前固定文件名和正文；此许可不等于成果验收。",DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddMinutes(30),null);
            checkpoint=checkpoint with {Session=response.Session,Approval=response.Approval,Approved=null};
            return d with {Task=d.Task with {Status="WaitingApproval",Approvals=d.Task.Approvals.Append(approval).ToArray(),Runs=d.Task.Runs.Select(run=>run.Id==response.RunId?run with {Status="WaitingApproval",Progress="草稿已形成，等待你允许这一次固定内容的发布。"}:run).ToArray()},Checkpoints=d.Checkpoints.Select(c=>c.RunId==response.RunId?checkpoint:c).ToArray(),Operations=old is null?d.Operations.Append(operation).ToArray():d.Operations.Select(o=>o.Id==operation.Id?operation:o).ToArray()};
        },"approval.requested","有一次成果发布需要你确认，当前尚未创建成果文件。");
    }
    public WorkerToolResponse Publish(string taskId,WorkerToolRequest request)
    {
        var start=store.Update(taskId,d=>
        {
            ValidateIdentity(d,request.RunId,request.AttemptId,request.HostEpoch,request.FencingToken);
            if(request.ProtocolVersion!=Protocol.Version)throw new DomainException("WORKER_PROTOCOL_MISMATCH","执行器协议不兼容。");
            var operation=d.Operations.FirstOrDefault(o=>o.Id==request.OperationId)??throw new DomainException("OPERATION_NOT_AUTHORIZED","执行器提出了没有登记的发布操作。");
            var approval=d.Task.Approvals.LastOrDefault(a=>a.OperationId==operation.Id&&a.Status=="Approved");
            if(DomainRules.PublicationHash(request.FileName,request.Content)!=operation.PayloadHash)throw new DomainException("OPERATION_PAYLOAD_MISMATCH","操作内容已经改变，旧许可与回执不能用于新内容。");
            if(operation.State=="Succeeded")return d;
            if(operation.State!="Approved" || approval is null || approval.ExpiresAt<=DateTimeOffset.UtcNow || DomainRules.PublicationHash(request.FileName,request.Content)!=operation.PayloadHash)throw new DomainException("OPERATION_NOT_AUTHORIZED","许可已改变或过期，不能发布当前内容。");
            return d with {Operations=d.Operations.Select(o=>o.Id==operation.Id?o with {State="Executing"}:o).ToArray()};
        },"operation.dispatched","固定发布意图已持久接纳，开始校验并发布成果文件。");
        var op=start.Operations.First(o=>o.Id==request.OperationId);
        if(op.State=="Succeeded")return new(op.Id,op.ArtifactId!,op.ContentHash!,"Succeeded","原发布已经完成，返回原回执，没有再次写入。");
        var version=start.Task.Artifacts.Length+1;
        var artifact=publisher.Publish(taskId,request.RunId,start.Task.Revision,version,op,["本任务固定输入资料（内容 hash："+DomainRules.Hash(start.Task.InputText)+"）"],["正文完整性校验与固定版本发布"],["内容事实与结论需要人工复核","未执行外部发送或平台发布"]);
        store.Update(taskId,d=>
        {
            var current=d.Operations.First(o=>o.Id==op.Id);
            if(current.State=="Succeeded")return d;
            var checkpoint=d.Checkpoints.Last(c=>c.RunId==request.RunId) with {PublishedArtifactId=artifact.Id};
            return d with {Task=d.Task with {Artifacts=d.Task.Artifacts.Append(artifact with {Content=""}).ToArray()},Operations=d.Operations.Select(o=>o.Id==op.Id?o with {State="Succeeded",ArtifactId=artifact.Id,ContentHash=artifact.ContentHash}:o).ToArray(),Checkpoints=d.Checkpoints.Select(c=>c.RunId==request.RunId?checkpoint:c).ToArray()};
        },"artifact.published","成果文件已完整发布并记录回执，仍需独立验收。");
        return new(op.Id,artifact.Id,artifact.ContentHash,"Succeeded","成果已发布；任务尚未验收。");
    }
    public void Complete(string taskId,WorkerResponse response)
    {
        store.Update(taskId,d=>
        {
            ValidateIdentity(d,response.RunId,response.AttemptId,response.HostEpoch,response.FencingToken);
            var run=d.Task.Runs.Last();var checkpoint=d.Checkpoints.Last(c=>c.RunId==run.Id);
            if(response.Status=="ApprovalRejected")
                return d with {Task=d.Task with {Status="Paused",Runs=d.Task.Runs.Select(x=>x.Id==run.Id?x with {Status="Paused",Progress="本次发布已拒绝。工作保留，可调整计划或继续形成新提案。"}:x).ToArray()},Checkpoints=d.Checkpoints.Select(c=>c.RunId==run.Id?c with {Session=response.Session,Approval=null,Approved=null}:c).ToArray()};
            var artifact=d.Task.Artifacts.LastOrDefault(a=>a.RunId==run.Id&&a.TaskRevision==d.Task.Revision);
            if(response.Status!="Succeeded" || artifact is null || !d.Operations.Any(o=>o.RunId==run.Id&&o.State=="Succeeded"&&o.ArtifactId==artifact.Id))throw new DomainException("CANDIDATE_ARTIFACT_REQUIRED","执行器没有交付经过完整发布的候选成果，任务不能进入验收。");
            publisher.Read(artifact,taskId);
            return d with {Task=d.Task with {Status="InReview",Runs=d.Task.Runs.Select(x=>x.Id==run.Id?x with {Status="Succeeded",EndedAt=DateTimeOffset.UtcNow,Progress="候选成果已固定保存，等待你按要求验收。"}:x).ToArray()},Checkpoints=d.Checkpoints.Select(c=>c.RunId==run.Id?c with {Session=response.Session,Approval=null,Approved=null}:c).ToArray()};
        },"run.completed","执行已交付候选成果；通过固定版本验收后任务才算完成。");
    }
    public void StopAcknowledged(string taskId,bool cancelled)
    {
        store.Update(taskId,d=>ApplyStopped(d,cancelled),"run.stopped","执行器已确认退出，已保存最新暂停或停止决定。");
    }
    private static TaskDocument ApplyStopped(TaskDocument d,bool cancelled)
    {
        var runId=d.Task.Runs.Last().Id;
        cancelled|=d.Checkpoints.Last(c=>c.RunId==runId).ControlIntent=="cancel";
        return d with {Task=d.Task with {Status=cancelled?"Cancelled":"Paused",Runs=d.Task.Runs.Select(run=>run.Id==runId?run with {Status=cancelled?"Cancelled":"Paused",Progress=cancelled?"执行器已确认退出，已有成果和效果保留。":"执行器已确认退出，可以从保存的状态继续。",EndedAt=cancelled?DateTimeOffset.UtcNow:null}:run).ToArray()}};
    }
    public void Fail(string taskId,string code,string message,bool blocked=false)
    {
        store.Update(taskId,d=>
        {
            if(!blocked&&d.Task.Status=="Stopping")return ApplyStopped(d,false);
            var run=d.Task.Runs.LastOrDefault();
            return d with {Task=d.Task with {Status=blocked?"Blocked":"Failed",Runs=d.Task.Runs.Select(x=>x.Id==run?.Id?x with {Status=blocked?"Reconciling":"Failed",FailureCode=code,FailureMessage=message,Progress=message,EndedAt=DateTimeOffset.UtcNow}:x).ToArray()}};
        },"run.interrupted","执行状态已更新；最新停止决定与已有工作已保留。");
    }
    public void RecoverAfterRestart()
    {
        foreach(var document in store.ReadTasks())
        {
            var d=document;
            foreach(var operation in d.Operations.Where(o=>o.State=="Executing"))
            {
                try
                {
                    var artifact=publisher.Publish(d.Task.Id,operation.RunId,d.Task.Runs.First(r=>r.Id==operation.RunId).TaskRevision,d.Task.Artifacts.Length+1,operation,["本任务固定输入资料"],["重启后依据原发布意图校验并补登记"],["内容仍需人工验收"]);
                    d=store.Update(d.Task.Id,current=>current with {Task=current.Task with {Artifacts=current.Task.Artifacts.Any(a=>a.Id==artifact.Id)?current.Task.Artifacts:current.Task.Artifacts.Append(artifact with {Content=""}).ToArray()},Operations=current.Operations.Select(o=>o.Id==operation.Id?o with {State="Succeeded",ArtifactId=artifact.Id,ContentHash=artifact.ContentHash}:o).ToArray(),Checkpoints=current.Checkpoints.Select(c=>c.RunId==operation.RunId?c with {PublishedArtifactId=artifact.Id}:c).ToArray()},"operation.reconciled","已依据原发布意图核对成果，补登记原操作回执，没有重复创建版本。");
                }
                catch(Exception e) when(e is IOException or DomainException){Fail(d.Task.Id,"PUBLICATION_NEEDS_RECONCILIATION","成果发布尚待核对，已有文件与意图保留，暂不继续派发。",true);d=store.ReadTask(d.Task.Id)!;break;}
            }
            if(d.Task.Status is "Running" or "Stopping" or "Queued")
            {
                var stopIntent=d.Checkpoints.LastOrDefault()?.ControlIntent=="cancel";
                store.Update(d.Task.Id,current=>current with {Task=current.Task with {Status=stopIntent?"Cancelled":"Paused",Runs=current.Task.Runs.Select(run=>run.Id==current.Task.Runs.Last().Id?run with {Status=stopIntent?"Cancelled":"Paused",Progress=stopIntent?"重启后先恢复停止意图，已有工作保留。":"应用已重启，输入和操作记录保留，请检查后继续。"}:run).ToArray()},Usage=current.Usage.Select(u=>u.State=="reserved"?u with {State="unknown"}:u).ToArray()},"run.recovered",stopIntent?"重启后已恢复停止意图，没有重新派发。":"重启后已保留任务和已完成操作，等待你确认继续。");
            }
        }
    }
}
