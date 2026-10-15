using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using Career.Contracts;

namespace Career.AgentWorker;

internal static class Program
{
    private static readonly JsonSerializerOptions ProtocolJson = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions MafJson = AIJsonUtilities.DefaultOptions;

    public static async Task<int> Main()
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        WorkerRequest? request = null;
        try
        {
            byte[] firstLine = await WorkerProtocol.ReadLineAsync(cancellation.Token);
            using JsonDocument document = JsonDocument.Parse(firstLine);
            JsonElement root = document.RootElement;
            if (root.GetProperty("jsonrpc").GetString() != "2.0" ||
                root.GetProperty("id").GetString() != "run" ||
                root.GetProperty("method").GetString() != "agent.run")
            {
                throw new WorkerFault("invalid_worker_request");
            }

            request = root.GetProperty("params").Deserialize<WorkerRequest>(ProtocolJson)
                ?? throw new WorkerFault("invalid_worker_request");
            Validate(request);

            await WorkerProtocol.WriteAsync(new WorkerEnvelope(
                "2.0", null, "worker.hello", null,
                JsonSerializer.SerializeToElement(new
                {
                    request.ProtocolVersion,
                    request.RunId,
                    request.AttemptId,
                    request.HostEpoch,
                    request.FencingToken,
                }, ProtocolJson), null, null), cancellation.Token);

            WorkerResponse response;
            await using var broker = new HostBroker(request);
            using IChatClient openAiClient = CreateChatClient(request).AsIChatClient();
            using var meteredClient = new ReservedChatClient(openAiClient, request, broker);
            try
            {
                response = request.TestOnly
                    ? await RunCapabilityProbeAsync(request, meteredClient, broker, cancellation.Token)
                    : await RunTaskAsync(request, meteredClient, broker, cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                response = Failed(request, broker, "cancelled");
            }
            catch (WorkerFault fault)
            {
                response = FailureOrPublishedSuccess(request, broker, fault.Code);
            }
            catch
            {
                response = FailureOrPublishedSuccess(request, broker, "worker_execution_failed");
            }

            await WriteFinalAsync(response, cancellation.Token, broker.Publication);
            return response.Status == "Failed" ? 1 : 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (request is not null)
            {
                await WriteFinalAsync(Failed(request, null, "cancelled"), CancellationToken.None);
            }
            else
            {
                await WriteProtocolErrorAsync("cancelled");
            }
            return 1;
        }
        catch (WorkerFault fault)
        {
            if (request is not null)
            {
                await WriteFinalAsync(Failed(request, null, fault.Code), CancellationToken.None);
            }
            else
            {
                await WriteProtocolErrorAsync(fault.Code);
            }
            return 1;
        }
        catch
        {
            if (request is not null)
            {
                await WriteFinalAsync(Failed(request, null, "worker_execution_failed"), CancellationToken.None);
            }
            else
            {
                await WriteProtocolErrorAsync("invalid_worker_request");
            }
            return 1;
        }
    }

