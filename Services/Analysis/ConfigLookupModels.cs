namespace AnswerCode.Services.Analysis;

/// <summary>
/// A single config value found in one config source file.
/// </summary>
public sealed record ConfigEntry(string Key,
                                 string Value,
                                 string FilePath,
                                 string RelativePath,
                                 int LineNumber,
                                 string Format,
                                 int Precedence);

/// <summary>
/// A config file discovered in the project.
/// </summary>
public sealed record ConfigFileInfo(string FilePath,
                                    string RelativePath,
                                    string Format,
                                    string Language,
                                    int Precedence);

/// <summary>
/// Result of a config key lookup across all config sources.
/// </summary>
public sealed record ConfigLookupResult(string Query,
                                        IReadOnlyList<string> DetectedLanguages,
                                        IReadOnlyList<ConfigFileInfo> ConfigFiles,
                                        IReadOnlyList<ConfigEntry> Entries,
                                        ConfigEntry? EffectiveEntry,
                                        string? Message = null);
