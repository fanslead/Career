using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace P0.Worker;

internal static class Program
{
    private static readonly JsonSerializerOptions ProtocolJson = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions MafJson = AIJsonUtilities.DefaultOptions;

    public static async Task<int> Main()
    {
        try
        {
            string? line = await Console.In.ReadLineAsync();
            if (line is null)
            {
                throw new InvalidOperationException("Expected one JSON-RPC request on stdin.");
            }

            using JsonDocument requestDocument = JsonDocument.Parse(line);
            JsonElement request = requestDocument.RootElement;
            if (request.GetProperty("jsonrpc").GetString() != "2.0" ||
                request.GetProperty("id").GetString() != "run" ||
                request.GetProperty("method").GetString() != "agent.run")
            {
                throw new InvalidOperationException("Expected JSON-RPC 2.0 request id 'run' with method 'agent.run'.");
            }
            JsonElement parameters = request.GetProperty("params");
            string runId = parameters.GetProperty("runId").GetString() ?? throw new InvalidOperationException("params.runId is required.");
            string prompt = parameters.GetProperty("prompt").GetString() ?? string.Empty;
            string scenario = parameters.TryGetProperty("scenario", out JsonElement scenarioValue) ? scenarioValue.GetString() ?? "normal" : "normal";
            if (scenario is not ("normal" or "invalid-arguments" or "endless"))
            {
                throw new InvalidOperationException($"Unsupported scenario '{scenario}'.");
            }

            var model = new ScriptedChatClient(scenario);
            (IList<AITool> tools, ToolDispatcher dispatcher) = CreateTools(scenario, runId,
                parameters.TryGetProperty("operationId", out JsonElement operationIdValue) ? operationIdValue.GetString() ?? "p0-record-note" : "p0-record-note");
            var functionClient = new FunctionInvokingChatClient(model)
            {
                MaximumIterationsPerRequest = 6,
                MaximumConsecutiveErrorsPerRequest = 1,
                IncludeDetailedErrors = true,
            };
            var agent = new ChatClientAgent(functionClient, new ChatClientAgentOptions
            {
                Name = "p0-worker",
                Description = "Deterministic MAF tool loop and approval probe.",
                ChatOptions = new ChatOptions { Tools = tools },
            });

            JsonElement? serializedSession = GetNullableElement(parameters, "session");
            AgentSession session = serializedSession is null
                ? await agent.CreateSessionAsync()
                : await agent.DeserializeSessionAsync(serializedSession.Value);

            AgentResponse response;
            bool? approvalDecision = null;
            JsonElement? approvalElement = GetNullableElement(parameters, "approval");
            if (approvalElement is not null)
            {
                if (!parameters.TryGetProperty("approved", out JsonElement approvedElement) || approvedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw new InvalidOperationException("params.approved must be a boolean when params.approval is provided.");
                }

                approvalDecision = approvedElement.GetBoolean();
                ToolApprovalRequestContent approval = JsonSerializer.Deserialize<ToolApprovalRequestContent>(approvalElement.Value, MafJson)
                    ?? throw new InvalidOperationException("Could not deserialize the native MAF approval request.");
                if (approval.ToolCall is not FunctionCallContent functionCall || functionCall.Name != "record_note")
                {
                    throw new InvalidOperationException("Only the pending record_note approval is supported by this probe.");
                }

                ChatMessage approvalMessage = new(ChatRole.User, [approval.CreateResponse(approvalDecision.Value, "P0 host decision")]);
                response = await agent.RunAsync(approvalMessage, session);
            }
            else
            {
                response = await agent.RunAsync(prompt, session);
            }

            if (dispatcher.Failure is Exception toolFailure)
            {
                throw new InvalidOperationException("Host tool.execute failed; refusing to return a successful agent.run result.", toolFailure);
            }

            ToolApprovalRequestContent? pendingApproval = response.Messages
                .SelectMany(message => message.Contents)
                .OfType<ToolApprovalRequestContent>()
                .FirstOrDefault();
            FunctionCallContent? unresolvedCall = pendingApproval is null
                ? response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().FirstOrDefault(call => !call.InformationalOnly)
                : null;
            if (unresolvedCall is not null)
            {
                throw new InvalidOperationException($"MAF returned unresolved tool call '{unresolvedCall.Name}' after the configured six-iteration loop cap.");
            }
            string status = pendingApproval is not null
                ? "WaitingApproval"
                : approvalDecision == false ? "Denied" : "Succeeded";

            object result = new
            {
                status,
                session = await agent.SerializeSessionAsync(session),
                approval = pendingApproval is null ? (JsonElement?)null : JsonSerializer.SerializeToElement(pendingApproval, MafJson),
                callId = pendingApproval?.ToolCall is FunctionCallContent pendingCall ? pendingCall.CallId : null,
                toolName = pendingApproval?.ToolCall is FunctionCallContent pendingTool ? pendingTool.Name : null,
                arguments = pendingApproval?.ToolCall is FunctionCallContent pendingArguments ? pendingArguments.Arguments : null,
                text = response.Text,
                modelCalls = model.CallCount,
                toolCalls = dispatcher.InvocationCount,
                workerPid = Environment.ProcessId,
            };

            WriteResponse("run", result);
            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"P0.Worker failed: {ex}");
            return 1;
        }
    }

    private static (IList<AITool> Tools, ToolDispatcher Dispatcher) CreateTools(string scenario, string runId, string operationId)
    {
        var dispatcher = new ToolDispatcher(runId, operationId);
        AIFunction add = new RuntimeFunction(
            "add",
            "Adds two integer values.",
            """{"type":"object","properties":{"a":{"type":"integer"},"b":{"type":"integer"}},"required":["a","b"],"additionalProperties":false}""",
            (arguments, _) =>
            {
                dispatcher.CountInvocation();
                int a = GetRequiredInt(arguments, "a");
                int b = GetRequiredInt(arguments, "b");
                return ValueTask.FromResult<object?>(a + b);
            });
        AIFunction recordNote = new RuntimeFunction(
            "record_note",
            "Ask the host to record a note. The host owns the side effect and approval policy.",
            """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"],"additionalProperties":false}""",
            async (arguments, cancellationToken) =>
            {
                dispatcher.CountInvocation();
                string text = GetRequiredString(arguments, "text");
                return await dispatcher.RecordNoteAsync(text, cancellationToken);
            });

        return ([add, new ApprovalRequiredAIFunction(recordNote)], dispatcher);
    }

    private static int GetRequiredInt(AIFunctionArguments arguments, string name)
    {
        if (!arguments.TryGetValue(name, out object? value) || !TryInt(value, out int parsed))
        {
            throw new ArgumentException($"Tool argument '{name}' must be an integer.");
        }
        return parsed;
    }

    private static bool TryInt(object? value, out int parsed)
    {
        switch (value)
        {
            case int intValue:
                parsed = intValue;
                return true;
            case long longValue when longValue is >= int.MinValue and <= int.MaxValue:
                parsed = (int)longValue;
                return true;
            case JsonElement json when json.ValueKind == JsonValueKind.Number && json.TryGetInt32(out parsed):
                return true;
            default:
                parsed = default;
                return false;
        }
    }

    private static string GetRequiredString(AIFunctionArguments arguments, string name)
    {
        if (!arguments.TryGetValue(name, out object? value))
        {
            throw new ArgumentException($"Tool argument '{name}' must be a non-empty string.");
        }

        string? text = value switch
        {
            string stringValue => stringValue,
            JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString(),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException($"Tool argument '{name}' must be a non-empty string.");
        }
        return text;
    }

    private static JsonElement? GetNullableElement(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return value.Clone();
    }

    private static void WriteResponse(string id, object result)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }, ProtocolJson));
        Console.Out.Flush();
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