    private static async Task<WorkerResponse> RunTaskAsync(
        WorkerRequest request,
        IChatClient chatClient,
        HostBroker broker,
        CancellationToken cancellationToken)
    {
        ChatClientAgent agent = CreateAgent(request, chatClient, broker, includePublishTool: true);
        AgentSession session = request.Session is JsonElement serializedSession
            ? await agent.DeserializeSessionAsync(serializedSession)
            : await agent.CreateSessionAsync(cancellationToken: cancellationToken);

        AgentResponse response;
        bool? approvalDecision = null;
        if (request.Approval is JsonElement approvalJson)
        {
            if (request.Approved is not bool approved)
            {
                throw new WorkerFault("approval_decision_required");
            }
            ToolApprovalRequestContent approval = DeserializeApproval(approvalJson);
            EnsurePublishApproval(approval);
            approvalDecision = approved;
            var decisionMessage = new ChatMessage(ChatRole.User,
                [approval.CreateResponse(approved, approved ? "Host approved the fixed publication." : "Host rejected the fixed publication.")]);
            response = await agent.RunAsync(decisionMessage, session, cancellationToken: cancellationToken);
        }
        else
        {
            string prompt = ComposeTaskPrompt(request);
            response = await agent.RunAsync(prompt, session, cancellationToken: cancellationToken);
        }

        ToolApprovalRequestContent? pendingApproval = FindPendingApproval(response);
        if (pendingApproval is not null)
        {
            if (broker.Publication is not null)
            {
                throw new WorkerFault("duplicate_publication_request_after_success");
            }
            EnsurePublishApproval(pendingApproval);
            var call = (FunctionCallContent)pendingApproval.ToolCall;
            (string fileName, string content) = ReadPublishArguments(call.Arguments);
            return new WorkerResponse(
                request.ProtocolVersion, request.RunId, request.AttemptId, request.HostEpoch, request.FencingToken,
                "WaitingApproval", await agent.SerializeSessionAsync(session), JsonSerializer.SerializeToElement(pendingApproval, MafJson),
                call.CallId, call.Name, fileName, content, response.Text ?? string.Empty,
                broker.ModelCalls, broker.InputTokens, broker.OutputTokens, broker.UsageState);
        }

        if (approvalDecision == false)
        {
            if (broker.Publication is not null)
            {
                throw new WorkerFault("rejected_publication_was_dispatched");
            }
            return new WorkerResponse(
                request.ProtocolVersion, request.RunId, request.AttemptId, request.HostEpoch, request.FencingToken,
                "ApprovalRejected", await agent.SerializeSessionAsync(session), null, null, null, null, null,
                response.Text ?? string.Empty, broker.ModelCalls, broker.InputTokens, broker.OutputTokens, broker.UsageState);
        }

        if (HasUnresolvedToolCall(response))
        {
            throw new WorkerFault("agent_iteration_limit_reached");
        }
        if (broker.Publication is null)
        {
            throw new WorkerFault("publication_not_completed");
        }

        return new WorkerResponse(
            request.ProtocolVersion, request.RunId, request.AttemptId, request.HostEpoch, request.FencingToken,
            "Succeeded", await agent.SerializeSessionAsync(session), null, null, null,
            broker.Publication.FileName, broker.Publication.Content, response.Text ?? string.Empty,
            broker.ModelCalls, broker.InputTokens, broker.OutputTokens, broker.UsageState);
    }

    private static async Task<WorkerResponse> RunCapabilityProbeAsync(
        WorkerRequest request,
        IChatClient chatClient,
        HostBroker broker,
        CancellationToken cancellationToken)
    {
        if (request.Session is not null || request.Approval is not null || request.Approved is not null)
        {
            throw new WorkerFault("test_only_resume_not_allowed");
        }

        ChatClientAgent textAgent = CreateAgent(request, chatClient, broker, includePublishTool: false);
        AgentResponse textResponse = await textAgent.RunAsync(
            "Reply with one short plain-text sentence confirming that this model connection works. Do not use any tools.",
            await textAgent.CreateSessionAsync(cancellationToken: cancellationToken),
            cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(textResponse.Text))
        {
            throw new WorkerFault("text_capability_missing");
        }

        ChatClientAgent toolAgent = CreateAgent(request, chatClient, broker, includePublishTool: true);
        AgentSession toolSession = await toolAgent.CreateSessionAsync(cancellationToken: cancellationToken);
        AgentResponse toolResponse = await toolAgent.RunAsync(
            "For a harmless capability check, request publish_document with fileName 'connection-test.md' and content 'Connection test'. Do not call any other tool.",
            toolSession,
            cancellationToken: cancellationToken);
        ToolApprovalRequestContent? pendingApproval = FindPendingApproval(toolResponse);
        if (pendingApproval is null)
        {
            throw new WorkerFault("tool_approval_capability_missing");
        }
        EnsurePublishApproval(pendingApproval);
        if (broker.Publication is not null)
        {
            throw new WorkerFault("test_only_publication_dispatched");
        }

        FunctionCallContent call = (FunctionCallContent)pendingApproval.ToolCall;
        (string fileName, string content) = ReadPublishArguments(call.Arguments);
        JsonElement sessionCheckpoint = await toolAgent.SerializeSessionAsync(toolSession);
        _ = await toolAgent.DeserializeSessionAsync(sessionCheckpoint);
        JsonElement approvalCheckpoint = JsonSerializer.SerializeToElement(pendingApproval, MafJson);
        _ = DeserializeApproval(approvalCheckpoint);
        return new WorkerResponse(
            request.ProtocolVersion, request.RunId, request.AttemptId, request.HostEpoch, request.FencingToken,
            "TestSucceeded", sessionCheckpoint, approvalCheckpoint,
            call.CallId, call.Name, fileName, content, textResponse.Text,
            broker.ModelCalls, broker.InputTokens, broker.OutputTokens, broker.UsageState);
    }

