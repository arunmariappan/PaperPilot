using PaperPilot.Core.Contracts;

namespace PaperPilot.Web.Chat;

public enum ChatMode
{
    /// <summary><c>/stream</c>: the answer appears token by token.</summary>
    Classic,

    /// <summary><c>/ask-agentic</c>: scope check, grading and query rewriting, then the whole answer at once.</summary>
    Agentic,
}

/// <summary>The question form, with the Gradio app's defaults.</summary>
public sealed class ChatSettings
{
    public const int MaxQuestionLength = 1000;

    public string Question { get; set; } = string.Empty;

    public ChatMode Mode { get; set; } = ChatMode.Classic;

    /// <summary>Chunks to retrieve, 1–10.</summary>
    public int TopK { get; set; } = 3;

    public bool UseHybrid { get; set; } = true;

    /// <summary>Empty means the API's default model.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Comma-separated arXiv categories, e.g. <c>cs.AI, cs.LG</c>.</summary>
    public string Categories { get; set; } = string.Empty;

    public AskRequest ToRequest() => new()
    {
        Query = Question.Trim(),
        TopK = TopK,
        UseHybrid = UseHybrid,
        Model = string.IsNullOrWhiteSpace(Model) ? null : Model,
        Categories = ParseCategories(Categories),
    };

    /// <summary>Fills the form from an example, keeping the mode.</summary>
    public void Apply(ExampleQuestion example, string defaultModel)
    {
        ArgumentNullException.ThrowIfNull(example);

        Question = example.Question;
        TopK = example.TopK;
        UseHybrid = example.UseHybrid;
        Categories = example.Categories;
        Model = defaultModel;
    }

    /// <summary><c>"cs.AI, , cs.LG "</c> → <c>["cs.AI", "cs.LG"]</c>; null when there are none, meaning every category.</summary>
    public static IReadOnlyList<string>? ParseCategories(string? text)
    {
        var categories = (text ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return categories.Length > 0 ? categories : null;
    }
}

/// <summary>One of the examples under the form. They use the configured model, not a hard-coded one.</summary>
public sealed record ExampleQuestion(string Question, int TopK, bool UseHybrid, string Categories)
{
    /// <summary>The Gradio app's examples.</summary>
    public static IReadOnlyList<ExampleQuestion> All { get; } =
    [
        new("What are transformers in machine learning?", 3, true, "cs.AI, cs.LG"),
        new("How do convolutional neural networks work?", 5, true, "cs.CV, cs.LG"),
        new("What is attention mechanism in deep learning?", 4, false, "cs.AI"),
        new("Explain reinforcement learning algorithms", 3, true, "cs.LG, cs.AI"),
        new("What are the latest developments in NLP?", 5, true, "cs.CL"),
    ];
}
