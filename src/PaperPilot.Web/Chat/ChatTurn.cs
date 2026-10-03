using System.Diagnostics;
using System.Text;
using PaperPilot.Core.Contracts;

namespace PaperPilot.Web.Chat;

public enum TurnStatus
{
    Running,
    Answered,
    Stopped,
    Failed,
}

/// <summary>One question and its answer, filled in as the answer arrives.</summary>
public sealed class ChatTurn(string question, ChatMode mode)
{
    private readonly StringBuilder _answer = new();
    private readonly long _started = Stopwatch.GetTimestamp();
    private TimeSpan? _elapsed;

    public string Question { get; } = question;

    public ChatMode Mode { get; } = mode;

    public TurnStatus Status { get; private set; } = TurnStatus.Running;

    public string Answer => _answer.ToString();

    public bool HasAnswer => _answer.Length > 0;

    /// <summary>PDF URLs of the papers the answer draws on.</summary>
    public IReadOnlyList<string> Sources { get; private set; } = [];

    /// <summary>Null until the search has finished.</summary>
    public int? ChunksUsed { get; private set; }

    public string? SearchMode { get; private set; }

    public IReadOnlyList<string> ReasoningSteps { get; private set; } = [];

    public int? RetrievalAttempts { get; private set; }

    /// <summary>For <c>/feedback</c>; agentic answers only.</summary>
    public string? TraceId { get; private set; }

    public string? Error { get; private set; }

    /// <summary>The score sent to <c>/feedback</c>: 1 or −1.</summary>
    public int? Feedback { get; set; }

    public TimeSpan Elapsed => _elapsed ?? Stopwatch.GetElapsedTime(_started);

    public void SetSearch(IReadOnlyList<string> sources, int chunksUsed, string searchMode)
    {
        Sources = sources;
        ChunksUsed = chunksUsed;
        SearchMode = searchMode;
    }

    public void Append(string text) => _answer.Append(text);

    /// <summary>The final answer replaces what was streamed, as the Gradio app did with <c>done</c>.</summary>
    public void Complete(string answer, IReadOnlyList<string>? sources = null)
    {
        _answer.Clear().Append(answer);
        if (sources is not null)
        {
            Sources = sources;
            ChunksUsed ??= sources.Count;
        }

        End(TurnStatus.Answered);
    }

    public void Complete(AgenticAskResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        SetSearch(response.Sources, response.ChunksUsed, response.SearchMode);
        ReasoningSteps = response.ReasoningSteps;
        RetrievalAttempts = response.RetrievalAttempts;
        TraceId = response.TraceId;
        Complete(response.Answer);
    }

    public void Fail(string error)
    {
        Error = error;
        End(TurnStatus.Failed);
    }

    public void Stop() => End(TurnStatus.Stopped);

    private void End(TurnStatus status)
    {
        if (Status != TurnStatus.Running)
        {
            return;
        }

        Status = status;
        _elapsed = Stopwatch.GetElapsedTime(_started);
    }
}