    private static ChatClientAgent CreateAgent(WorkerRequest request, IChatClient chatClient, HostBroker broker, bool includePublishTool)
    {
        AITool[] tools = includePublishTool
            ? [new ApprovalRequiredAIFunction(broker.PublishFunction)]
            : [];
        var functionClient = new FunctionInvokingChatClient(chatClient)
        {
            MaximumIterationsPerRequest = request.MaxModelCalls,
            MaximumConsecutiveErrorsPerRequest = 0,
            IncludeDetailedErrors = false,
            AllowConcurrentInvocation = false,
        };
        return new ChatClientAgent(functionClient, new ChatClientAgentOptions
        {
            Name = "career-worker",
            Description = "Completes one fixed user task and proposes a single brokered document publication.",
            ChatOptions = new ChatOptions
            {
                ModelId = request.Model,
                MaxOutputTokens = request.MaxOutputTokens,
                Temperature = 0.2f,
                AllowMultipleToolCalls = false,
                Tools = tools,
                Instructions = includePublishTool
                    ? "Produce the requested Markdown result. The sole publication tool requires user approval; never claim publication unless the host returns a receipt. Do not retry a publication after a failed or uncertain host response."
                    : "Respond with text only and do not use tools.",
            },
        });
    }

    private static string ComposeTaskPrompt(WorkerRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Task goal:");
        builder.AppendLine(request.Goal);
        builder.AppendLine();
        builder.AppendLine("Acceptance criteria:");
        foreach (string criterion in request.Criteria)
        {
            builder.Append("- ").AppendLine(criterion);
        }
        builder.AppendLine();
        builder.AppendLine("User-provided input:");
        builder.AppendLine(request.InputText);
        return builder.ToString();
    }

    private static ToolApprovalRequestContent DeserializeApproval(JsonElement approvalJson) =>
        JsonSerializer.Deserialize<ToolApprovalRequestContent>(approvalJson, MafJson)
        ?? throw new WorkerFault("invalid_approval_checkpoint");

    private static void EnsurePublishApproval(ToolApprovalRequestContent approval)
    {
        if (approval.ToolCall is not FunctionCallContent functionCall || functionCall.Name != "publish_document")
        {
            throw new WorkerFault("unsupported_approval_checkpoint");
        }
    }

    private static ToolApprovalRequestContent? FindPendingApproval(AgentResponse response)
    {
        using IEnumerator<ToolApprovalRequestContent> approvals = response.Messages
            .SelectMany(message => message.Contents)
            .OfType<ToolApprovalRequestContent>()
            .GetEnumerator();
        if (!approvals.MoveNext())
        {
            return null;
        }
        ToolApprovalRequestContent approval = approvals.Current;
        if (approvals.MoveNext())
        {
            throw new WorkerFault("multiple_approval_requests_not_allowed");
        }
        return approval;
    }

    private static bool HasUnresolvedToolCall(AgentResponse response)
    {
        var completedCallIds = response.Messages.SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()
            .Select(result => result.CallId)
            .ToHashSet(StringComparer.Ordinal);
        return response.Messages.SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>()
            .Any(call => !call.InformationalOnly && !completedCallIds.Contains(call.CallId));
    }

