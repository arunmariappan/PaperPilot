using System.Text;
using Microsoft.Extensions.AI;
using PaperPilot.Core.Search;

namespace PaperPilot.Rag.Prompts;

/// <summary>A RAG prompt: <c>rag_system.txt</c> as the system message, context and question as the user message (C3).</summary>
public sealed record RagPrompt(string System, string User)
{
    /// <summary>Both parts as one prompt, the way Python sent it to <c>/api/generate</c>.</summary>
    public string Combined => System + "\n\n" + User;

    public IList<ChatMessage> ToMessages() => [new(ChatRole.System, System), new(ChatRole.User, User)];
}

/// <summary>Port of Python's <c>RAGPromptBuilder.create_rag_prompt</c>.</summary>
public static class RagPromptBuilder
{
    /// <summary><c>rag_system.txt</c>, trimmed as Python's <c>read_text().strip()</c> does.</summary>
    public static string SystemPrompt { get; } = LoadPrompt("rag_system.txt");

    /// <summary>
    /// <see cref="RagPrompt.Combined"/> equals Python's prompt for the same chunks: each chunk is listed as
    /// <c>[{n}. arXiv:{id}]</c> plus its text (or the paper abstract when the chunk has no text).
    /// </summary>
    public static RagPrompt Build(string query, IReadOnlyList<ChunkHit> chunks)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(chunks);

        var user = new StringBuilder("### Context from Papers:\n\n");
        for (var i = 0; i < chunks.Count; i++)
        {
            user.Append('[').Append(i + 1).Append(". arXiv:").Append(chunks[i].ArxivId).Append("]\n");
            user.Append(chunks[i].ChunkText ?? chunks[i].Abstract ?? string.Empty).Append("\n\n");
        }

        user.Append("### Question:\n").Append(query).Append("\n\n");
        user.Append("### Answer:\nProvide a natural, conversational response (not JSON) and cite sources using [arXiv:id] format.\n\n");

        return new RagPrompt(SystemPrompt, user.ToString());
    }

    internal static string LoadPrompt(string fileName)
    {
        var resource = $"{typeof(RagPromptBuilder).Namespace}.{fileName}";
        using var stream = typeof(RagPromptBuilder).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded prompt '{resource}' is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().ReplaceLineEndings("\n").Trim();
    }
}
