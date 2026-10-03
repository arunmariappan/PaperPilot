using System.Diagnostics;
using Microsoft.AspNetCore.Components;
using PaperPilot.Core.Contracts;
using PaperPilot.Web.Api;
using PaperPilot.Web.Chat;

namespace PaperPilot.Web.Components.Pages;

/// <summary>
/// The chat page: one question at a time, as in the Gradio app, with earlier answers from this session kept in memory.
/// Classic mode streams from <c>/stream</c>; agentic mode waits for <c>/ask-agentic</c>.
/// </summary>
public sealed partial class Chat : IDisposable
{
    /// <summary>Tokens arrive faster than it's worth re-rendering, so the answer is redrawn at most this often.</summary>
    internal static readonly TimeSpan RenderInterval = TimeSpan.FromMilliseconds(50);

    private readonly ChatSettings _settings = new();
    private readonly List<ChatTurn> _history = [];
    private readonly CancellationTokenSource _closing = new();
    private ModelChoices _models = ModelChoices.ServerDefault;
    private ChatTurn? _current;
    private CancellationTokenSource? _request;
    private string? _formMessage;
    private bool _feedbackOff;

    [Inject]
    private PaperPilotApiClient Api { get; set; } = default!;

    [Inject]
    private ILogger<Chat> Logger { get; set; } = default!;

    private bool Busy => _request is not null;

    public void Dispose()
    {
        // The circuit is gone: stop any answer still being generated for it.
        _closing.Cancel();
        _closing.Dispose();
    }

    protected override async Task OnInitializedAsync()
    {
        try
        {
            _models = ModelChoices.From(await Api.GetModelsAsync(_closing.Token));
            if (_settings.Model.Length == 0)
            {
                _settings.Model = _models.DefaultModel;
            }
        }
        catch (ApiException ex)
        {
            // The dropdown keeps "Server default", which leaves the choice to the API.
            LogModelsUnavailable(Logger, ex.Message);
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
        }
    }

    private async Task AskAsync()
    {
        if (Busy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.Question))
        {
            _formMessage = "Please enter a question.";
            return;
        }

        _formMessage = null;
        if (_current is not null)
        {
            _history.Insert(0, _current);
        }

        var turn = _current = new ChatTurn(_settings.Question.Trim(), _settings.Mode);
        var request = _settings.ToRequest();

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        using var ticking = new CancellationTokenSource();
        _request = stop;
        var ticker = TickAsync(ticking.Token);
        try
        {
            if (turn.Mode == ChatMode.Agentic)
            {
                turn.Complete(await Api.AskAgenticAsync(request, stop.Token));
            }
            else
            {
                await StreamAsync(turn, request, stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            turn.Stop();
        }
        catch (ApiException ex)
        {
            turn.Fail(ex.Message);
        }
        finally
        {
            _request = null;
            await ticking.CancelAsync();
        }

        await ticker;
    }

    private async Task StreamAsync(ChatTurn turn, AskRequest request, CancellationToken cancellationToken)
    {
        var lastRender = Stopwatch.GetTimestamp();
        await foreach (var streamEvent in Api.StreamAskAsync(request, cancellationToken))
        {
            switch (streamEvent)
            {
                case StreamMetadata metadata:
                    turn.SetSearch(metadata.Sources, metadata.ChunksUsed, metadata.SearchMode);
                    break;
                case StreamChunk chunk:
                    turn.Append(chunk.Text);
                    break;
                case StreamDone done:
                    turn.Complete(done.Answer, done.Sources);
                    break;
                case StreamError error:
                    turn.Fail(error.Message);
                    break;
            }

            if (streamEvent is not StreamChunk || Stopwatch.GetElapsedTime(lastRender) >= RenderInterval)
            {
                lastRender = Stopwatch.GetTimestamp();
                StateHasChanged();
            }
        }

        if (turn.Status == TurnStatus.Running)
        {
            turn.Fail("The answer stream ended before the answer was complete.");
        }
    }

    /// <summary>Redraws once a second while answering, for the elapsed time and any token not yet shown.</summary>
    private async Task TickAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                StateHasChanged();
            }
        }
        catch (OperationCanceledException)
        {
            // The answer is finished.
        }
    }

    private void Stop() => _request?.Cancel();

    private void UseExample(ExampleQuestion example)
    {
        _settings.Apply(example, _models.DefaultModel);
        _formMessage = null;
    }

    private void FeedbackOff() => _feedbackOff = true;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cannot list models, using the API's default: {Reason}")]
    private static partial void LogModelsUnavailable(ILogger logger, string reason);
}