    private static (string FileName, string Content) ReadPublishArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null)
        {
            throw new WorkerFault("invalid_publish_arguments");
        }
        string fileName = GetRequiredString(arguments, "fileName");
        string content = GetRequiredString(arguments, "content");
        return (fileName, content);
    }

    private static string GetRequiredString(IDictionary<string, object?> arguments, string name)
    {
        if (!arguments.TryGetValue(name, out object? value))
        {
            throw new WorkerFault("invalid_publish_arguments");
        }
        string? text = value switch
        {
            string stringValue => stringValue,
            JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString(),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new WorkerFault("invalid_publish_arguments");
        }
        return text;
    }

    private static OpenAI.Chat.ChatClient CreateChatClient(WorkerRequest request)
    {
        if (!Uri.TryCreate(request.Endpoint, UriKind.Absolute, out Uri? endpoint) ||
            endpoint.Scheme is not ("https" or "http") ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new WorkerFault("invalid_model_endpoint");
        }

        bool loopbackHost = IsLoopbackHost(endpoint);
        if (request.AllowLocalEndpoint != loopbackHost)
        {
            throw new WorkerFault("local_endpoint_permission_mismatch");
        }
        if (endpoint.Scheme == Uri.UriSchemeHttp && !loopbackHost)
        {
            throw new WorkerFault("insecure_model_endpoint");
        }

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectCallback = async (context, cancellationToken) =>
            {
                DnsEndPoint target = context.DnsEndPoint;
                if (!string.Equals(NormalizeHost(target.Host), NormalizeHost(endpoint.IdnHost), StringComparison.OrdinalIgnoreCase) ||
                    target.Port != endpoint.Port)
                {
                    throw new HttpRequestException("The model client attempted to connect outside the configured endpoint.");
                }

                IPAddress[] addresses = await Dns.GetHostAddressesAsync(target.Host, cancellationToken);
                if (addresses.Length == 0 || addresses.Any(address => !IsPermittedAddress(address, request.AllowLocalEndpoint)))
                {
                    throw new HttpRequestException("The model endpoint resolved to a disallowed network address.");
                }

                Exception? lastError = null;
                foreach (IPAddress address in addresses)
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(address, target.Port), cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception error)
                    {
                        socket.Dispose();
                        if (error is OperationCanceledException)
                        {
                            throw;
                        }
                        if (error is not SocketException)
                        {
                            throw;
                        }
                        lastError = error;
                    }
                }
                throw new HttpRequestException("The model endpoint could not be reached.", lastError);
            },
        };
        var httpClient = new HttpClient(handler, disposeHandler: true);
        var options = new OpenAIClientOptions
        {
            Endpoint = endpoint,
            NetworkTimeout = TimeSpan.FromSeconds(75),
            RetryPolicy = new ClientRetryPolicy(0),
            Transport = new HttpClientPipelineTransport(httpClient),
        };
        return new OpenAI.Chat.ChatClient(request.Model, new ApiKeyCredential(request.ApiKey), options);
    }

    private static string NormalizeHost(string host) => host.TrimEnd('.');

    private static bool IsLoopbackHost(Uri endpoint) =>
        IPAddress.TryParse(endpoint.IdnHost, out IPAddress? ipAddress)
            ? IPAddress.IsLoopback(ipAddress)
            : endpoint.IdnHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
              endpoint.IdnHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    private static bool IsPermittedAddress(IPAddress address, bool allowLocalEndpoint)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return IsPermittedAddress(address.MapToIPv4(), allowLocalEndpoint);
        }
        if (IPAddress.IsLoopback(address))
        {
            return allowLocalEndpoint;
        }
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] bytes = address.GetAddressBytes();
            byte first = bytes[0];
            byte second = bytes[1];
            return first is not (0 or 10 or 127 or >= 224) &&
                !(first == 169 && second == 254) &&
                !(first == 172 && second is >= 16 and <= 31) &&
                !(first == 192 && second == 168) &&
                !(first == 100 && second is >= 64 and <= 127) &&
                !(first == 192 && second == 88 && bytes[2] == 99) &&
                !(first == 192 && second == 31 && bytes[2] == 196) &&
                !(first == 192 && second == 52 && bytes[2] == 193) &&
                !(first == 192 && second == 175 && bytes[2] == 48) &&
                !(first == 198 && second is 18 or 19) &&
                !(first == 192 && second == 0 && bytes[2] == 0) &&
                !(first == 192 && second == 0 && bytes[2] == 2) &&
                !(first == 198 && second == 51 && bytes[2] == 100) &&
                !(first == 203 && second == 0 && bytes[2] == 113);
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte[] bytes = address.GetAddressBytes();
            bool compatiblePrefix = bytes.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0;
            bool reserved2001 = bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] <= 0x01;
            bool documentationPrefix = bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8 ||
                bytes[0] == 0x3f && bytes[1] == 0xff && bytes[2] == 0 && bytes[3] <= 0x0f;
            bool globallyRoutablePrefix = (bytes[0] & 0xe0) == 0x20;
            return !address.Equals(IPAddress.IPv6Any) && !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal && !address.IsIPv6Multicast &&
                globallyRoutablePrefix && !compatiblePrefix && !reserved2001 && !documentationPrefix &&
                !(bytes[0] == 0x20 && bytes[1] == 0x02) &&
                bytes[0] is not (0xfc or 0xfd);
        }
        return false;
    }

    private static void Validate(WorkerRequest request)
    {
        if (request.ProtocolVersion != Protocol.Version ||
            !HasIdentity(request.RunId) || !HasIdentity(request.AttemptId) || !HasIdentity(request.HostEpoch) ||
            request.FencingToken <= 0 || !HasIdentity(request.OperationId) ||
            !IsChatCompletionsProtocol(request.Protocol) || !HasIdentity(request.Endpoint) ||
            !HasIdentity(request.Model) || !HasIdentity(request.ApiKey) ||
            request.MaxOutputTokens is < 1 or > 32768 ||
            request.MaxModelCalls is < 1 or > 40 ||
            request.MaxInputCharacters is < 1 or > 1_000_000 ||
            request.Criteria is null || request.Criteria.Length > 100 || request.Goal is null || request.InputText is null)
        {
            throw new WorkerFault("invalid_worker_request");
        }

        long initialInputSize = (long)request.Goal.Length + request.InputText.Length + request.Criteria.Sum(value => value?.Length ?? 0);
        if (initialInputSize > request.MaxInputCharacters)
        {
            throw new WorkerFault("input_budget_exceeded");
        }
    }

    private static bool IsChatCompletionsProtocol(string protocol)
    {
        string normalized = protocol.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return normalized is "chatcompletions" or "openaichatcompletions" or "openaicompatiblechatcompletions";
    }

    private static bool HasIdentity(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256;

    private static WorkerResponse Failed(WorkerRequest request, HostBroker? broker, string code) => new(
        request.ProtocolVersion, request.RunId, request.AttemptId, request.HostEpoch, request.FencingToken,
        "Failed", null, null, null, null, null, null, string.Empty,
        broker?.ModelCalls ?? 0, broker?.InputTokens ?? 0, broker?.OutputTokens ?? 0,
        broker?.UsageState ?? "unknown", code, "The worker could not complete this step. Review the task state before retrying.");

    private static WorkerResponse FailureOrPublishedSuccess(WorkerRequest request, HostBroker broker, string code)
    {
        WorkerResponse failure = Failed(request, broker, code);
        return broker.Publication?.Receipt.Status == "Succeeded"
            ? CompactPublishedSuccess(failure)
            : failure;
    }

    private static WorkerResponse CompactPublishedSuccess(WorkerResponse response) => response with
    {
        Status = "Succeeded",
        Session = null,
        Approval = null,
        CallId = null,
        ToolName = null,
        FileName = null,
        Content = null,
        Text = string.Empty,
        ErrorCode = null,
        ErrorMessage = null,
    };

    private static async Task WriteFinalAsync(
        WorkerResponse response,
        CancellationToken cancellationToken,
        WorkerPublication? publication = null)
    {
        WorkerEnvelope envelope = new("2.0", "run", null, response, null, null, null);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(envelope, ProtocolJson);
        if (payload.Length > Protocol.MaxMessageBytes)
        {
            WorkerResponse compactResponse = publication?.Receipt.Status == "Succeeded"
                ? CompactPublishedSuccess(response)
                : response with
            {
                Status = "Failed",
                Session = null,
                Approval = null,
                CallId = null,
                ToolName = null,
                FileName = null,
                Content = null,
                Text = string.Empty,
                ErrorCode = "response_too_large",
                ErrorMessage = "The worker checkpoint exceeded the protocol limit. Retry after reviewing the task state.",
            };
            payload = JsonSerializer.SerializeToUtf8Bytes(envelope with { Result = compactResponse }, ProtocolJson);
        }
        await WorkerProtocol.WriteBytesAsync(payload, cancellationToken);
    }

    private static Task WriteProtocolErrorAsync(string code)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            id = "run",
            error = new { code, message = "Invalid or incomplete worker request." },
        }, ProtocolJson);
        return WorkerProtocol.WriteBytesAsync(payload, CancellationToken.None);
    }
}

