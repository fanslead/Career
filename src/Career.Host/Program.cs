using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using System.Text.Json;
using System.Text.Json.Serialization;
using Career.Contracts;
using Career.Core;
using Career.Host;
using Career.Infrastructure;
using Career.Infrastructure.Secrets;
using Career.Infrastructure.Backup;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;

if(args.Length==2&&args[0]==WorkspaceLock.ProbeArgument)return WorkspaceLock.Probe(args[1]);
string Option(string key,string fallback){var index=Array.IndexOf(args,key);return index>=0&&index+1<args.Length?args[index+1]:fallback;}
var workspaceDirectory=Path.GetFullPath(Option("--workspace",Environment.GetEnvironmentVariable("CAREER_WORKSPACE")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Career","workspace")));
var backupPath=Option("--backup","");var restorePath=Option("--restore","");
if(backupPath.Length>0&&restorePath.Length>0)throw new ArgumentException("备份与恢复应分开执行。");
if(restorePath.Length>0)
{
    WorkspaceBackup.Restore(restorePath,workspaceDirectory);
    Console.WriteLine("Career 备份已校验并恢复："+workspaceDirectory+"。首次启动将撤销旧会话并暂停新派发，请先核对任务、成果与模型凭据。");
    return 0;
}
var port=int.Parse(Option("--port",Environment.GetEnvironmentVariable("CAREER_PORT")??"5198"),CultureInfo.InvariantCulture);
if(port is <1024 or >65535)throw new ArgumentException("监听端口应在 1024–65535 之间。");
ArtifactPublisher.SafeDirectory(workspaceDirectory);
using var store=new CareerStore(workspaceDirectory);
var restoreMarker=Path.Combine(workspaceDirectory,"state","restore-pending");
if(File.Exists(restoreMarker))
{
    store.ApplyRestoreBarrier();
    File.Move(restoreMarker,Path.Combine(workspaceDirectory,"state","restore-applied-"+DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),false);
}
store.WriteSetting("executionReady","false");store.WriteSetting("executionError","正在校验工作区与恢复执行状态，请稍候。");
var secrets=new EncryptedSecretStore(workspaceDirectory,store.WorkspaceId);
var connections=new ConnectionService(workspaceDirectory,secrets,store);
var publisher=new ArtifactPublisher(workspaceDirectory);
var tasks=new TaskService(store,publisher,connections);
var owner=new OwnerSessions(store);
var defaultWorker=Path.Combine(AppContext.BaseDirectory,"worker",OperatingSystem.IsWindows()?"Career.AgentWorker.exe":"Career.AgentWorker");
var workerPath=Option("--worker",Environment.GetEnvironmentVariable("CAREER_WORKER_PATH")??defaultWorker);
var workerSupervisor=new WorkerSupervisor(workspaceDirectory,workerPath);
if(backupPath.Length>0)
{
    await workerSupervisor.RecoverProcessesAsync(CancellationToken.None);
    tasks.RecoverAfterRestart();
    WorkspaceBackup.Create(workspaceDirectory,backupPath,store.BackupDatabase);
    Console.WriteLine("Career 一致备份已保存："+Path.GetFullPath(backupPath)+"。系统主密钥不包含在备份中；异机恢复可能需要重新配置模型密钥。");
    return 0;
}
var webRoot=Option("--web",Path.Combine(AppContext.BaseDirectory,"wwwroot"));
var builder=WebApplication.CreateSlimBuilder(new WebApplicationOptions{Args=[],WebRootPath=webRoot,ContentRootPath=AppContext.BaseDirectory});
builder.WebHost.ConfigureKestrel(options=>{options.Listen(IPAddress.Loopback,port);options.Limits.MaxRequestBodySize=1024*1024;options.Limits.MaxRequestHeadersTotalSize=16384;});
builder.Services.ConfigureHttpJsonOptions(options=>{options.SerializerOptions.TypeInfoResolverChain.Insert(0,CareerJson.Default);options.SerializerOptions.PropertyNameCaseInsensitive=false;options.SerializerOptions.AllowDuplicateProperties=false;options.SerializerOptions.RespectNullableAnnotations=true;options.SerializerOptions.RespectRequiredConstructorParameters=true;});
builder.Services.AddSingleton<ICareerStore>(store);builder.Services.AddSingleton<ISecretStore>(secrets);builder.Services.AddSingleton<IConnectionService>(connections);builder.Services.AddSingleton<IArtifactPublisher>(publisher);builder.Services.AddSingleton(tasks);builder.Services.AddSingleton<ITaskService>(tasks);builder.Services.AddSingleton(owner);builder.Services.AddSingleton(workerSupervisor);
builder.Services.AddSingleton<ExecutionCoordinator>();builder.Services.AddHostedService(service=>service.GetRequiredService<ExecutionCoordinator>());
builder.Services.AddRateLimiter(options=>
{
    options.RejectionStatusCode=429;
    options.AddPolicy("commands",context=>RateLimitPartition.GetConcurrencyLimiter("owner-commands",_=>new ConcurrencyLimiterOptions{PermitLimit=16,QueueLimit=32,QueueProcessingOrder=QueueProcessingOrder.OldestFirst}));
    options.AddPolicy("auth",context=>RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString()??"local",_=>new FixedWindowRateLimiterOptions{PermitLimit=20,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));
});
var app=builder.Build();
app.Use(async(context,next)=>
{
    context.Response.Headers["X-Content-Type-Options"]="nosniff";context.Response.Headers["Referrer-Policy"]="no-referrer";
    context.Response.Headers["Content-Security-Policy"]="default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
    try
    {
        var host=context.Request.Host;
        if(host.Host is not ("127.0.0.1" or "localhost" or "::1" or "[::1]") || host.Port!=port)throw new DomainException("HOST_NOT_ALLOWED","请求不是当前本机工作区地址。",400);
        var origin=context.Request.Headers.Origin.ToString();
        if(origin.Length>0 && origin!=$"http://127.0.0.1:{port}" && origin!=$"http://localhost:{port}")throw new DomainException("ORIGIN_NOT_ALLOWED","请求来源没有获得工作区访问许可。",403);
        if(context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.Headers.CacheControl="no-store";
            if(context.Request.Method is not ("GET" or "HEAD")&&context.Request.Headers["X-Career-Client"]!="web")throw new DomainException("CLIENT_HEADER_REQUIRED","请通过当前工作区界面提交操作。",403);
            var anonymous=context.Request.Path.Value is "/api/bootstrap" or "/api/auth/pair" or "/api/auth/refresh";
            var session=owner.Validate(context.Request.Headers.Authorization.ToString());
            if(!anonymous&&session is null)throw new DomainException("OWNER_SESSION_REQUIRED","请先使用本机配对码连接当前工作区。",401);
            if(session is not null)context.Items["ownerSession"]=session;
        }
        await next(context);
    }
    catch(DomainException error)
    {
        if(context.Response.HasStarted)throw;
        context.Response.StatusCode=error.Status;
        await context.Response.WriteAsJsonAsync(new ProblemResponse("urn:career:problem:"+error.Code,error.Code,error.Status,error.Message,error.Code,Current:error.Current),CareerJson.Default.ProblemResponse,"application/problem+json",context.RequestAborted);
    }
    catch(Exception error) when(error is BadHttpRequestException or JsonException)
    {
        if(context.Response.HasStarted)throw;
        context.Response.StatusCode=400;
        await context.Response.WriteAsJsonAsync(new ProblemResponse("urn:career:problem:invalid-json","输入无效",400,"提交内容不符合接口格式，请检查字段。","INVALID_JSON"),CareerJson.Default.ProblemResponse,"application/problem+json",context.RequestAborted);
    }
    catch(Exception error) when(error is IOException or SqliteException or System.Security.Cryptography.CryptographicException)
    {
        if(context.Response.HasStarted)throw;
        app.Logger.LogError("Workspace operation failed with {ErrorType}",error.GetType().Name);
        context.Response.StatusCode=503;
        await context.Response.WriteAsJsonAsync(new ProblemResponse("urn:career:problem:storage-unavailable","工作区暂不可写",503,"无法安全读取或保存工作区。原文件和输入保留，请检查本机凭据、磁盘或恢复状态。","WORKSPACE_STORAGE_UNAVAILABLE"),CareerJson.Default.ProblemResponse,"application/problem+json",context.RequestAborted);
    }
    catch(OperationCanceledException) when(context.RequestAborted.IsCancellationRequested) { }
    catch(Exception error)
    {
        if(context.Response.HasStarted)throw;
        app.Logger.LogError("Workspace request could not complete: {ErrorType}",error.GetType().Name);
        context.Response.StatusCode=500;
        await context.Response.WriteAsJsonAsync(new ProblemResponse("urn:career:problem:request-interrupted","操作未完成",500,"当前操作未能完成。输入与已有工作保留；请重新读取状态，核对原请求接纳记录后再决定下一步。","REQUEST_INTERRUPTED"),CareerJson.Default.ProblemResponse,"application/problem+json",context.RequestAborted);
    }
});
app.UseRateLimiter();
if(Directory.Exists(webRoot)){app.UseDefaultFiles();app.UseStaticFiles();}
void RefreshCookie(HttpContext context,string token)=>context.Response.Cookies.Append("career.refresh",token,new CookieOptions{HttpOnly=true,Secure=context.Request.IsHttps,SameSite=SameSiteMode.Strict,Path="/api/auth",MaxAge=TimeSpan.FromDays(7),IsEssential=true});
app.MapGet("/api/bootstrap",()=>TypedResults.Ok(new BootstrapResponse("Career",store.WorkspaceId,store.WorkspaceName,owner.HasOwner,true,Protocol.Version)));
app.MapPost("/api/auth/pair",(PairRequest request,HttpContext context)=>{var session=owner.Pair(request.Code);RefreshCookie(context,session.Refresh);return TypedResults.Ok(session.Access);}).RequireRateLimiting("auth");
app.MapPost("/api/auth/refresh",(HttpContext context)=>{var session=owner.Refresh(context.Request.Cookies["career.refresh"]);RefreshCookie(context,session.Refresh);return TypedResults.Ok(session.Access);}).RequireRateLimiting("auth");
app.MapPost("/api/auth/logout",(HttpContext context)=>{owner.Logout(context.Items["ownerSession"] as SessionDocument,context.Request.Cookies["career.refresh"]);context.Response.Cookies.Delete("career.refresh",new CookieOptions{Path="/api/auth"});return TypedResults.NoContent();}).RequireRateLimiting("auth");
app.MapGet("/api/workspace",()=>TypedResults.Ok(tasks.Workspace()));
app.MapGet("/api/tasks/{id}",(string id)=>TypedResults.Ok(tasks.Read(id)));
app.MapGet("/api/connection",()=>TypedResults.Ok(connections.Read()));
app.MapPut("/api/connection",async(SaveConnectionRequest request,CancellationToken ct)=>TypedResults.Ok(await connections.SaveAsync(request,ct))).RequireRateLimiting("commands");
app.MapPost("/api/connection/test",async(TestConnectionRequest request,ExecutionCoordinator coordinator,CancellationToken ct)=>TypedResults.Ok(await coordinator.TestConnectionAsync(request,ct))).RequireRateLimiting("commands");
app.MapPost("/api/tasks",(CreateTaskRequest request)=>{var task=tasks.Create(request);return TypedResults.Created("/api/tasks/"+task.Id,task);}).RequireRateLimiting("commands");
app.MapPatch("/api/tasks/{id}",(string id,UpdateTaskRequest request)=>TypedResults.Ok(tasks.Revise(id,request))).RequireRateLimiting("commands");
app.MapPost("/api/tasks/{id}/confirm-plan",(string id,ConfirmPlanRequest request)=>TypedResults.Ok(tasks.ConfirmPlan(id,request))).RequireRateLimiting("commands");
foreach(var action in new[]{"pause","resume","cancel","retry"})
{
    var command=action;app.MapPost("/api/tasks/{id}/"+command,(string id,TaskCommandRequest request)=>TypedResults.Ok(tasks.Control(id,command,request))).RequireRateLimiting("commands");
}
app.MapPost("/api/approvals/{id}/decision",(string id,ApprovalDecisionRequest request)=>TypedResults.Ok(tasks.DecideApproval(id,request))).RequireRateLimiting("commands");
app.MapPost("/api/tasks/{id}/acceptances",(string id,AcceptanceRequest request)=>TypedResults.Ok(tasks.Accept(id,request))).RequireRateLimiting("commands");
app.MapGet("/api/commands/{id}",(string id)=>{var r=store.ReadCommand(id)??throw new DomainException("COMMAND_NOT_FOUND","暂未找到这个请求的接纳记录，请保留原请求并重读状态。",404);return TypedResults.Ok(new CommandReceiptResponse(r.RequestId,r.Route,r.TaskId,r.Version,r.AcceptedAt));});
app.MapGet("/api/artifacts/{id}/versions/{version:int}/download",(string id,int version)=>
{
    var document=store.ReadTasks().FirstOrDefault(d=>d.Task.Artifacts.Any(a=>a.Id==id&&a.Version==version))??throw new DomainException("ARTIFACT_NOT_FOUND","成果版本不存在。",404);
    var artifact=document.Task.Artifacts.First(a=>a.Id==id&&a.Version==version);return TypedResults.File(publisher.Read(artifact,document.Task.Id),"text/markdown; charset=utf-8",artifact.FileName);
});
foreach(var action in new[]{"pause-intake","resume-intake"})
{
    var command=action;app.MapPost("/api/workspace/"+command,(TestConnectionRequest request)=>
    {
        DomainRules.RequestId(request.RequestId);var hash=DomainRules.Hash(command);
        store.SettingCommand("POST /api/workspace/"+command,request.RequestId,hash,"intakePaused",command=="pause-intake"?"true":"false");
        return TypedResults.Ok(tasks.Workspace());
    }).RequireRateLimiting("commands");
}
app.MapGet("/api/events",async(HttpContext context,OwnerSessions sessions,CancellationToken requestCancellation)=>
{
    using var streamStop=CancellationTokenSource.CreateLinkedTokenSource(requestCancellation,app.Lifetime.ApplicationStopping);
    var ct=streamStop.Token;
    var after=long.TryParse(context.Request.Query["after"],out var parsed)?Math.Max(0,parsed):0;
    context.Response.ContentType="text/event-stream";context.Response.Headers["X-Accel-Buffering"]="no";
    await context.Response.WriteAsync(": connected\n\n",ct);await context.Response.Body.FlushAsync(ct);
    var heartbeat=DateTimeOffset.UtcNow;
    try { while(!ct.IsCancellationRequested)
    {
        if(sessions.Validate(context.Request.Headers.Authorization.ToString()) is null)break;
        var events=store.ReadEvents(after);
        foreach(var value in events){await context.Response.WriteAsync("id: "+value.Seq+"\nevent: workspace\ndata: "+JsonSerializer.Serialize(value,CareerJson.Default.EventResponse)+"\n\n",ct);after=value.Seq;}
        if(events.Length>0)await context.Response.Body.FlushAsync(ct);
        if(DateTimeOffset.UtcNow-heartbeat>TimeSpan.FromSeconds(15)){await context.Response.WriteAsync(": heartbeat\n\n",ct);await context.Response.Body.FlushAsync(ct);heartbeat=DateTimeOffset.UtcNow;}
        await Task.Delay(200,ct);
    } } catch(OperationCanceledException) when(ct.IsCancellationRequested) { }
});
app.MapGet("/health",()=>TypedResults.Text("ready"));
if(Directory.Exists(webRoot))app.MapFallbackToFile("index.html");
Console.WriteLine("Career 工作区："+workspaceDirectory);
Console.WriteLine("Career 配对码："+owner.PairingCode+"（一次有效，10 分钟内使用）");
Console.WriteLine($"Career 界面：http://127.0.0.1:{port}");
await app.RunAsync();
return 0;
