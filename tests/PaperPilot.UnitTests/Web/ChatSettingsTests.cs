using PaperPilot.Web.Chat;

namespace PaperPilot.UnitTests.Web;

public sealed class ChatSettingsTests
{
    [Theory]
    [InlineData(" cs.AI, , cs.LG ", new[] { "cs.AI", "cs.LG" })]
    [InlineData("cs.CL", new[] { "cs.CL" })]
    [InlineData("", null)]
    [InlineData(" , ,", null)]
    [InlineData(null, null)]
    public void Categories_are_split_trimmed_and_empties_dropped(string? text, string[]? expected) =>
        ChatSettings.ParseCategories(text).ShouldBe(expected);

    [Fact]
    public void The_defaults_match_the_gradio_app()
    {
        var settings = new ChatSettings();

        (settings.TopK, settings.UseHybrid, settings.Mode, settings.Model, settings.Categories)
            .ShouldBe((3, true, ChatMode.Classic, "", ""));
    }

    [Fact]
    public void The_request_has_the_trimmed_question_and_leaves_a_blank_model_to_the_api()
    {
        var settings = new ChatSettings { Question = "  What is BERT?\n", TopK = 5, UseHybrid = false, Categories = "cs.CL, " };

        var request = settings.ToRequest();

        request.Query.ShouldBe("What is BERT?");
        (request.TopK, request.UseHybrid, request.Model).ShouldBe((5, false, null));
        request.Categories.ShouldBe(["cs.CL"]);
        settings.Model = "qwen3.5:9b";
        settings.ToRequest().Model.ShouldBe("qwen3.5:9b");
    }

    [Fact]
    public void An_example_fills_the_form_with_the_default_model_and_keeps_the_mode()
    {
        var settings = new ChatSettings { Mode = ChatMode.Agentic, Model = "other:1b" };

        settings.Apply(ExampleQuestion.All[2], "qwen3.5:9b");

        settings.Question.ShouldBe("What is attention mechanism in deep learning?");
        (settings.TopK, settings.UseHybrid, settings.Categories, settings.Model, settings.Mode)
            .ShouldBe((4, false, "cs.AI", "qwen3.5:9b", ChatMode.Agentic));
    }

    [Fact]
    public void The_model_list_starts_with_the_default_without_duplicates()
    {
        var choices = ModelChoices.From(new("qwen3.5:9b", ["llama3.2:1b", "qwen3.5:9b"]));

        choices.Models.ShouldBe(["qwen3.5:9b", "llama3.2:1b"]);
        ModelChoices.ServerDefault.Models.Select(ModelChoices.Label).ShouldBe(["Server default"]);
    }
}
