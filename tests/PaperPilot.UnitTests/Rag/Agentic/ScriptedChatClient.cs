using Microsoft.Extensions.AI;

namespace PaperPilot.UnitTests.Rag.Agentic;

/// <summary>The agent's four LLM calls, told apart by the first line of their prompt.</summary>
internal enum AgentCall
{
    Guardrail,
    Grade,
    Rewrite,
    Generate,
}

/// <summary>
/// Answers each <see cref="AgentCall"/> from a script: replies are used in order and the last one repeats. An
/// <see cref="Exception"/> in the script is thrown instead. By default every question is in scope, every retrieval is
/// relevant, and the answer is <see cref="Answer"/>.
/// </summary>
internal sealed class ScriptedChatClient : IChatClient
{
    public const string Answer = "Transformers use self-attention [arXiv:2610.00001v1].";

    private readonly Dictionary<AgentCall, List<object>> _script = new()
    {
        [AgentCall.Guardrail] = ["""{"score": 90, "reason": "About transformer architectures."}"""],
        [AgentCall.Grade] = ["""{"binary_score": "yes", "reasoning": "The excerpts discuss attention."}"""],
        [AgentCall.Rewrite] = ["""{"rewritten_query": "transformer self-attention architecture", "reasoning": "More specific."}"""],
        [AgentCall.Generate] = [Answer],
    };

    private readonly Dictionary<AgentCall, int> _used = [];

    public List<(AgentCall Call, string Prompt, ChatOptions? Options)> Calls { get; } = [];

    /// <summary>Replaces the script for <paramref name="call"/>: each reply is a string or an exception to throw.</summary>
    public ScriptedChatClient Script(AgentCall call, params object[] replies)
    {
        _script[call] = [.. replies];
        return this;
    }

    public IEnumerable<string> Prompts(AgentCall call) => Calls.Where(c => c.Call == call).Select(c => c.Prompt);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var prompt = messages.Last().Text;
        var call = Classify(prompt);
        object reply;
        lock (Calls)
        {
            Calls.Add((call, prompt, options));
            var used = _used.GetValueOrDefault(call);
            _used[call] = used + 1;
            reply = _script[call][Math.Min(used, _script[call].Count - 1)];
        }

        return reply is Exception failure
            ? Task.FromException<ChatResponse>(failure)
            : Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, (string)reply)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The agent doesn't stream.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private static AgentCall Classify(string prompt) => prompt[..prompt.IndexOf('\n', StringComparison.Ordinal)] switch
    {
        var line when line.StartsWith("You are a guardrail evaluator", StringComparison.Ordinal) => AgentCall.Guardrail,
        var line when line.StartsWith("You are a grader", StringComparison.Ordinal) => AgentCall.Grade,
        var line when line.StartsWith("You are a question re-writer", StringComparison.Ordinal) => AgentCall.Rewrite,
        var line when line.StartsWith("You are an AI research assistant", StringComparison.Ordinal) => AgentCall.Generate,
        var line => throw new InvalidOperationException($"Unexpected prompt: {line}"),
    };
}
