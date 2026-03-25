using System.Text.Json;
using System.Text.RegularExpressions;

namespace AnswerCode.Services.Analysis;

public class ConfigLookupService(IWorkspaceFileService workspaceFileService) : IConfigLookupService
{
    public Task<ConfigLookupResult> LookupConfigKeyAsync(string rootPath,
                                                          string key,
                                                          CancellationToken cancellationToken = default)
    {
        var languages = DetectLanguages(rootPath);
        if (languages.Count == 0)
        {
            return Task.FromResult(new ConfigLookupResult(key,
                                                          [],
                                                          [],
                                                          [],
                                                          null,
                                                          "No supported source files found in the project."));
        }

        var allConfigFiles = new List<ConfigFileInfo>();

        foreach (var lang in languages)
        {
            allConfigFiles.AddRange(DiscoverConfigFiles(rootPath, lang));
        }

        // Deduplicate by absolute path (multiple languages may share .env)
        allConfigFiles = allConfigFiles.GroupBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderBy(f => f.Precedence).First()).ToList();

        if (allConfigFiles.Count == 0)
        {
            return Task.FromResult(new ConfigLookupResult(key,
                                                          [.. languages],
                                                          [],
                                                          [],
                                                          null,
                                                          $"No config files found for detected languages: {string.Join(", ", languages)}"));
        }

