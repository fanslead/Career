using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace P0.Host;

public sealed class WorkerRunner(ProbeStore store)
{
    private readonly string? worker = Environment.GetEnvironmentVariable("P0_WORKER");
    private readonly string dotnet = Environment.GetEnvironmentVariable("P0_DOTNET") ?? "dotnet";
    private readonly ConcurrentDictionary<string, byte> active = new();
    private readonly ConcurrentDictionary<int, Process> processes = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> runCancellations = new();
    private readonly CancellationTokenSource stopping = new();
    public bool Configured => worker is not null && File.Exists(worker);

    public void Schedule(string id)
    {
        if (!active.TryAdd(id, 0)) return;
        _ = RunAsync(id);
    }

    private async Task RunAsync(string id)
    {
        Process? process = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
            runCancellations[id] = timeout;
            var seconds = Environment.GetEnvironmentVariable("P0_ENABLE_FAULTS") == "1" &&
                int.TryParse(Environment.GetEnvironmentVariable("P0_WORKER_TIMEOUT_SECONDS"), out var requested) && requested is >= 1 and <= 120
                ? requested : 40;
            timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
            var token = timeout.Token;
            var input = store.Begin(id);
            var psi = new ProcessStartInfo(worker!.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? dotnet : worker)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            if (worker.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) psi.ArgumentList.Add(worker);
            process = Process.Start(psi) ?? throw new InvalidOperationException("Worker failed to start.");
            processes.TryAdd(process.Id, process);
            store.WorkerStarted(id, process.Id);
            // Drain stderr concurrently to avoid pipe deadlock; bounded capture is unnecessary
            // for the fixed probe, but must be added before executing arbitrary plugin processes.
            var stderrTask = process.StandardError.ReadToEndAsync(token);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new RpcRequest("2.0", "run", "agent.run", input), ProbeJson.Default.RpcRequest));
            await process.StandardInput.FlushAsync(token);
            WorkerResult? result = null;
            var messageCount = 0;
            while (!token.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(token);
                if (line is null) break;
                if (line.Length > 2_000_000 || ++messageCount > 1000) throw new InvalidDataException("Worker protocol bounds exceeded.");
                using var doc = JsonDocument.Parse(line);
                var message = doc.RootElement;
                if (message.GetProperty("jsonrpc").GetString() != "2.0") throw new InvalidDataException("Protocol version mismatch.");
                if (message.TryGetProperty("method", out var method))
                {
                    var payload = message.GetProperty("params");
                    if (method.GetString() == "tool.execute")
                    {
                        var callId = message.GetProperty("id").GetString()!;
                        try
                        {
                            if (payload.GetProperty("runId").GetString() != id) throw new InvalidDataException("Run binding mismatch.");
                            var receipt = await store.ExecuteToolAsync(id, payload.GetProperty("operationId").GetString()!,
                                payload.GetProperty("name").GetString()!, payload.GetProperty("arguments"), token);
                            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new RpcReceipt("2.0", callId, receipt), ProbeJson.Default.RpcReceipt));
                        }
                        catch (Exception e) when (e is not OperationCanceledException)
                        {
                            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new RpcFailure("2.0", callId, new RpcErrorDetail(-32000, e.Message)), ProbeJson.Default.RpcFailure));
                            throw;
                        }
                        await process.StandardInput.FlushAsync(token);
                    }
                    else if (method.GetString() == "agent.event")
                    {
                        var type = payload.TryGetProperty("type", out var t) ? t.GetString() ?? "WorkerEvent" : "WorkerEvent";
                        var text = payload.TryGetProperty("text", out var value) ? value.ToString() : payload.GetRawText();
                        store.AddEvent(id, type, text);
                    }
                    else throw new InvalidDataException("Unexpected worker method.");
                }
                else if (message.TryGetProperty("error", out var error)) throw new InvalidOperationException("Worker error: " + error.GetRawText());
                else if (message.GetProperty("id").GetString() == "run")
                {
                    result = message.GetProperty("result").Deserialize(ProbeJson.Default.WorkerResult);
                    break;
                }
            }
            process.StandardInput.Close();
            await process.WaitForExitAsync(token);
            var stderr = await stderrTask;
            if (process.ExitCode != 0 || result is null) throw new InvalidOperationException($"Worker exit={process.ExitCode}; {stderr}");
            await store.BeforeFinalCommit(id, token);
            store.Complete(id, result);
        }
        catch (Exception e)
        {
            store.Fail(id, e is OperationCanceledException ? "worker_cancelled_or_timed_out" : e.Message);
        }
        finally
        {
            runCancellations.TryRemove(id, out _);
            if (process is not null)
            {
                processes.TryRemove(process.Id, out _);
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                process.Dispose();
            }
            active.TryRemove(id, out _);
            // An approval can arrive after Complete publishes WaitingApproval but
            // before this invocation leaves the active set. Pick it up durably.
            if (!stopping.IsCancellationRequested && store.Get(id)?.Status == "Approved") Schedule(id);
        }
    }

    public void Cancel(string id)
    {
        if (runCancellations.TryGetValue(id, out var cancellation))
        {
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { store.Fail(id, "cancel_requested"); }
        }
        else store.Fail(id, "cancel_requested");
    }

    public void Stop()
    {
        stopping.Cancel();
        foreach (var process in processes.Values)
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    }
}
