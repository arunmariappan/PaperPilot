using System.Text;
using System.Text.RegularExpressions;
using PaperPilot.Rag.Prompts;
using PaperPilot.Rag.Retrieval;

namespace PaperPilot.Rag.Agentic;

/// <summary>
/// The agent's prompts, embedded verbatim from Python's <c>services/agents/prompts.py</c>. Placeholders are filled in
/// one pass, so braces or placeholder names inside a question or a paper can't change the prompt.
/// </summary>
internal static partial class AgentPrompts
{
    private static readonly string GuardrailTemplate = RagPromptBuilder.LoadPrompt("guardrail.txt");
    private static readonly string GradeDocumentsTemplate = RagPromptBuilder.LoadPrompt("grade_documents.txt");
    private static readonly string RewriteTemplate = RagPromptBuilder.LoadPrompt("rewrite.txt");
    private static readonly string GenerateAnswerTemplate = RagPromptBuilder.LoadPrompt("generate_answer.txt");

    public static string Guardrail(string question) => Fill(GuardrailTemplate, question, context: string.Empty);

    public static string GradeDocuments(string question, string context) => Fill(GradeDocumentsTemplate, question, context);

    public static string Rewrite(string question) => Fill(RewriteTemplate, question, context: string.Empty);

    public static string GenerateAnswer(string question, string context) => Fill(GenerateAnswerTemplate, question, context);

    private static string Fill(string template, string question, string context) =>
        Placeholder().Replace(template, match => match.Groups[1].Value == "question" ? question : context);

    [GeneratedRegex(@"\{(question|context)\}")]
    private static partial Regex Placeholder();
}

/// <summary>Formats retrieved chunks as the context of the grading and answer prompts.</summary>
internal static class AgentContextFormatter
{
    /// <summary>
    /// Numbered excerpts, <c>[n] arXiv:{id} — {title}</c> and the chunk text, separated by blank lines (B4: Python
    /// passed the repr of a list of LangChain documents). Empty when nothing was retrieved.
    /// </summary>
    public static string Format(RetrievalResult retrieval)
    {
        ArgumentNullException.ThrowIfNull(retrieval);

        var context = new StringBuilder();
        for (var i = 0; i < retrieval.Chunks.Count; i++)
        {
            var chunk = retrieval.Chunks[i];
            if (i > 0)
            {
                context.Append("\n\n");
            }

            context.Append('[').Append(i + 1).Append("] arXiv:").Append(chunk.ArxivId).Append(" — ").Append(chunk.Title).Append('\n');
            context.Append(chunk.ChunkText ?? chunk.Abstract ?? string.Empty);
        }

        return context.ToString();
    }
}
