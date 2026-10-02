namespace PaperPilot.Core.Exceptions;

/// <summary>The arXiv API failed, timed out or returned XML that can't be parsed.</summary>
public sealed class ArxivApiException : Exception
{
    public ArxivApiException()
    {
    }

    public ArxivApiException(string message)
        : base(message)
    {
    }

    public ArxivApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A PDF couldn't be downloaded, even after retries.</summary>
public sealed class PdfDownloadException : Exception
{
    public PdfDownloadException()
    {
    }

    public PdfDownloadException(string message)
        : base(message)
    {
    }

    public PdfDownloadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A PDF is invalid (empty, not a PDF, unreadable) or docling-serve couldn't convert it.</summary>
public sealed class PdfParsingException : Exception
{
    public PdfParsingException()
    {
    }

    public PdfParsingException(string message)
        : base(message)
    {
    }

    public PdfParsingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>An ingestion run finished without doing anything useful (B15), or a setup check failed.</summary>
public sealed class IngestionFailedException : Exception
{
    public IngestionFailedException()
    {
    }

    public IngestionFailedException(string message)
        : base(message)
    {
    }

    public IngestionFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