internal sealed class ToolDispatcher(string runId, string operationId)
{
    private int _invocationCount;
    private Exception? _failure;
    public int InvocationCount => Volatile.Read(ref _invocationCount);
    public Exception? Failure => Volatile.Read(ref _failure);
    public void CountInvocation() => Interlocked.Increment(ref _invocationCount);

    public async ValueTask<object?> RecordNoteAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            return await SendToHostAsync(text, cancellationToken);
        }
        catch (Exception exception)
        {
            MarkFailure(exception);
            throw;
        }
    }

    private async ValueTask<object?> SendToHostAsync(string text, CancellationToken cancellationToken)
    {
        var request = new
        {
            jsonrpc = "2.0",
            id = "tool-1",
            method = "tool.execute",
            @params = new { runId, operationId, name = "record_note", arguments = new { text } },
        };
        Console.Out.WriteLine(JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Console.Out.Flush();

        string? line = await Console.In.ReadLineAsync(cancellationToken);
        if (line is null)
        {
            throw new EndOfStreamException("Host closed stdin before responding to tool.execute.");
        }

        using JsonDocument response = JsonDocument.Parse(line);
        JsonElement root = response.RootElement;
        if (!root.TryGetProperty("jsonrpc", out JsonElement protocol) || protocol.GetString() != "2.0")
        {
            throw new InvalidDataException("Host tool.execute response must use JSON-RPC 2.0.");
        }
        if (!root.TryGetProperty("id", out JsonElement responseId) || responseId.GetString() != "tool-1")
        {
            throw new InvalidDataException("Host tool.execute response id must be 'tool-1'.");
        }
        if (root.TryGetProperty("error", out JsonElement error))
        {
            var exception = new InvalidOperationException($"Host rejected tool.execute: {error.GetRawText()}");
            Interlocked.CompareExchange(ref _failure, exception, null);
            throw exception;
        }

        if (!root.TryGetProperty("result", out JsonElement result) ||
            !result.TryGetProperty("receiptId", out JsonElement receiptIdValue) ||
            !result.TryGetProperty("text", out JsonElement resultTextValue))
        {
            var exception = new InvalidDataException("Host tool.execute response must include result.receiptId and result.text.");
            Interlocked.CompareExchange(ref _failure, exception, null);
            throw exception;
        }

        string receiptId = receiptIdValue.GetString() ?? throw MarkFailure(new InvalidDataException("Host receiptId must be a string."));
        string resultText = resultTextValue.GetString() ?? throw MarkFailure(new InvalidDataException("Host result.text must be a string."));
        return new { receiptId, text = resultText };
    }

    private Exception MarkFailure(Exception exception)
    {
        Interlocked.CompareExchange(ref _failure, exception, null);
        return exception;
    }
}