internal sealed class HostBroker : IAsyncDisposable
{
    private readonly WorkerRequest _request;
    private int _publicationCount;
    private WorkerPublication? _publication;
    private int _modelCalls;
    private long _inputTokens;
    private long _outputTokens;
    private string _usageState = "unknown";

    public HostBroker(WorkerRequest request)
    {
        _request = request;
        PublishFunction = new RuntimeFunction(
            "publish_document",
            "Request approval to publish one Markdown document to the current task's artifact area.",
            """{"type":"object","properties":{"fileName":{"type":"string","description":"Safe Markdown file name for the document."},"content":{"type":"string","description":"Complete Markdown document content."}},"required":["fileName","content"],"additionalProperties":false}""",
            PublishAsync);
    }

    public AIFunction PublishFunction { get; }
    public WorkerPublication? Publication => Volatile.Read(ref _publication);
    public int ModelCalls => Volatile.Read(ref _modelCalls);
    public long InputTokens => Interlocked.Read(ref _inputTokens);
    public long OutputTokens => Interlocked.Read(ref _outputTokens);
    public string UsageState => Volatile.Read(ref _usageState);

    public void SetModelCalls(int value) => Interlocked.Exchange(ref _modelCalls, value);
    public void AddUsage(long inputTokens, long outputTokens)
    {
        Interlocked.Add(ref _inputTokens, inputTokens);
        Interlocked.Add(ref _outputTokens, outputTokens);
    }
    public void SetUsageState(string value) => Interlocked.Exchange(ref _usageState, value);

