using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Career.Contracts;
using Career.Core;

namespace Career.Infrastructure;

public sealed record ProcessRecord(string AttemptId,int ProcessId,long StartTimeUtcTicks,string Executable,DateTimeOffset RegisteredAt);
[JsonSourceGenerationOptions(PropertyNamingPolicy=JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ProcessRecord))]
public partial class ProcessJson:JsonSerializerContext;

public sealed class WorkerSupervisor(string workspaceDirectory,string workerPath)
{
    private readonly string _processDirectory=Path.Combine(workspaceDirectory,"state","processes");
    private readonly string _workerPath=Path.GetFullPath(workerPath);
    public async Task<WorkerResponse> RunAsync(WorkerRequest request,Func<WorkerEnvelope,CancellationToken,Task<WorkerEnvelope>> handle,CancellationToken cancellationToken)
    {
        if(!File.Exists(_workerPath))throw new DomainException("WORKER_UNAVAILABLE","执行器未安装或缺少发布文件，请重新构建或安装完整应用。",503);
        var start=new ProcessStartInfo{UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Path.GetDirectoryName(_workerPath)!};
        if(_workerPath.EndsWith(".dll",StringComparison.OrdinalIgnoreCase))
        {
            start.FileName=Environment.GetEnvironmentVariable("CAREER_DOTNET_PATH")??"dotnet";start.ArgumentList.Add(_workerPath);
        }
        else start.FileName=_workerPath;
        var path=Environment.GetEnvironmentVariable("PATH");start.Environment.Clear();
        if(path is not null)start.Environment["PATH"]=path;
        start.Environment["DOTNET_NOLOGO"]="1";start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"]="1";
        Directory.CreateDirectory(_processDirectory);ArtifactPublisher.SafeDirectory(_processDirectory);
        var recordPath=Path.Combine(_processDirectory,request.AttemptId+".json");
        using var process=Process.Start(start)??throw new IOException("无法启动执行器。");
        Task stderr=Task.CompletedTask;
        try
        {
            var record=new ProcessRecord(request.AttemptId,process.Id,process.StartTime.ToUniversalTime().Ticks,_workerPath,DateTimeOffset.UtcNow);
            ArtifactPublisher.WriteDurable(recordPath,JsonSerializer.SerializeToUtf8Bytes(record,ProcessJson.Default.ProcessRecord));
            stderr=DrainAsync(process.StandardError.BaseStream,cancellationToken);
            await WriteAsync(process,new WorkerEnvelope("2.0","run","agent.run",null,JsonSerializer.SerializeToElement(request,CareerJson.Default.WorkerRequest),null,null),cancellationToken);
            var reader=new BoundedLineReader(process.StandardOutput.BaseStream);var hello=false;WorkerResponse? response=null;
            while(true)
            {
                var line=await reader.ReadAsync(cancellationToken);
                if(line is null)break;
                WorkerEnvelope envelope;
                try{envelope=JsonSerializer.Deserialize(line,CareerJson.Default.WorkerEnvelope)??throw new JsonException();}
                catch(JsonException){throw new DomainException("INVALID_WORKER_PROTOCOL","执行器输出不符合协议，已停止并保留任务状态。",503);}
                if(envelope.Jsonrpc!="2.0")throw new DomainException("INVALID_WORKER_PROTOCOL","执行器协议版本不正确。",503);
                if(!hello)
                {
                    if(envelope.Method!="worker.hello" || envelope.Params is not {} parameters || parameters.GetProperty("protocolVersion").GetString()!=Protocol.Version || parameters.GetProperty("attemptId").GetString()!=request.AttemptId || parameters.GetProperty("runId").GetString()!=request.RunId || parameters.GetProperty("hostEpoch").GetString()!=request.HostEpoch || parameters.GetProperty("fencingToken").GetInt64()!=request.FencingToken)
                        throw new DomainException("WORKER_HANDSHAKE_FAILED","执行器身份或协议不兼容。",503);
                    hello=true;continue;
                }
                if(envelope.Id=="run" && envelope.Result is {} final)
                {
                    if(final.ProtocolVersion!=Protocol.Version || final.RunId!=request.RunId || final.AttemptId!=request.AttemptId || final.HostEpoch!=request.HostEpoch || final.FencingToken!=request.FencingToken)throw new DomainException("STALE_WORKER","执行器返回了过期身份的结果。",503);
                    response=final;break;
                }
                if(envelope.Id is null || envelope.Method is not ("tool.publish" or "model.reserve" or "model.settle"))throw new DomainException("UNEXPECTED_WORKER_REQUEST","执行器提出了不支持的命令。",503);
                var reply=await handle(envelope,cancellationToken);await WriteAsync(process,reply,cancellationToken);
            }
            await process.WaitForExitAsync(cancellationToken);
            if(response is null)throw new DomainException("WORKER_EXITED","执行器退出前没有交付完整结果，已有工作保留，可检查后继续。",503);
            return response;
        }
        finally
        {
            if(!process.HasExited)
            {
                process.Kill(entireProcessTree:true);
                using var killDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try{await process.WaitForExitAsync(killDeadline.Token);}catch(OperationCanceledException){throw new DomainException("WORKER_STOP_UNCONFIRMED","无法确认旧执行器停止，暂时冻结任务，防止重复派发。",503);}
            }
            if(process.HasExited && File.Exists(recordPath))File.Delete(recordPath);
            try{await stderr;}catch(OperationCanceledException){}
        }
    }
    private static async Task WriteAsync(Process process,WorkerEnvelope envelope,CancellationToken ct)
    {
        var line=JsonSerializer.Serialize(envelope,CareerJson.Default.WorkerEnvelope);
        if(Encoding.UTF8.GetByteCount(line)>Protocol.MaxMessageBytes)throw new DomainException("WORKER_MESSAGE_TOO_LARGE","任务或检查点超过执行协议上限，请减少输入资料。",400);
        await process.StandardInput.WriteLineAsync(line.AsMemory(),ct);await process.StandardInput.FlushAsync(ct);
    }
    private static async Task DrainAsync(Stream stream,CancellationToken ct)
    {
        // Worker stderr is deliberately not copied into owner logs: providers can include secrets in errors.
        var buffer=new byte[4096];while(await stream.ReadAsync(buffer,ct)>0){}
    }
    public async Task RecoverProcessesAsync(CancellationToken ct)
    {
        if(!Directory.Exists(_processDirectory))return;
        foreach(var file in Directory.EnumerateFiles(_processDirectory,"attempt_*.json"))
        {
            ct.ThrowIfCancellationRequested();
            ProcessRecord? record;
            try{record=JsonSerializer.Deserialize(File.ReadAllBytes(file),ProcessJson.Default.ProcessRecord);}catch(JsonException){throw new IOException("旧执行器登记文件损坏，无法证明安全接管。原文件已保留。");}
            if(record is null)throw new IOException("旧执行器登记文件为空，无法证明安全接管。");
            try
            {
                using var process=Process.GetProcessById(record.ProcessId);
                if(!process.HasExited && process.StartTime.ToUniversalTime().Ticks==record.StartTimeUtcTicks)
                {
                    process.Kill(true);using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    try{await process.WaitForExitAsync(timeout.Token);}catch(OperationCanceledException){throw new IOException("旧执行器未确认停止，工作区暂不派发任务。");}
                }
            }
            catch(ArgumentException){}
            File.Delete(file);
        }
    }
}

internal sealed class BoundedLineReader(Stream stream)
{
    private readonly byte[] _buffer=new byte[4096];private int _position;private int _length;
    public async Task<byte[]?> ReadAsync(CancellationToken ct)
    {
        using var line=new MemoryStream();
        while(true)
        {
            if(_position==_length){_length=await stream.ReadAsync(_buffer,ct);_position=0;if(_length==0)return line.Length==0?null:line.ToArray();}
            var start=_position;
            while(_position<_length&&_buffer[_position]!=(byte)'\n')_position++;
            var count=_position-start;
            if(line.Length+count>Protocol.MaxMessageBytes)throw new DomainException("WORKER_MESSAGE_TOO_LARGE","执行器消息超过协议上限，已停止处理。",503);
            line.Write(_buffer,start,count);
            if(_position<_length){_position++;var result=line.ToArray();return result.Length>0&&result[^1]=='\r'?result[..^1]:result;}
        }
    }
}
