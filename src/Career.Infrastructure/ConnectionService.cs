using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Career.Contracts;
using Career.Core;

namespace Career.Infrastructure;

public sealed class ConnectionService : IConnectionService
{
    private readonly string _path;
    private readonly ISecretStore _secrets;
    private readonly ICareerStore _store;
    private readonly SemaphoreSlim _gate=new(1,1);
    private ConnectionDocument? _current;
    public ConnectionService(string workspaceDirectory,ISecretStore secrets,ICareerStore store)
    {
        _path=Path.Combine(workspaceDirectory,".models","providers","default.json");_secrets=secrets;_store=store;
        if(File.Exists(_path))
        {
            try{_current=JsonSerializer.Deserialize(File.ReadAllBytes(_path),StorageJson.Default.ConnectionDocument);if(_current is null)throw new IOException("连接配置为空。");ValidateEndpoint(_current.Connection.Endpoint,_current.Connection.AllowLocalEndpoint);}
            catch(Exception e) when(e is JsonException or DomainException or IOException){throw new IOException("模型配置无法读取，原文件已保留，请修复配置或从备份恢复。",e);}
        }
    }
    public ConnectionResponse? Read()=>_current?.Connection;
    public string? CurrentHash=>_current?.ConfigurationHash;
    public async ValueTask<ConnectionResponse> SaveAsync(SaveConnectionRequest r,CancellationToken ct)
    {
        DomainRules.RequestId(r.RequestId);DomainRules.Text(r.Name,"连接名称",80);DomainRules.Text(r.Model,"模型标识",160);DomainRules.Text(r.ApiKey,"密钥",4096,true);
        if(r.Protocol!="chat-completions")throw new DomainException("PROTOCOL_NOT_AVAILABLE","这批先接入 OpenAI 兼容 Chat Completions；其他协议将在后续扩展。",400);
        if(r.MaxOutputTokens is <256 or >16000 || r.MaxModelCalls is <1 or >10 || r.MaxInputCharacters is <100 or >100000)throw new DomainException("INVALID_BUDGET","模型调用限制超出支持范围。",400);
        ValidateEndpoint(r.Endpoint,r.AllowLocalEndpoint);
        var payloadHash=DomainRules.Hash(JsonSerializer.Serialize(r,CareerJson.Default.SaveConnectionRequest));
        await _gate.WaitAsync(ct);
        try
        {
            if(_store.ReadCommand(r.RequestId) is {} receipt)
            {
                if(receipt.Route!="PUT /api/connection" || receipt.PayloadHash!=payloadHash)throw new DomainException("IDEMPOTENCY_CONFLICT","请求标识已用于其他内容。");
                return JsonSerializer.Deserialize(receipt.ResponseJson,CareerJson.Default.ConnectionResponse)!;
            }
            if(_current?.LastRequestId==r.RequestId)
            {
                if(_current.LastPayloadHash!=payloadHash)throw new DomainException("IDEMPOTENCY_CONFLICT","请求标识已用于其他内容。");
                PersistReceipt(r.RequestId,payloadHash,_current.Connection);return _current.Connection;
            }
            string secretRef=_current?.SecretRef??DomainRules.Id("secret");
            if(!string.IsNullOrEmpty(r.ApiKey))
            {
                secretRef=DomainRules.Id("secret");
                try{await _secrets.SaveAsync(secretRef,r.ApiKey,ct);}
                catch(Exception error) when(error is IOException or InvalidOperationException or CryptographicException){throw CredentialUnavailable();}
            }
            var hasCredential=await ReadCredentialAsync(secretRef,ct) is not null;
            if(!hasCredential)throw new DomainException("MODEL_CREDENTIAL_REQUIRED","请填写模型密钥。密钥只保存在本机加密凭据库。",400);
            var connection=new ConnectionResponse("default",r.Name,r.Protocol,r.Endpoint.TrimEnd('/'),r.Model,true,r.AllowLocalEndpoint,"untested",null,null,r.MaxOutputTokens,r.MaxModelCalls,r.MaxInputCharacters);
            var hash=DomainRules.Hash(JsonSerializer.Serialize(connection,CareerJson.Default.ConnectionResponse)+"\n"+secretRef);
            var next=new ConnectionDocument(connection,secretRef,hash,r.RequestId,payloadHash);
            Persist(next);_current=next;PersistReceipt(r.RequestId,payloadHash,connection);_store.PublishEvent("connection.saved","模型连接已保存，参数改变后需要重新测试。");return connection;
        }
        finally{_gate.Release();}
    }
    private void PersistReceipt(string requestId,string hash,ConnectionResponse response)
        =>_store.SaveCommand(new StoredCommand(requestId,"PUT /api/connection",hash,"",0,JsonSerializer.Serialize(response,CareerJson.Default.ConnectionResponse),DateTimeOffset.UtcNow));
    private void Persist(ConnectionDocument document)
    {
        var directory=Path.GetDirectoryName(_path)!;Directory.CreateDirectory(directory);ArtifactPublisher.SafeDirectory(directory);
        var temp=Path.Combine(directory,".connection-"+Guid.NewGuid().ToString("N")+".tmp");
        ArtifactPublisher.WriteDurable(temp,JsonSerializer.SerializeToUtf8Bytes(document,StorageJson.Default.ConnectionDocument));
        if(JsonSerializer.Deserialize(File.ReadAllBytes(temp),StorageJson.Default.ConnectionDocument)?.ConfigurationHash!=document.ConfigurationHash)throw new IOException("连接配置暂存校验失败。");
        File.Move(temp,_path,true);
    }
    public void RecordTest(string hash,bool succeeded,string message)
    {
        _gate.Wait();try{if(_current is null || _current.ConfigurationHash!=hash)throw new DomainException("CONNECTION_CHANGED","测试期间连接已改变，请重新测试。");var next=_current with {Connection=_current.Connection with {TestState=succeeded?"verified":"failed",TestMessage=message,TestedAt=DateTimeOffset.UtcNow}};Persist(next);_current=next;_store.PublishEvent("connection.tested",succeeded?"模型连接能力测试通过。":"模型连接能力测试未通过，设置保留。");}finally{_gate.Release();}
    }
    public async ValueTask<WorkerRequest> CreateWorkerRequestAsync(TaskResponse task,RunResponse run,RunCheckpoint checkpoint,string hostEpoch,long fence,bool testOnly,CancellationToken ct)
    {
        var current=_current??throw new DomainException("MODEL_NOT_CONFIGURED","先连接一个模型，任务草稿已经保存。",409);
        ValidateEndpoint(current.Connection.Endpoint,current.Connection.AllowLocalEndpoint);
        if(!testOnly && current.Connection.TestState!="verified")throw new DomainException("MODEL_NOT_VERIFIED","模型连接尚未通过文本与工具能力测试，草稿已保留。",409);
        if(!testOnly && checkpoint.ConnectionHash!=current.ConfigurationHash)throw new DomainException("CONNECTION_CHANGED","连接参数已改变，旧运行不能直接沿用。请重新测试并开始新一轮执行。",409);
        var key=await ReadCredentialAsync(current.SecretRef,ct)??throw CredentialUnavailable();
        if(task.InputText.Length>current.Connection.MaxInputCharacters)throw new DomainException("INPUT_LIMIT","输入资料超过这条连接的上限，请减少资料或调整限制。",409);
        return new WorkerRequest(Protocol.Version,run.Id,run.AttemptId!,hostEpoch,fence,checkpoint.OperationId,task.Goal,task.Criteria,task.InputText,current.Connection.Protocol,current.Connection.Endpoint,current.Connection.Model,key,current.Connection.MaxOutputTokens,current.Connection.MaxModelCalls,current.Connection.MaxInputCharacters,checkpoint.Session,checkpoint.Approval,checkpoint.Approved,checkpoint.PublishedArtifactId,testOnly,current.Connection.AllowLocalEndpoint);
    }
    private static DomainException CredentialUnavailable()=>new("MODEL_CREDENTIAL_UNAVAILABLE","无法安全读取模型凭据。请恢复本机凭据访问或使用原应用版本；原凭据库、连接设置与已有工作保留。",409);
    private async ValueTask<string?> ReadCredentialAsync(string reference,CancellationToken ct)
    {
        try{return await _secrets.ReadAsync(reference,ct);}
        catch(Exception error) when(error is IOException or InvalidOperationException or CryptographicException){throw CredentialUnavailable();}
    }
    public static void ValidateEndpoint(string endpoint,bool allowLocal)
    {
        if(!Uri.TryCreate(endpoint,UriKind.Absolute,out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length>0 || uri.Query.Length>0 || uri.Fragment.Length>0)
            throw new DomainException("INVALID_MODEL_ENDPOINT","模型地址应为不含密码、查询参数的 HTTP(S) 地址。",400);
        var local=uri.Host.Equals("localhost",StringComparison.OrdinalIgnoreCase)||(IPAddress.TryParse(uri.Host,out var ip)&&IPAddress.IsLoopback(ip));
        if(local&&!allowLocal)throw new DomainException("LOCAL_MODEL_PERMISSION_REQUIRED","本机模型需要在这条连接中明确允许本地地址。",400);
        if(uri.Scheme!="https" && !(allowLocal&&local))throw new DomainException("MODEL_TLS_REQUIRED","远程模型连接需要 HTTPS；HTTP 仅允许明确配置的本机模型。",400);
        if(IPAddress.TryParse(uri.Host,out var address) && !local && IsPrivate(address))throw new DomainException("MODEL_TARGET_DENIED","模型地址不能指向私网或元数据服务。",400);
    }
    public static bool IsPrivate(IPAddress address)
    {
        if(address.IsIPv4MappedToIPv6)address=address.MapToIPv4();var b=address.GetAddressBytes();
        return IPAddress.IsLoopback(address) || (b.Length==4&&(b[0]is 0 or 10 or 127||b[0]==169&&b[1]==254||b[0]==172&&b[1]>=16&&b[1]<=31||b[0]==192&&b[1]==168||b[0]>=224)) || (b.Length==16&&(address.IsIPv6LinkLocal||address.IsIPv6SiteLocal||address.Equals(IPAddress.IPv6Any)||(b[0]&0xfe)==0xfc||b[0]==0xff));
    }
}