    private async ValueTask<object?> PublishAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (_request.TestOnly)
        {
            throw new WorkerFault("test_only_publication_dispatched");
        }
        if (Interlocked.Increment(ref _publicationCount) != 1)
        {
            throw new WorkerFault("multiple_publications_not_allowed");
        }

        string fileName = WorkerArguments.GetRequiredString(arguments, "fileName");
        string content = WorkerArguments.GetRequiredString(arguments, "content");
        var toolRequest = new WorkerToolRequest(
            _request.ProtocolVersion, _request.RunId, _request.AttemptId, _request.HostEpoch,
            _request.FencingToken, _request.OperationId, fileName, content);
        WorkerEnvelope response = await WorkerProtocol.RequestHostAsync(
            "publish", "tool.publish", toolRequest, cancellationToken);
        if (response.ToolResult is not WorkerToolResponse receipt ||
            receipt.OperationId != _request.OperationId || receipt.Status != "Succeeded" ||
            !WorkerArguments.HasIdentity(receipt.ArtifactId) || !WorkerArguments.HasIdentity(receipt.ContentHash))
        {
            throw new WorkerFault("host_publication_receipt_invalid");
        }

        Interlocked.Exchange(ref _publication, new WorkerPublication(fileName, content, receipt));
        return new { receipt.OperationId, receipt.ArtifactId, receipt.ContentHash, receipt.Status, receipt.Message };
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed record WorkerPublication(string FileName, string Content, WorkerToolResponse Receipt);

internal sealed class ReservedChatClient : DelegatingChatClient
{
    private readonly WorkerRequest _request;
    private readonly HostBroker _broker;
    private int _callCount;
    private int _unknownUsageCalls;

    public ReservedChatClient(IChatClient innerClient, WorkerRequest request, HostBroker broker) : base(innerClient)
    {
        _request = request;
        _broker = broker;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ChatMessage[] history = messages.ToArray();
        if (WorkerArguments.MeasureCharacters(history) > _request.MaxInputCharacters)
        {
            throw new WorkerFault("input_budget_exceeded");
        }
        return await GetReservedResponseAsync(history, options, cancellationToken);
    }