internal sealed class ScriptedChatClient(string scenario) : IChatClient
{
    private int _callCount;
    public int CallCount => Volatile.Read(ref _callCount);

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int call = Interlocked.Increment(ref _callCount);
        ChatMessage[] history = messages.ToArray();
        string prompt = history.FirstOrDefault(message => message.Role == ChatRole.User && !string.IsNullOrWhiteSpace(message.Text))?.Text ?? "";
        FunctionResultContent? noteResult = history.SelectMany(message => message.Contents).OfType<FunctionResultContent>().LastOrDefault(content => content.CallId.StartsWith("call-note", StringComparison.Ordinal));

        ChatMessage response;
        if (history.SelectMany(message => message.Contents).OfType<ToolApprovalResponseContent>().Any(content => !content.Approved))
        {
            response = new ChatMessage(ChatRole.Assistant, "The requested action was denied.");
        }
        else if (scenario == "endless")
        {
            response = FunctionCall($"call-endless-{call}", "add", new Dictionary<string, object?> { ["a"] = 1, ["b"] = 1 });
        }
        else if (noteResult is not null)
        {
            string toolText = noteResult.Result?.ToString() ?? "unknown tool result";
            response = scenario == "invalid-arguments" && toolText.StartsWith("Error: Function failed.", StringComparison.Ordinal)
                ? new ChatMessage(ChatRole.Assistant, "The malformed record_note arguments were rejected; no host note was written.")
                : new ChatMessage(ChatRole.Assistant, $"Completed the requested note. The record_note tool result was: {toolText}");
        }
        else if (history.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Any())
        {
            Dictionary<string, object?> arguments = scenario == "invalid-arguments"
                ? new Dictionary<string, object?> { ["unexpected"] = "missing required text" }
                : new Dictionary<string, object?> { ["text"] = prompt };
            response = FunctionCall("call-note-1", "record_note", arguments);
        }
        else
        {
            response = FunctionCall("call-add-1", "add", new Dictionary<string, object?> { ["a"] = 2, ["b"] = 3 });
        }

        return Task.FromResult(new ChatResponse(response)
        {
            ModelId = "p0-scripted-model",
            FinishReason = response.Contents.Any(content => content is FunctionCallContent) ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop,
        });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (ChatResponseUpdate update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private static ChatMessage FunctionCall(string callId, string name, IDictionary<string, object?> arguments)
        => new(ChatRole.Assistant, [new FunctionCallContent(callId, name, arguments)]);
}
