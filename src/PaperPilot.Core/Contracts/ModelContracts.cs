namespace PaperPilot.Core.Contracts;

/// <summary>Response from <c>/models</c> (N1): the installed Ollama models, for the UI's model dropdown.</summary>
public sealed record ModelsResponse(string DefaultModel, IReadOnlyList<string> Models);