    private async Task<ChatResponse> GetReservedResponseAsync(
        ChatMessage[] history,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        int count = Interlocked.Increment(ref _callCount);
        if (count > _request.MaxModelCalls)
        {
            throw new WorkerFault("model_call_budget_exceeded");
        }

        string callId = Guid.NewGuid().ToString("N");
        ModelCallResponse reservation = await WorkerProtocol.RequestModelAsync(
            "model.reserve", _request, callId, _request.MaxOutputTokens, null, null, "reserved", cancellationToken);
        if (!reservation.Allowed)
        {
            throw new WorkerFault("model_budget_rejected");
        }

        _broker.SetModelCalls(count);
        ChatResponse? response = null;
        Exception? modelFailure = null;
        try
        {
            response = await base.GetResponseAsync(history, options, cancellationToken);
        }
        catch (Exception error)
        {
            modelFailure = error;
        }
        finally
        {
            UsageDetails? usage = response?.Usage;
            long? inputTokens = usage?.InputTokenCount;
            long? outputTokens = usage?.OutputTokenCount;
            bool known = inputTokens.HasValue && outputTokens.HasValue;
            if (known)
            {
                _broker.AddUsage(inputTokens!.Value, outputTokens!.Value);
            }
            else
            {
                Interlocked.Increment(ref _unknownUsageCalls);
            }
            _broker.SetUsageState(Volatile.Read(ref _unknownUsageCalls) == 0 && _broker.ModelCalls > 0 ? "known" : "unknown");

            try
            {
                ModelCallResponse settlement = await WorkerProtocol.RequestModelAsync(
                    "model.settle", _request, callId, _request.MaxOutputTokens,
                    inputTokens, outputTokens, known ? "known" : "unknown",
                    cancellationToken.IsCancellationRequested ? CancellationToken.None : cancellationToken);
                if (!settlement.Allowed)
                {
                    throw new WorkerFault("model_settlement_rejected");
                }
            }
            catch
            {
                throw new WorkerFault("model_settlement_failed");
            }
        }

        if (modelFailure is not null)
        {
            ExceptionDispatchInfo.Capture(modelFailure).Throw();
        }
        return response!;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatMessage[] history = messages.ToArray();
        if (WorkerArguments.MeasureCharacters(history) > _request.MaxInputCharacters)
        {
            throw new WorkerFault("input_budget_exceeded");
        }
        int count = Interlocked.Increment(ref _callCount);
        if (count > _request.MaxModelCalls)
        {
            throw new WorkerFault("model_call_budget_exceeded");
        }

        string callId = Guid.NewGuid().ToString("N");
        ModelCallResponse reservation = await WorkerProtocol.RequestModelAsync(
            "model.reserve", _request, callId, _request.MaxOutputTokens, null, null, "reserved", cancellationToken);
        if (!reservation.Allowed)
        {
            throw new WorkerFault("model_budget_rejected");
        }
        _broker.SetModelCalls(count);

        // This worker currently invokes MAF in non-streaming mode. Keep streaming calls
        // bounded too, and report usage as unknown if the selected provider omits it.
        long? inputTokens = null;
        long? outputTokens = null;
        try
        {
            await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(history, options, cancellationToken)
                .WithCancellation(cancellationToken))
            {
                foreach (UsageContent usageContent in update.Contents.OfType<UsageContent>())
                {
                    inputTokens = usageContent.Details.InputTokenCount;
                    outputTokens = usageContent.Details.OutputTokenCount;
                }
                yield return update;
            }
        }
        finally
        {
            bool known = inputTokens.HasValue && outputTokens.HasValue;
            if (known)
            {
                _broker.AddUsage(inputTokens!.Value, outputTokens!.Value);
            }
            else
            {
                Interlocked.Increment(ref _unknownUsageCalls);
            }
            _broker.SetUsageState(Volatile.Read(ref _unknownUsageCalls) == 0 ? "known" : "unknown");
            try
            {
                ModelCallResponse settlement = await WorkerProtocol.RequestModelAsync(
                    "model.settle", _request, callId, _request.MaxOutputTokens,
                    inputTokens, outputTokens, known ? "known" : "unknown",
                    cancellationToken.IsCancellationRequested ? CancellationToken.None : cancellationToken);
                if (!settlement.Allowed)
                {
                    throw new WorkerFault("model_settlement_rejected");
                }
            }
            catch
            {
                throw new WorkerFault("model_settlement_failed");
            }
        }
    }
}

internal sealed class RuntimeFunction : AIFunction
{
    private readonly JsonDocument _schema;
    private readonly Func<AIFunctionArguments, CancellationToken, ValueTask<object?>> _invoke;

    public RuntimeFunction(
        string name,
        string description,
        string jsonSchema,
        Func<AIFunctionArguments, CancellationToken, ValueTask<object?>> invoke)
    {
        Name = name;
        Description = description;
        _schema = JsonDocument.Parse(jsonSchema);
        _invoke = invoke;
    }

    public override string Name { get; }
    public override string Description { get; }
    public override JsonElement JsonSchema => _schema.RootElement;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        => _invoke(arguments, cancellationToken);
}

internal static class WorkerArguments
{
    public static string GetRequiredString(AIFunctionArguments arguments, string name)
    {
        if (!arguments.TryGetValue(name, out object? value))
        {
            throw new WorkerFault("invalid_publish_arguments");
        }
        string? text = value switch
        {
            string stringValue => stringValue,
            JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString(),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new WorkerFault("invalid_publish_arguments");
        }
        return text;
    }

    public static long MeasureCharacters(IEnumerable<ChatMessage> messages)
    {
        long count = 0;
        foreach (ChatMessage message in messages)
        {
            count += message.Text?.Length ?? 0;
            foreach (AIContent content in message.Contents)
            {
                count += content switch
                {
                    TextContent text => text.Text?.Length ?? 0,
                    FunctionCallContent call => JsonSerializer.Serialize(call.Arguments).Length,
                    FunctionResultContent result => JsonSerializer.Serialize(result.Result).Length,
                    _ => 0,
                };
                if (count > int.MaxValue)
                {
                    return count;
                }
            }
        }
        return count;
    }

