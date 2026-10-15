using System.Text.Json;
using Career.Contracts;
using Career.Core;
using Career.Infrastructure;

namespace Career.Host;

public sealed class ExecutionCoordinator(TaskService tasks,ICareerStore store,IConnectionService connections,WorkerSupervisor workers,ILogger<ExecutionCoordinator> logger,IHostApplicationLifetime lifetime):BackgroundService
{
    private readonly SemaphoreSlim _slot=new(1,1);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var shutdown=CancellationTokenSource.CreateLinkedTokenSource(stoppingToken,lifetime.ApplicationStopping);
        stoppingToken=shutdown.Token;
        try
        {
            await workers.RecoverProcessesAsync(stoppingToken);tasks.RecoverAfterRestart();
            store.WriteSetting("executionReady","true");store.WriteSetting("executionError","");
        }
        catch(Exception error) when(error is IOException or DomainException)
        {
            store.WriteSetting("executionReady","false");store.WriteSetting("executionError","旧执行者或成果恢复尚待核对。工作区可读取和保存草稿，暂不派发新任务。");
            logger.LogError("Startup recovery could not establish safe execution: {ErrorType}",error.GetType().Name);
        }
        while(!stoppingToken.IsCancellationRequested)
        {
            await _slot.WaitAsync(stoppingToken);
            try
            {
                TaskDocument? document;
                try{document=tasks.StartNext();}
                catch(DomainException){document=null;}
                if(document is not null)await ExecuteTaskAsync(document,stoppingToken);
            }
            finally{_slot.Release();}
            await Task.Delay(200,stoppingToken);
        }
    }
    private async Task ExecuteTaskAsync(TaskDocument document,CancellationToken stoppingToken)
    {
        var task=document.Task;var run=task.Runs.Last();var checkpoint=document.Checkpoints.Last(c=>c.RunId==run.Id);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);deadline.CancelAfter(TimeSpan.FromMinutes(10));
        using var monitorStop=CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var monitor=MonitorControlAsync(task.Id,deadline,monitorStop.Token);
        try
        {
            var request=await connections.CreateWorkerRequestAsync(task,run,checkpoint,store.HostEpoch,run.AttemptNumber,false,deadline.Token);
            var response=await workers.RunAsync(request,(message,ct)=>HandleAsync(task.Id,request,message,ct),deadline.Token);
            if(stoppingToken.IsCancellationRequested)return;
            var current=store.ReadTask(task.Id)!;
            if(current.Task.Status=="Stopping"){tasks.StopAcknowledged(task.Id,current.Checkpoints.Last().ControlIntent=="cancel");return;}
            if(response.Status=="WaitingApproval")tasks.AwaitApproval(task.Id,response);
            else if(response.Status is "Succeeded" or "ApprovalRejected")tasks.Complete(task.Id,response);
            else if(!TryFinalizePublished(task.Id,run))tasks.Fail(task.Id,response.ErrorCode??"MODEL_EXECUTION_FAILED",FriendlyWorkerError(response.ErrorCode));
        }
        catch(OperationCanceledException)
        {
            var current=store.ReadTask(task.Id)!;
            if(current.Task.Status=="Stopping")tasks.StopAcknowledged(task.Id,current.Checkpoints.Last().ControlIntent=="cancel");
            else if(!stoppingToken.IsCancellationRequested&&!TryFinalizePublished(task.Id,run))tasks.Fail(task.Id,"EXECUTION_TIMEOUT","执行超过时限，已停止执行器。输入与已有成果保留，可检查后继续。");
        }
        catch(DomainException error)
        {
            if(stoppingToken.IsCancellationRequested&&error.Code!="WORKER_STOP_UNCONFIRMED")return;
            var current=store.ReadTask(task.Id)!;
            if(error.Code=="WORKER_STOP_UNCONFIRMED")
            {
                store.WriteSetting("executionReady","false");
                store.WriteSetting("executionError","旧执行器尚未确认停止，暂不派发新任务。请重新启动并核对恢复状态。");
            }
            if(current.Task.Status=="Stopping"&&error.Code!="WORKER_STOP_UNCONFIRMED")tasks.StopAcknowledged(task.Id,current.Checkpoints.Last().ControlIntent=="cancel");
            else if(error.Code=="WORKER_STOP_UNCONFIRMED"||!TryFinalizePublished(task.Id,run))tasks.Fail(task.Id,error.Code,error.Message,error.Code=="WORKER_STOP_UNCONFIRMED");
        }
        catch(Exception error) when(error is IOException or InvalidOperationException or JsonException)
        {
            if(stoppingToken.IsCancellationRequested)return;
            logger.LogError("Execution {RunId} stopped with {ErrorType}",run.Id,error.GetType().Name);
            var current=store.ReadTask(task.Id)!;
            if(current.Task.Status=="Stopping")tasks.StopAcknowledged(task.Id,current.Checkpoints.Last().ControlIntent=="cancel");
            else if(!TryFinalizePublished(task.Id,run))tasks.Fail(task.Id,"EXECUTION_INTERRUPTED","执行中断，输入、操作记录与已有成果保留。请检查连接或恢复后继续。");
        }
        finally
        {
            monitorStop.Cancel();try{await monitor;}catch(OperationCanceledException){}
        }
    }
    private bool TryFinalizePublished(string taskId,RunResponse attempt)
    {
        var current=store.ReadTask(taskId);
        if(current?.Task.Status!="Running"||current.Task.Runs.LastOrDefault()?.Id!=attempt.Id||
            !current.Operations.Any(operation=>operation.RunId==attempt.Id&&operation.State=="Succeeded"&&
                current.Task.Artifacts.Any(artifact=>artifact.Id==operation.ArtifactId&&artifact.RunId==attempt.Id&&artifact.TaskRevision==current.Task.Revision)))return false;
        try
        {
            // A missing worker finale cannot undo an already verified publication receipt.
            tasks.Complete(taskId,new WorkerResponse(Protocol.Version,attempt.Id,attempt.AttemptId!,store.HostEpoch,attempt.AttemptNumber,"Succeeded",null,null,null,null,null,null,"已按原发布回执核对候选成果。",0,0,0,"pending-or-unknown"));
            return true;
        }
        catch(Exception error) when(error is DomainException or IOException)
        {
            logger.LogWarning("Published result could not be finalized: {ErrorType}",error.GetType().Name);
            return false;
        }
    }
    private Task<WorkerEnvelope> HandleAsync(string taskId,WorkerRequest request,WorkerEnvelope message,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(message.Params is not {} parameters)throw new DomainException("INVALID_WORKER_REQUEST","执行器缺少命令内容。");
        if(message.Method=="tool.publish")
        {
            var command=parameters.Deserialize(CareerJson.Default.WorkerToolRequest)??throw new DomainException("INVALID_WORKER_REQUEST","发布命令无法读取。");
            ValidateRequest(request,command.RunId,command.AttemptId,command.HostEpoch,command.FencingToken);
            return Task.FromResult(new WorkerEnvelope("2.0",message.Id,null,null,null,tasks.Publish(taskId,command),null));
        }
        var model=parameters.Deserialize(CareerJson.Default.ModelCallRequest)??throw new DomainException("INVALID_WORKER_REQUEST","模型许可请求无法读取。");
        ValidateRequest(request,model.RunId,model.AttemptId,model.HostEpoch,model.FencingToken);
        var result=message.Method=="model.reserve"?tasks.Reserve(taskId,model,request.MaxModelCalls):tasks.Settle(taskId,model);
        return Task.FromResult(new WorkerEnvelope("2.0",message.Id,null,null,null,null,null,result));
    }
    private static void ValidateRequest(WorkerRequest expected,string run,string attempt,string epoch,long fence)
    {if(expected.RunId!=run||expected.AttemptId!=attempt||expected.HostEpoch!=epoch||expected.FencingToken!=fence)throw new DomainException("STALE_WORKER","执行器命令身份已经过期。");}
    private async Task MonitorControlAsync(string taskId,CancellationTokenSource execution,CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            if(store.ReadTask(taskId)?.Task.Status=="Stopping"){execution.Cancel();return;}
            await Task.Delay(100,ct);
        }
    }
    public async Task<ConnectionResponse> TestConnectionAsync(TestConnectionRequest command,CancellationToken cancellationToken)
    {
        DomainRules.RequestId(command.RequestId);
        var hash=connections.CurrentHash??throw new DomainException("MODEL_NOT_CONFIGURED","请先保存模型连接。",409);
        var payloadHash=DomainRules.Hash(hash+"/test");
        if(store.ReadCommand(command.RequestId) is {} receipt)
        {
            if(receipt.Route!="POST /api/connection/test"||receipt.PayloadHash!=payloadHash)throw new DomainException("IDEMPOTENCY_CONFLICT","这个请求标识已用于其他内容。");
            return JsonSerializer.Deserialize(receipt.ResponseJson,CareerJson.Default.ConnectionResponse)!;
        }
        if(store.ReadSetting("executionReady")=="false")throw new DomainException("EXECUTION_RECOVERY_REQUIRED","执行恢复尚未完成，暂不能启动模型测试。连接设置和已有工作保留。",409);
        var pendingKey="connection-test:"+command.RequestId;
        if(store.ReadSetting(pendingKey) is not null)throw new DomainException("CONNECTION_TEST_NEEDS_REVIEW","这次测试已经接纳但尚未完成核对，请先读取连接状态。重启后请使用新测试请求，不会重复发出原请求。",409);
        if(!await _slot.WaitAsync(0,cancellationToken))throw new DomainException("EXECUTOR_BUSY","有任务正在执行。先暂停任务，或等待执行槽空闲后测试连接。",409);
        ConnectionResponse Finish(bool succeeded,string message)
        {
            connections.RecordTest(hash,succeeded,message);
            var result=connections.Read()!;
            store.SaveCommand(new(command.RequestId,"POST /api/connection/test",payloadHash,"",0,JsonSerializer.Serialize(result,CareerJson.Default.ConnectionResponse),DateTimeOffset.UtcNow));
            store.WriteSetting(pendingKey,"completed:"+hash);
            return result;
        }
        try
        {
            store.WriteSetting(pendingKey,"pending:"+hash);
            var now=DateTimeOffset.UtcNow;
            var task=new TaskResponse("connection-test","连接能力测试","验证文本回复和 publish_document 工具许可；不得实际发布文件。",["文本回复与原生工具批准检查点均可读取"],"本地连接能力测试，不含用户任务资料。","office",null,"所有者","Running",1,1,new("test-plan",1,"","Confirmed","连接能力探测"),[],[],[],[],[],now,now,0,[]);
            var run=new RunResponse(DomainRules.Id("test-run"),1,"Running",DomainRules.Id("attempt"),1,"连接能力测试",null,null,0,0,0,"not-started",now,null);
            var checkpoint=new RunCheckpoint(run.Id,null,null,null,DomainRules.Id("operation"),null,"maf-1.24.0/career-1",hash);
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var request=await connections.CreateWorkerRequestAsync(task,run,checkpoint,store.HostEpoch,1,true,timeout.Token);
            var count=0;
            var response=await workers.RunAsync(request,(message,ct)=>
            {
                ct.ThrowIfCancellationRequested();
                if(message.Method=="tool.publish")throw new DomainException("TEST_PUBLICATION_DENIED","连接测试禁止实际发布成果。");
                var model=message.Params?.Deserialize(CareerJson.Default.ModelCallRequest)??throw new DomainException("INVALID_WORKER_REQUEST","连接测试许可无效。");
                ValidateRequest(request,model.RunId,model.AttemptId,model.HostEpoch,model.FencingToken);
                var callKey=pendingKey+":"+model.CallId;
                var allowed=true;
                if(message.Method=="model.reserve")
                {
                    if(store.ReadSetting(callKey) is not null)throw new DomainException("MODEL_CALL_ALREADY_RESERVED","这次测试调用已经接纳，不能重复派发。");
                    allowed=++count<=3;
                    if(allowed)store.WriteSetting(callKey,JsonSerializer.Serialize(model,CareerJson.Default.ModelCallRequest));
                }
                else
                {
                    if(store.ReadSetting(callKey) is null)throw new DomainException("MODEL_CALL_NOT_FOUND","测试调用没有对应许可。");
                    store.WriteSetting(callKey,JsonSerializer.Serialize(model,CareerJson.Default.ModelCallRequest));
                }
                return Task.FromResult(new WorkerEnvelope("2.0",message.Id,null,null,null,null,null,new(model.CallId,allowed,3-count,"仅连接能力测试许可。")));
            },timeout.Token);
            var succeeded=response.Status=="TestSucceeded";
            return Finish(succeeded,succeeded?"文本、工具许可与检查点测试通过。测试未发布文件。":FriendlyWorkerError(response.ErrorCode));
        }
        catch(OperationCanceledException) when(!cancellationToken.IsCancellationRequested){return Finish(false,"连接测试超时，设置仍保留，请检查服务地址后重试。");}
        catch(DomainException error) when(error.Code is not ("CONNECTION_CHANGED" or "IDEMPOTENCY_CONFLICT"))
        {
            if(error.Code=="WORKER_STOP_UNCONFIRMED"){store.WriteSetting("executionReady","false");store.WriteSetting("executionError","模型测试的旧执行器尚未确认停止，暂不派发新任务。请重新启动并核对恢复状态。");}
            return Finish(false,error.Code=="MODEL_CREDENTIAL_UNAVAILABLE"?error.Message:"连接测试中断，设置保留。请检查服务与本机执行器后重新测试。");
        }
        finally{_slot.Release();}
    }
    private static string FriendlyWorkerError(string? code)=>code switch
    {
        "model_call_limit_reached"=>"已达到本轮模型调用上限，输入与已完成操作保留。请检查调用限制后继续。",
        "endpoint_not_allowed" or "endpoint_not_secure"=>"模型地址没有通过连接范围检查，请在设置中检查 HTTPS 和本地地址许可。",
        "publication_not_completed"=>"模型没有交付可发布的完整成果。工作保留，可以补充要求后重试。",
        _=>"模型执行失败或连接中断。任务输入与已完成步骤保留，请检查连接后继续。"
    };
}
