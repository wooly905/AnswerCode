namespace AnswerCode.Models;

/// <summary>
/// Project-related configuration.
/// Binds to the "QASourceCodePath" section in appsettings.json.
/// </summary>
public class ProjectSettings
{
    public const string SectionName = "QASourceCodePath";

    /// <summary>
    /// Default project path for code exploration.
    /// Can be a relative path (resolved from the application base directory) or an absolute path.
    /// </summary>
    public string DefaultPath { get; set; } = "project-code";
}
