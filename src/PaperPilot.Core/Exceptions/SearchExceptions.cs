namespace PaperPilot.Core.Exceptions;

/// <summary>OpenSearch can't be reached or isn't healthy. Endpoints map this to 503 (B6).</summary>
public sealed class SearchUnavailableException : Exception
{
    public SearchUnavailableException()
    {
    }

    public SearchUnavailableException(string message) : base(message)
    {
    }

    public SearchUnavailableException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>OpenSearch rejected a request (4xx/5xx). Carries OpenSearch's <c>error.reason</c>.</summary>
public sealed class SearchQueryException : Exception
{
    public SearchQueryException()
    {
    }

    public SearchQueryException(string message) : base(message)
    {
    }

    public SearchQueryException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public SearchQueryException(int statusCode, string reason) : base($"OpenSearch returned {statusCode}: {reason}")
    {
        StatusCode = statusCode;
        Reason = reason;
    }

    public int StatusCode { get; }

    public string? Reason { get; }
}

/// <summary>
/// Embeddings couldn't be produced: no API key, Jina failed or timed out, or the response was malformed.
/// Query callers fall back to BM25; indexing fails the paper.
/// </summary>
public sealed class EmbeddingUnavailableException : Exception
{
    public EmbeddingUnavailableException()
    {
    }

    public EmbeddingUnavailableException(string message) : base(message)
    {
    }

    public EmbeddingUnavailableException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