        var allEntries = new List<ConfigEntry>();
        foreach (var file in allConfigFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var entries = SearchConfigFile(file, key);
                allEntries.AddRange(entries);
            }
            catch
            {
                // Skip files that cannot be read or parsed
            }
        }

        var effective = allEntries.Count > 0
            ? allEntries.OrderBy(e => e.Precedence).First()
            : null;

        return Task.FromResult(new ConfigLookupResult(key,
                                                      languages.ToList(),
                                                      allConfigFiles,
                                                      [.. allEntries.OrderBy(e => e.Precedence)],
                                                      effective));
    }

    // ── Language Detection ──────────────────────────────────────────────────

    private HashSet<string> DetectLanguages(string rootPath)
    {
        var sourceFiles = workspaceFileService.EnumerateSupportedSourceFiles(rootPath);
        var languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in sourceFiles)
        {
            var lang = workspaceFileService.GetLanguageId(file);
            if (lang != null)
            {
                languages.Add(lang);
            }
        }

        // Collapse c/cpp into a single group for config discovery
        if (languages.Contains("c") || languages.Contains("cpp"))
        {
            languages.Add("c_cpp");
        }

        // JS and TS share config files
        if (languages.Contains("javascript") || languages.Contains("typescript"))
        {
            languages.Add("jsts");
        }

        return languages;
    }

    // ── Config File Discovery ──────────────────────────────────────────────

    private List<ConfigFileInfo> DiscoverConfigFiles(string rootPath, string language)
    {
        return language switch
        {
            "csharp" => DiscoverCSharpConfigs(rootPath),
            "jsts" => DiscoverJsTsConfigs(rootPath),
            "python" => DiscoverPythonConfigs(rootPath),
            "java" => DiscoverJavaConfigs(rootPath),
            "go" => DiscoverGoConfigs(rootPath),
            "rust" => DiscoverRustConfigs(rootPath),
            "c_cpp" => DiscoverCCppConfigs(rootPath),
            _ => []
        };
    }

    private List<ConfigFileInfo> DiscoverCSharpConfigs(string rootPath)
    {
        var results = new List<ConfigFileInfo>();

        // appsettings.json (lowest precedence among JSON configs)
        AddIfExists(results, rootPath, rootPath, "appsettings.json", "json", "csharp", 4);

        // appsettings.{Environment}.json
        try
        {
            foreach (var file in Directory.GetFiles(rootPath, "appsettings.*.json"))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Local overrides get highest file precedence
                int precedence = fileName.Contains("Local", StringComparison.OrdinalIgnoreCase) ? 1 : 3;
                results.Add(MakeConfigFileInfo(file, rootPath, "json", "csharp", precedence));
            }
        }
        catch { /* directory access error */ }

        // launchSettings.json (environment variables defined there, informational)
        AddIfExists(results, rootPath, Path.Combine(rootPath, "Properties"), "launchSettings.json", "json", "csharp", 5);

        return results;
    }

    private List<ConfigFileInfo> DiscoverJsTsConfigs(string rootPath)
    {
        var results = new List<ConfigFileInfo>();

        AddIfExists(results, rootPath, rootPath, ".env", "env", "javascript", 4);
        AddIfExists(results, rootPath, rootPath, ".env.local", "env", "javascript", 2);

        // .env.{environment} files
        foreach (var envName in new[] { "development", "production", "staging", "test" })
        {
            AddIfExists(results, rootPath, rootPath, $".env.{envName}", "env", "javascript", 3);
            AddIfExists(results, rootPath, rootPath, $".env.{envName}.local", "env", "javascript", 1);
        }

        return results;
    }

    private List<ConfigFileInfo> DiscoverPythonConfigs(string rootPath)
    {
        var results = new List<ConfigFileInfo>();

        AddIfExists(results, rootPath, rootPath, ".env", "env", "python", 3);

        // settings.py / config.py at root or common subdirectories
        foreach (var name in new[] { "settings.py", "config.py" })
        {
            AddIfExists(results, rootPath, rootPath, name, "python", "python", 2);
        }

        // config.yaml / config.yml
        foreach (var name in new[] { "config.yaml", "config.yml" })
        {
            AddIfExists(results, rootPath, rootPath, name, "yaml", "python", 2);
        }

        AddIfExists(results, rootPath, rootPath, "config.ini", "ini", "python", 2);
        AddIfExists(results, rootPath, rootPath, "pyproject.toml", "toml", "python", 4);

        return results;
    }

    private List<ConfigFileInfo> DiscoverJavaConfigs(string rootPath)
    {
        var results = new List<ConfigFileInfo>();

        // Check both root and src/main/resources/
        var searchDirs = new List<string> { rootPath };
        var resourcesDir = Path.Combine(rootPath, "src", "main", "resources");
        if (Directory.Exists(resourcesDir))
        {
            searchDirs.Add(resourcesDir);
        }

        foreach (var dir in searchDirs)
        {
            // Base config
            AddIfExists(results, rootPath, dir, "application.properties", "properties", "java", 3);
            AddIfExists(results, rootPath, dir, "application.yml", "yaml", "java", 3);
            AddIfExists(results, rootPath, dir, "application.yaml", "yaml", "java", 3);

            // Profile-specific
            try
            {
                foreach (var pattern in new[] { "application-*.properties", "application-*.yml", "application-*.yaml" })
                {
                    foreach (var file in Directory.GetFiles(dir, pattern))
                    {
                        var ext = Path.GetExtension(file).TrimStart('.');
                        var format = ext is "yml" or "yaml" ? "yaml" : "properties";
                        results.Add(MakeConfigFileInfo(file, rootPath, format, "java", 2));
                    }
                }
            }
            catch { /* directory access error */ }

            AddIfExists(results, rootPath, dir, "bootstrap.yml", "yaml", "java", 4);
            AddIfExists(results, rootPath, dir, "bootstrap.yaml", "yaml", "java", 4);
            AddIfExists(results, rootPath, dir, "bootstrap.properties", "properties", "java", 4);
        }

        return results;
    }

    private List<ConfigFileInfo> DiscoverGoConfigs(string rootPath)
    {
        var results = new List<ConfigFileInfo>();

        foreach (var name in new[] { "config.yaml", "config.yml" })
        {
            AddIfExists(results, rootPath, rootPath, name, "yaml", "go", 2);
        }

        AddIfExists(results, rootPath, rootPath, "config.toml", "toml", "go", 2);
        AddIfExists(results, rootPath, rootPath, "config.json", "json", "go", 2);
        AddIfExists(results, rootPath, rootPath, ".env", "env", "go", 3);

        // Check configs/ subdirectory
        var configDir = Path.Combine(rootPath, "configs");
        if (!Directory.Exists(configDir))
        {
            configDir = Path.Combine(rootPath, "config");
        }

        if (Directory.Exists(configDir))
        {
            try
            {
                foreach (var file in Directory.GetFiles(configDir))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    var format = ext switch
                    {
                        ".yaml" or ".yml" => "yaml",
                        ".toml" => "toml",
                        ".json" => "json",
                        ".env" => "env",
                        _ => (string?)null
                    };

                    if (format != null)
                    {
                        results.Add(MakeConfigFileInfo(file, rootPath, format, "go", 2));
                    }
                }
            }
            catch { /* directory access error */ }
        }

        return results;
    }

    private List<ConfigFileInfo> DiscoverRustConfigs(string rootPath)
    {
        var results = new List<ConfigFileInfo>();

        AddIfExists(results, rootPath, rootPath, ".env", "env", "rust", 3);
        var configDir = Path.Combine(rootPath, "config");

        if (Directory.Exists(configDir))
        {
            AddIfExists(results, rootPath, configDir, "default.toml", "toml", "rust", 3);
            AddIfExists(results, rootPath, configDir, "local.toml", "toml", "rust", 1);

            try
            {
                foreach (var file in Directory.GetFiles(configDir, "*.toml"))
                {
                    var fileName = Path.GetFileName(file);

                    if (fileName.Equals("default.toml", StringComparison.OrdinalIgnoreCase)
                        || fileName.Equals("local.toml", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    results.Add(MakeConfigFileInfo(file, rootPath, "toml", "rust", 2));
                }
            }
            catch { /* directory access error */ }
        }

        // Settings.toml at root
        AddIfExists(results, rootPath, rootPath, "Settings.toml", "toml", "rust", 2);

        return results;
    }

    private List<ConfigFileInfo> DiscoverCCppConfigs(string rootPath)
    {
        var results = new List<ConfigFileInfo>();

        // config.h and similar generated headers
        try
        {
            foreach (var file in Directory.GetFiles(rootPath, "*.h"))
            {
                var fileName = Path.GetFileName(file);

                if (fileName.Contains("config", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(MakeConfigFileInfo(file, rootPath, "c-define", "c_cpp", 2));
                }
            }
        }
        catch { /* directory access error */ }

        AddIfExists(results, rootPath, rootPath, "CMakeLists.txt", "c-define", "c_cpp", 3);

        // config/ subdirectory
        var configDir = Path.Combine(rootPath, "config");

        if (Directory.Exists(configDir))
        {
            try
            {
                foreach (var file in Directory.GetFiles(configDir))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    var format = ext switch
                    {
                        ".ini" => "ini",
                        ".json" => "json",
                        ".xml" => "xml",
                        ".yaml" or ".yml" => "yaml",
                        _ => (string?)null
                    };

                    if (format != null)
                    {
                        results.Add(MakeConfigFileInfo(file, rootPath, format, "c_cpp", 3));
                    }
                }
            }
            catch { /* directory access error */ }
        }

        return results;
    }

    // ── Config File Searching ──────────────────────────────────────────────

    private static List<ConfigEntry> SearchConfigFile(ConfigFileInfo file, string key)
    {
        if (!File.Exists(file.FilePath))
        {
            return [];
        }

        return file.Format switch
        {
            "json" => SearchJsonConfig(file, key),
            "env" => SearchEnvFile(file, key),
            "properties" => SearchPropertiesFile(file, key),
            "yaml" => SearchYamlFile(file, key),
            "toml" => SearchTomlFile(file, key),
            "ini" => SearchIniFile(file, key),
            "python" => SearchPythonConfig(file, key),
            "c-define" => SearchCDefineConfig(file, key),
            _ => []
        };
    }

    // ── JSON Parser ────────────────────────────────────────────────────────

    private static List<ConfigEntry> SearchJsonConfig(ConfigFileInfo file, string key)
    {
        var text = File.ReadAllText(file.FilePath);
        var lines = File.ReadAllLines(file.FilePath);

        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(text);
        }
        catch
        {
            return [];
        }

        var results = new List<ConfigEntry>();
        SearchJsonElement(root, "", key, file, lines, results);
        return results;
    }

    private static void SearchJsonElement(JsonElement element,
                                          string prefix,
                                          string key,
                                          ConfigFileInfo file,
                                          string[] lines,
                                          List<ConfigEntry> results)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                var fullKey = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}:{prop.Name}";

                if (prop.Value.ValueKind == JsonValueKind.Object
                    || prop.Value.ValueKind == JsonValueKind.Array)
                {
                    // Check if the full path so far matches the search key
                    if (fullKey.Contains(key, StringComparison.OrdinalIgnoreCase))
                    {
                        var lineNum = FindLineNumber(lines, prop.Name);
                        results.Add(new ConfigEntry(fullKey,
                                                    FormatJsonValue(prop.Value),
                                                    file.FilePath,
                                                    file.RelativePath,
                                                    lineNum,
                                                    file.Format,
                                                    file.Precedence));
                    }

                    SearchJsonElement(prop.Value,
                                      fullKey,
                                      key,
                                      file,
                                      lines,
                                      results);
                }
                else
                {
                    if (fullKey.Contains(key, StringComparison.OrdinalIgnoreCase))
                    {
                        var lineNum = FindLineNumber(lines, prop.Name);
                        results.Add(new ConfigEntry(fullKey,
                                                    FormatJsonValue(prop.Value),
                                                    file.FilePath,
                                                    file.RelativePath,
                                                    lineNum,
                                                    file.Format,
                                                    file.Precedence));
                    }
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            for (int i = 0; i < element.GetArrayLength(); i++)
            {
                SearchJsonElement(element[i], $"{prefix}[{i}]", key, file, lines, results);
            }
        }
    }

    private static string FormatJsonValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            JsonValueKind.Array => $"[{value.GetArrayLength()} items]",
            JsonValueKind.Object => "{...}",
            _ => value.GetRawText()
        };
    }

    private static int FindLineNumber(string[] lines, string propertyName)
    {
        var escaped = Regex.Escape(propertyName);
        var pattern = $@"""{escaped}""";

        // If there's a parent path, try to narrow down the search area
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1; // 1-based
            }
        }

        return -1;
    }

    // ── .env Parser ────────────────────────────────────────────────────────

    private static List<ConfigEntry> SearchEnvFile(ConfigFileInfo file, string key)
    {
        var results = new List<ConfigEntry>();
        var lines = File.ReadAllLines(file.FilePath);

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
            {
                continue;
            }

            var eqIndex = line.IndexOf('=');
            if (eqIndex <= 0)
            {
                continue;
            }

            var envKey = line[..eqIndex].Trim();
            var envValue = line[(eqIndex + 1)..].Trim();

            // Strip surrounding quotes
            if ((envValue.StartsWith('"') && envValue.EndsWith('"'))
                || (envValue.StartsWith('\'') && envValue.EndsWith('\'')))
            {
                envValue = envValue[1..^1];
            }

            if (envKey.Contains(key, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new ConfigEntry(envKey,
                                            envValue,
                                            file.FilePath,
                                            file.RelativePath,
                                            i + 1,
                                            file.Format,
                                            file.Precedence));
            }
        }

        return results;
    }

    // ── Properties Parser (Java) ───────────────────────────────────────────

    private static List<ConfigEntry> SearchPropertiesFile(ConfigFileInfo file, string key)
    {
        var results = new List<ConfigEntry>();
        var lines = File.ReadAllLines(file.FilePath);

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#') || line.StartsWith('!'))
            {
                continue;
            }

            // Properties can use = or : as separator
            var sepIndex = line.IndexOfAny(['=', ':']);
            if (sepIndex <= 0)
            {
                continue;
            }

            var propKey = line[..sepIndex].Trim();
            var propValue = line[(sepIndex + 1)..].Trim();

            if (propKey.Contains(key, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new ConfigEntry(propKey,
                                            propValue,
                                            file.FilePath,
                                            file.RelativePath,
                                            i + 1,
                                            file.Format,
                                            file.Precedence));
            }
        }

        return results;
    }

    // ── YAML Parser (line-based) ───────────────────────────────────────────

    private static List<ConfigEntry> SearchYamlFile(ConfigFileInfo file, string key)
    {
        var results = new List<ConfigEntry>();
        var lines = File.ReadAllLines(file.FilePath);
        var pathStack = new List<(int Indent, string Key)>();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#') || trimmed.StartsWith("---") || trimmed.StartsWith("..."))
            {
                continue;
            }

            var indent = line.Length - trimmed.Length;

            // Pop stack entries that are at the same or deeper indent
            while (pathStack.Count > 0 && pathStack[^1].Indent >= indent)
            {
                pathStack.RemoveAt(pathStack.Count - 1);
            }

            var colonIndex = trimmed.IndexOf(':');
            if (colonIndex <= 0)
            {
                continue;
            }

            var yamlKey = trimmed[..colonIndex].Trim().Trim('"', '\'');
            var yamlValue = trimmed[(colonIndex + 1)..].Trim();

            // Build full dotted path
            var fullPath = string.Join(".", pathStack.Select(p => p.Key).Append(yamlKey));

            if (string.IsNullOrEmpty(yamlValue) || yamlValue.StartsWith('#'))
            {
                // This is a parent key (value on next indented lines)
                pathStack.Add((indent, yamlKey));

                if (fullPath.Contains(key, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new ConfigEntry(fullPath,
                                                "(section)",
                                                file.FilePath,
                                                file.RelativePath,
                                                i + 1,
                                                file.Format,
                                                file.Precedence));
                }
            }
            else
            {
                // Strip inline comments
                var commentIndex = yamlValue.IndexOf(" #");
                if (commentIndex > 0)
                {
                    yamlValue = yamlValue[..commentIndex].Trim();
                }

                // Strip quotes
                if ((yamlValue.StartsWith('"') && yamlValue.EndsWith('"'))
                    || (yamlValue.StartsWith('\'') && yamlValue.EndsWith('\'')))
                {
                    yamlValue = yamlValue[1..^1];
                }

                pathStack.Add((indent, yamlKey));

                if (fullPath.Contains(key, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new ConfigEntry(fullPath,
                                                yamlValue,
                                                file.FilePath,
                                                file.RelativePath,
                                                i + 1,
                                                file.Format,
                                                file.Precedence));
                }
            }
        }

        return results;
    }

    // ── TOML Parser (line-based) ───────────────────────────────────────────

    private static List<ConfigEntry> SearchTomlFile(ConfigFileInfo file, string key)
    {
        var results = new List<ConfigEntry>();
        var lines = File.ReadAllLines(file.FilePath);
        var currentSection = "";

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
            {
                continue;
            }

            // Section header: [section] or [[array]]
            var sectionMatch = Regex.Match(line, @"^\[{1,2}\s*([^\]]+?)\s*\]{1,2}$");
            if (sectionMatch.Success)
            {
                currentSection = sectionMatch.Groups[1].Value;
                continue;
            }

            // Key = value
            var kvMatch = Regex.Match(line, @"^(\S+)\s*=\s*(.+)$");
            if (!kvMatch.Success)
            {
                continue;
            }

            var tomlKey = kvMatch.Groups[1].Value.Trim();
            var tomlValue = kvMatch.Groups[2].Value.Trim();

            var fullKey = string.IsNullOrEmpty(currentSection) ? tomlKey : $"{currentSection}.{tomlKey}";

            // Strip quotes from value
            if ((tomlValue.StartsWith('"') && tomlValue.EndsWith('"'))
                || (tomlValue.StartsWith('\'') && tomlValue.EndsWith('\'')))
            {
                tomlValue = tomlValue[1..^1];
            }

            if (fullKey.Contains(key, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new ConfigEntry(fullKey,
                                            tomlValue,
                                            file.FilePath,
                                            file.RelativePath,
                                            i + 1,
                                            file.Format,
                                            file.Precedence));
            }
        }

        return results;
    }

    // ── INI Parser ─────────────────────────────────────────────────────────

    private static List<ConfigEntry> SearchIniFile(ConfigFileInfo file, string key)
    {
        var results = new List<ConfigEntry>();
        var lines = File.ReadAllLines(file.FilePath);
        var currentSection = "";

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }

            // Section header
            var sectionMatch = Regex.Match(line, @"^\[(.+)\]$");
            if (sectionMatch.Success)
            {
                currentSection = sectionMatch.Groups[1].Value;
                continue;
            }

            var eqIndex = line.IndexOf('=');
            if (eqIndex <= 0)
            {
                continue;
            }

            var iniKey = line[..eqIndex].Trim();
            var iniValue = line[(eqIndex + 1)..].Trim();
            var fullKey = string.IsNullOrEmpty(currentSection) ? iniKey : $"{currentSection}:{iniKey}";

            if (fullKey.Contains(key, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new ConfigEntry(fullKey,
                                            iniValue,
                                            file.FilePath,
                                            file.RelativePath,
                                            i + 1,
                                            file.Format,
                                            file.Precedence));
            }
        }

        return results;
    }

    // ── Python Config Parser ───────────────────────────────────────────────

    private static readonly Regex _pythonAssignmentRegex = new(@"^\s*([A-Za-z_]\w*)\s*=\s*(.+)$", RegexOptions.Compiled);

    private static List<ConfigEntry> SearchPythonConfig(ConfigFileInfo file, string key)
    {
        var results = new List<ConfigEntry>();
        var lines = File.ReadAllLines(file.FilePath);

        for (int i = 0; i < lines.Length; i++)
        {
            var match = _pythonAssignmentRegex.Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            var pyKey = match.Groups[1].Value;
            var pyValue = match.Groups[2].Value.Trim();

            // Strip quotes
            if ((pyValue.StartsWith('"') && pyValue.EndsWith('"'))
                || (pyValue.StartsWith('\'') && pyValue.EndsWith('\'')))
            {
                pyValue = pyValue[1..^1];
            }

            if (pyKey.Contains(key, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new ConfigEntry(pyKey,
                                            pyValue,
                                            file.FilePath,
                                            file.RelativePath,
                                            i + 1,
                                            file.Format,
                                            file.Precedence));
            }
        }

        return results;
    }

    // ── C/C++ #define Parser ───────────────────────────────────────────────

    private static readonly Regex _cDefineRegex = new(@"^\s*#\s*define\s+(\w+)\s+(.+)$", RegexOptions.Compiled);

    private static readonly Regex _cMakeSetRegex = new(@"^\s*set\s*\(\s*(\w+)\s+(.+?)\s*\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex _cMakeOptionRegex = new(@"^\s*option\s*\(\s*(\w+)\s+""[^""]*""\s+(\w+)\s*\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static List<ConfigEntry> SearchCDefineConfig(ConfigFileInfo file, string key)
    {
        var results = new List<ConfigEntry>();
        var lines = File.ReadAllLines(file.FilePath);
        var fileName = Path.GetFileName(file.FilePath);
        var isCMake = fileName.Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase);

        for (int i = 0; i < lines.Length; i++)
        {
            Match match;

            if (isCMake)
            {
                match = _cMakeSetRegex.Match(lines[i]);
                if (!match.Success)
                {
                    match = _cMakeOptionRegex.Match(lines[i]);
                }
            }
            else
            {
                match = _cDefineRegex.Match(lines[i]);
            }

            if (!match.Success)
            {
                continue;
            }

            var defineKey = match.Groups[1].Value;
            var defineValue = match.Groups[2].Value.Trim();

            // Strip trailing comments for #define
            if (!isCMake)
            {
                var commentIdx = defineValue.IndexOf("//");
                if (commentIdx >= 0)
                {
                    defineValue = defineValue[..commentIdx].Trim();
                }

                var blockCommentIdx = defineValue.IndexOf("/*");
                if (blockCommentIdx >= 0)
                {
                    defineValue = defineValue[..blockCommentIdx].Trim();
                }
            }

            if (defineKey.Contains(key, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new ConfigEntry(defineKey,
                                            defineValue,
                                            file.FilePath,
                                            file.RelativePath,
                                            i + 1,
                                            file.Format,
                                            file.Precedence));
            }
        }

        return results;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private void AddIfExists(List<ConfigFileInfo> results,
                             string rootPath,
                             string directory,
                             string fileName,
                             string format,
                             string language,
                             int precedence)
    {
        var path = Path.Combine(directory, fileName);

        if (File.Exists(path))
        {
            results.Add(MakeConfigFileInfo(path, rootPath, format, language, precedence));
        }
    }

    private static ConfigFileInfo MakeConfigFileInfo(string absolutePath,
                                                     string rootPath,
                                                     string format,
                                                     string language,
                                                     int precedence)
    {
        var relativePath = Path.GetRelativePath(rootPath, absolutePath);
        return new ConfigFileInfo(Path.GetFullPath(absolutePath), relativePath, format, language, precedence);
    }
}
