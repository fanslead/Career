using System.Runtime.CompilerServices;
using System.Text.Json;
using P0.Host;

if (args.Length > 0 && args[0] == WorkspaceLock.ProbeArgument)
    return args.Length == 2 && !string.IsNullOrWhiteSpace(args[1]) ? WorkspaceLock.Probe(args[1]) : 64;

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("P0_URL") ?? "http://127.0.0.1:5270");
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, ProbeJson.Default));
var state = Path.GetFullPath(Environment.GetEnvironmentVariable("P0_STATE") ?? ".state");
var store = new ProbeStore(state);
var runner = new WorkerRunner(store);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton(runner);
var app = builder.Build();

// P0 only: constrain both the peer and Host before checking Origin. Comparing
// Origin to an arbitrary Host alone would permit DNS-rebinding requests.
// This is deliberately not a production identity/session implementation.
app.Use(async (context, next) =>
{
    if (context.Connection.RemoteIpAddress is { } ip && !System.Net.IPAddress.IsLoopback(ip))
    {
        context.Response.StatusCode = 403;
        return;
    }
    var host = context.Request.Host.Host;
    if (!host.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
        !(System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address)))
    {
        context.Response.StatusCode = 403;
        return;
    }
    if (HttpMethods.IsPost(context.Request.Method))
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (context.Request.Headers["X-P0-Client"] != "1" ||
            (origin.Length > 0 && !string.Equals(origin, $"{context.Request.Scheme}://{context.Request.Host}", StringComparison.OrdinalIgnoreCase)))
        {
            context.Response.StatusCode = 403;
            return;
        }
    }
    await next(context);
});
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => new Health("ok", !RuntimeFeature.IsDynamicCodeSupported,
    store.SqliteVersion, store.Fts5, Environment.ProcessId, runner.Configured));
app.MapGet("/api/runs", () => store.List());
app.MapGet("/api/runs/{id}", (string id) => store.Get(id) is { } run
    ? Results.Json(run, ProbeJson.Default.RunView)
    : Results.Json(new ApiError("run_not_found"), ProbeJson.Default.ApiError, statusCode: 404));

app.MapPost("/api/runs", (CreateRun input, HttpContext context) =>
{
    if (!runner.Configured) return Results.Json(new ApiError("worker_not_configured"), ProbeJson.Default.ApiError, statusCode: 503);
    if (string.IsNullOrWhiteSpace(input.Prompt) || input.Prompt.Length > 1000 ||
        input.Scenario is not ("normal" or "invalid-arguments" or "endless"))
        return Results.Json(new ApiError("invalid_probe_input"), ProbeJson.Default.ApiError, statusCode: 400);
    var fault = context.Request.Headers["X-P0-Fault"].ToString();
    if (fault.Length > 0 && (Environment.GetEnvironmentVariable("P0_ENABLE_FAULTS") != "1" ||
        fault is not ("before-effect" or "after-effect" or "receipt-partial" or "receipt-flushed" or "after-ledger" or "before-final-commit" or "cancel-persisted")))
        return Results.Json(new ApiError("fault_injection_disabled"), ProbeJson.Default.ApiError, statusCode: 400);
    var run = store.Create(input, fault);
    runner.Schedule(run.Id);
    return Results.Json(run, ProbeJson.Default.RunView, statusCode: 202);
});

app.MapPost("/api/runs/{id}/decision", (string id, Decision decision) =>
{
    if (!store.Decide(id, decision)) return Results.Json(new ApiError("stale_or_invalid_approval"), ProbeJson.Default.ApiError, statusCode: 409);
    runner.Schedule(id);
    return Results.Json(store.Get(id)!, ProbeJson.Default.RunView, statusCode: 202);
});
app.MapPost("/api/runs/{id}/reconcile", (string id) =>
{
    if (!store.Reconcile(id)) return Results.Json(new ApiError("cannot_reconcile"), ProbeJson.Default.ApiError, statusCode: 409);
    if (store.Get(id)?.Status == "Approved") runner.Schedule(id);
    return Results.Json(store.Get(id)!, ProbeJson.Default.RunView, statusCode: 202);
});
app.MapPost("/api/runs/{id}/cancel", async (string id, HttpContext context) =>
{
    if (!store.RequestCancel(id)) return Results.Json(new ApiError("cannot_cancel_run"), ProbeJson.Default.ApiError, statusCode: 409);
    await store.AfterCancellationRequested(id, context.RequestAborted);
    runner.Cancel(id);
    return Results.Json(store.Get(id)!, ProbeJson.Default.RunView, statusCode: 202);
});
app.MapPost("/api/runs/{id}/resume", (string id) =>
{
    if (!store.ResumeFinalization(id)) return Results.Json(new ApiError("cannot_resume_finalization"), ProbeJson.Default.ApiError, statusCode: 409);
    runner.Schedule(id);
    return Results.Json(store.Get(id)!, ProbeJson.Default.RunView, statusCode: 202);
});

app.MapGet("/api/runs/{id}/events", async (string id, long? after, HttpContext context) =>
{
    if (store.Get(id) is null) { context.Response.StatusCode = 404; return; }
    var cursor = after ?? 0;
    if (long.TryParse(context.Request.Headers["Last-Event-ID"], out var last)) cursor = Math.Max(cursor, last);
    context.Response.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers["X-Accel-Buffering"] = "no";
    try
    {
        while (!context.RequestAborted.IsCancellationRequested)
        {
            foreach (var item in store.Events(id, cursor))
            {
                await context.Response.WriteAsync($"id: {item.Seq}\ndata: {JsonSerializer.Serialize(item, ProbeJson.Default.EventView)}\n\n", context.RequestAborted);
                cursor = item.Seq;
            }
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await Task.Delay(150, context.RequestAborted);
        }
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
});

app.Lifetime.ApplicationStopping.Register(runner.Stop);
foreach (var id in store.Recover()) runner.Schedule(id);
await app.RunAsync();
return 0;
