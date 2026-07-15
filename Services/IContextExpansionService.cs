namespace AnswerCode.Services;

/// <summary>
/// Deterministically expands the initial agent context by detecting symbol-like identifiers
/// in the user's question and pre-fetching verified definition/call-graph/reference evidence,
/// so the tool-calling loop can start with evidence already in hand.
/// </summary>
public interface IContextExpansionService
{
    /// <summary>
    /// Build a pre-fetched context block for symbols detected in <paramref name="question"/>.
    /// Returns null when no candidate symbol could be verified against the codebase, or on failure.
    /// </summary>
    Task<string?> BuildSymbolContextAsync(string rootPath, string question, CancellationToken cancellationToken = default);
}
