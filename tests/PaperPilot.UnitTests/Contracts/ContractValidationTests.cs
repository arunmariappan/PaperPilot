using System.ComponentModel.DataAnnotations;
using PaperPilot.Core.Contracts;

namespace PaperPilot.UnitTests.Contracts;

public sealed class ContractValidationTests
{
    [Fact]
    public void A_normal_question_is_valid() =>
        IsValid(new AskRequest { Query = "What is attention?" }).ShouldBeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("   ")] // C13: Python let this through to /ask and /stream.
    [InlineData("\t\n")]
    public void Blank_questions_are_rejected(string query) =>
        IsValid(new AskRequest { Query = query }).ShouldBeFalse();

    [Fact]
    public void Questions_over_1000_characters_are_rejected()
    {
        IsValid(new AskRequest { Query = new string('a', 1000) }).ShouldBeTrue();
        IsValid(new AskRequest { Query = new string('a', 1001) }).ShouldBeFalse();
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    public void TopK_must_be_between_1_and_10(int topK, bool valid) =>
        IsValid(new AskRequest { Query = "q", TopK = topK }).ShouldBe(valid);

    [Theory]
    [InlineData("   ", true)] // A blank search lists the latest papers.
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Search_queries_may_be_blank_but_not_empty(string? query, bool valid) =>
        IsValid(new HybridSearchRequest { Query = query! }).ShouldBe(valid);

    [Fact]
    public void Search_paging_and_score_limits_are_enforced()
    {
        IsValid(new HybridSearchRequest { Query = "q", Size = 100 }).ShouldBeTrue();
        IsValid(new HybridSearchRequest { Query = "q", Size = 101 }).ShouldBeFalse();
        IsValid(new HybridSearchRequest { Query = "q", From = -1 }).ShouldBeFalse();
        IsValid(new HybridSearchRequest { Query = "q", MinScore = -0.1 }).ShouldBeFalse();
    }

    [Fact]
    public void Feedback_needs_a_trace_id_and_a_score_between_minus_one_and_one()
    {
        IsValid(new FeedbackRequest { TraceId = "t", Score = -1 }).ShouldBeTrue();
        IsValid(new FeedbackRequest { TraceId = "t", Score = 1.5 }).ShouldBeFalse();
        IsValid(new FeedbackRequest { TraceId = "t", Score = null }).ShouldBeFalse();
        IsValid(new FeedbackRequest { TraceId = "", Score = 1 }).ShouldBeFalse();
        IsValid(new FeedbackRequest { TraceId = "t", Score = 1, Comment = new string('a', 1001) }).ShouldBeFalse();
    }

    private static bool IsValid(object instance) =>
        Validator.TryValidateObject(instance, new ValidationContext(instance), [], validateAllProperties: true);
}
