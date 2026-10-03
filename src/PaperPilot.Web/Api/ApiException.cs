using System.Net;

namespace PaperPilot.Web.Api;

/// <summary>A call to the PaperPilot API failed. The message is meant for the user.</summary>
public sealed class ApiException : Exception
{
    public ApiException()
    {
    }

    public ApiException(string message) : base(message)
    {
    }

    public ApiException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public ApiException(string message, HttpStatusCode? statusCode, Exception? innerException = null)
        : base(message, innerException) => StatusCode = statusCode;

    /// <summary>The API's status code, or null when it couldn't be reached.</summary>
    public HttpStatusCode? StatusCode { get; }
}