    public static bool HasIdentity(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256;
}

internal static class WorkerProtocol
{
    private static readonly Stream Input = Console.OpenStandardInput();
    private static readonly Stream Output = Console.OpenStandardOutput();
    private static readonly SemaphoreSlim OutputLock = new(1, 1);
    private static readonly SemaphoreSlim InputLock = new(1, 1);

    public static async Task<byte[]> ReadLineAsync(CancellationToken cancellationToken)
    {
        await InputLock.WaitAsync(cancellationToken);
        try
        {
            using var buffer = new MemoryStream();
            byte[] oneByte = new byte[1];
            while (true)
            {
                int read = await Input.ReadAsync(oneByte.AsMemory(), cancellationToken);
                if (read == 0)
                {
                    if (buffer.Length == 0)
                    {
                        throw new EndOfStreamException();
                    }
                    return buffer.ToArray();
                }
                if (oneByte[0] == (byte)'\n')
                {
                    if (buffer.Length > 0 && buffer.GetBuffer()[buffer.Length - 1] == (byte)'\r')
                    {
                        buffer.SetLength(buffer.Length - 1);
                    }
                    return buffer.ToArray();
                }
                if (buffer.Length >= Protocol.MaxMessageBytes)
                {
                    throw new WorkerFault("protocol_message_too_large");
                }
                buffer.WriteByte(oneByte[0]);
            }
        }
        finally
        {
            InputLock.Release();
        }
    }

    public static async Task<WorkerEnvelope> RequestHostAsync<TParams>(string id, string method, TParams parameters, CancellationToken cancellationToken)
    {
        await WriteAsync(new WorkerEnvelope(
            "2.0", id, method, null, JsonSerializer.SerializeToElement(parameters, ProgramProtocolJson.Options), null, null), cancellationToken);
        WorkerEnvelope response = await ReadEnvelopeAsync(cancellationToken);
        ValidateResponse(response, id);
        if (response.Error is not null)
        {
            throw new WorkerFault("host_tool_rejected");
        }
        return response;
    }

    public static async Task<ModelCallResponse> RequestModelAsync(
        string method,
        WorkerRequest request,
        string callId,
        int maxOutputTokens,
        long? inputTokens,
        long? outputTokens,
        string usageState,
        CancellationToken cancellationToken)
    {
        var parameters = new ModelCallRequest(
            request.ProtocolVersion, request.RunId, request.AttemptId, request.HostEpoch, request.FencingToken,
            callId, maxOutputTokens, inputTokens, outputTokens, usageState);
        WorkerEnvelope response = await RequestHostAsync(callId, method, parameters, cancellationToken);
        if (response.ModelResult is not ModelCallResponse result || result.CallId != callId)
        {
            throw new WorkerFault("model_host_response_invalid");
        }
        return result;
    }

    public static async Task WriteAsync(WorkerEnvelope envelope, CancellationToken cancellationToken)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, ProgramProtocolJson.Options);
        if (bytes.Length > Protocol.MaxMessageBytes)
        {
            throw new WorkerFault("protocol_message_too_large");
        }
        await WriteBytesAsync(bytes, cancellationToken);
    }

    public static async Task WriteBytesAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        if (bytes.Length > Protocol.MaxMessageBytes)
        {
            throw new WorkerFault("protocol_message_too_large");
        }
        await OutputLock.WaitAsync(cancellationToken);
        try
        {
            await Output.WriteAsync(bytes, cancellationToken);
            await Output.WriteAsync("\n"u8.ToArray(), cancellationToken);
            await Output.FlushAsync(cancellationToken);
        }
        finally
        {
            OutputLock.Release();
        }
    }

    private static async Task<WorkerEnvelope> ReadEnvelopeAsync(CancellationToken cancellationToken)
    {
        byte[] line = await ReadLineAsync(cancellationToken);
        return JsonSerializer.Deserialize<WorkerEnvelope>(line, ProgramProtocolJson.Options)
            ?? throw new WorkerFault("host_response_invalid");
    }

    private static void ValidateResponse(WorkerEnvelope response, string expectedId)
    {
        if (response.Jsonrpc != "2.0" || response.Id != expectedId)
        {
            throw new WorkerFault("host_response_invalid");
        }
        if (response.Method is not null)
        {
            throw new WorkerFault("host_response_invalid");
        }
    }
}

internal static class ProgramProtocolJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

internal sealed class WorkerFault(string code) : Exception
{
    public string Code { get; } = code;
}
