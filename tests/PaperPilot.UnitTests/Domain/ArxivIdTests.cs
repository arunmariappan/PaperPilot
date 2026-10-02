using PaperPilot.Core.Domain;

namespace PaperPilot.UnitTests.Domain;

public sealed class ArxivIdTests
{
    [Theory]
    [InlineData("2510.01234v2", "2510.01234")]
    [InlineData("2510.01234v12", "2510.01234")]
    [InlineData("2510.01234", "2510.01234")]
    [InlineData("hep-th/9901001v3", "hep-th/9901001")]
    [InlineData("hep-th/9901001", "hep-th/9901001")]
    // Python's split("v")[0] turned these into "sol" (B8).
    [InlineData("solv-int/9901001v1", "solv-int/9901001")]
    [InlineData("solv-int/9901001", "solv-int/9901001")]
    public void StripVersion_removes_only_a_trailing_version(string arxivId, string expected) =>
        ArxivId.StripVersion(arxivId).ShouldBe(expected);

    [Theory]
    [InlineData("2510.01234v1", "https://arxiv.org/pdf/2510.01234.pdf")]
    [InlineData("solv-int/9901001v1", "https://arxiv.org/pdf/solv-int/9901001.pdf")]
    public void ToPdfUrl_builds_the_unversioned_pdf_url(string arxivId, string expected) =>
        ArxivId.ToPdfUrl(arxivId).ShouldBe(expected);

    [Theory]
    [InlineData("2510.01234v1", "https://arxiv.org/abs/2510.01234")]
    [InlineData("solv-int/9901001", "https://arxiv.org/abs/solv-int/9901001")]
    public void ToAbsUrl_builds_the_unversioned_abstract_url(string arxivId, string expected) =>
        ArxivId.ToAbsUrl(arxivId).ShouldBe(expected);
}
