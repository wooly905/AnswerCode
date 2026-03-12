using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace AnswerCode.Services;

public class RepoMapService : IRepoMapService
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<RepoMapService> _logger;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", ".vs", ".vscode", ".idea",
        "node_modules", "bin", "obj", "packages", "dist", "build", "out", "target",
        "__pycache__", ".pytest_cache", ".mypy_cache", "venv", "env",
        "vendor", "bower_components", ".nuget", ".angular", "coverage"
    };

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".vb", ".fs",
        ".js", ".jsx", ".ts", ".tsx", ".mjs", ".cjs",
        ".py",
        ".java", ".kt", ".scala",
        ".go", ".rs",
        ".c", ".cpp", ".cc", ".cxx", ".h", ".hpp", ".hxx",
        ".rb", ".php", ".swift"
    };

    // ── Role inference rules ──────────────────────────────────────────────

    private static readonly (string[] Patterns, string Role)[] RoleRules =
    [
        (["Controllers", "Endpoints", "Routes", "Api", "Handlers"], "API Layer"),
        (["Services", "Application", "UseCases", "Commands", "Queries"], "Business Logic"),
        (["Models", "Entities", "Domain", "DTOs", "Dtos", "ViewModels"], "Domain Layer"),
        (["Data", "Repositories", "Repository", "Infrastructure", "Persistence", "DAL"], "Data Layer"),
        (["wwwroot", "Views", "Pages", "Components", "UI", "Frontend", "ClientApp"], "UI Layer"),
        (["Tests", "Test", "Specs", "Spec", "__tests__", "UnitTests", "IntegrationTests"], "Tests"),
        (["Middleware", "Filters", "Interceptors", "Pipeline"], "Cross-Cutting"),
        (["Config", "Configuration", "Settings", "Options"], "Configuration"),
        (["Migrations", "Seeds", "Seeders"], "Database Migrations"),
        (["Providers", "Adapters", "Clients", "External"], "External Integration"),
        (["Analysis", "Analyzers"], "Analysis"),
        (["Tools", "Utilities", "Utils", "Helpers", "Common", "Shared", "Lib"], "Utilities"),
    ];

    // ── Entry point file patterns ─────────────────────────────────────────

    private static readonly string[] EntryPointFiles =
    [
        "Program.cs", "Startup.cs", "Main.cs",
        "main.go", "main.py", "app.py", "manage.py",
        "index.ts", "index.js", "main.ts", "main.js", "app.ts", "app.js", "server.ts", "server.js",
        "Main.java", "App.java", "Application.java",
        "main.rs", "lib.rs",
    ];

    // ── Import parsing regexes ────────────────────────────────────────────

    private static readonly Regex CsUsingRegex = new(@"^\s*using\s+(?!static)([\w.]+)\s*;", RegexOptions.Compiled);
    private static readonly Regex TsImportRegex = new(@"^\s*import\s+.*?\s+from\s+['""]([^'""]+)['""]", RegexOptions.Compiled);
    private static readonly Regex TsRequireRegex = new(@"require\s*\(\s*['""]([^'""]+)['""]\s*\)", RegexOptions.Compiled);
    private static readonly Regex PyImportRegex = new(@"^\s*(?:from\s+([\w.]+)\s+import|import\s+([\w.]+))", RegexOptions.Compiled);
    private static readonly Regex JavaImportRegex = new(@"^\s*import\s+(?:static\s+)?([\w.]+)\s*;", RegexOptions.Compiled);
    private static readonly Regex GoImportRegex = new(@"^\s*""([^""]+)""", RegexOptions.Compiled);

    public RepoMapService(IMemoryCache cache, ILogger<RepoMapService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task<string> BuildRepoMapAsync(string rootPath, string? scope = null, int maxDepth = 3, bool includeDependencies = true)
    {
        var effectiveRoot = rootPath;
        if (!string.IsNullOrWhiteSpace(scope))
        {
            effectiveRoot = Path.GetFullPath(Path.Combine(rootPath, scope));
            if (!Directory.Exists(effectiveRoot))
            {
                return $"Error: Scope directory not found: {scope}";
            }
        }

        var cacheKey = $"repo_map:{effectiveRoot}:{maxDepth}:{includeDependencies}";
        if (_cache.TryGetValue(cacheKey, out string? cached) && cached is not null)
        {
            return cached;
        }

        var result = await Task.Run(() => BuildMap(effectiveRoot, rootPath, maxDepth, includeDependencies));

        _cache.Set(cacheKey, result, CacheDuration);
        return result;
    }

    // ── Core map building ─────────────────────────────────────────────────

    private string BuildMap(string effectiveRoot, string rootPath, int maxDepth, bool includeDependencies)
    {
        var projectName = Path.GetFileName(rootPath);
        var projectType = DetectProjectType(effectiveRoot);

        // 1. Discover modules
        var modules = DiscoverModules(effectiveRoot, rootPath, maxDepth);

        if (modules.Count == 0)
        {
            return $"Repository Map: {projectName}\nType: {projectType}\n\nNo meaningful modules detected.";
        }

        // 2. Detect entry points
        var entryPoints = DetectEntryPoints(effectiveRoot, rootPath);

        // 3. Analyze cross-module dependencies
        Dictionary<string, Dictionary<string, int>>? edges = null;
        if (includeDependencies)
        {
            edges = AnalyzeCrossModuleDependencies(modules, effectiveRoot, rootPath);
        }

        // 4. Format output
        return FormatRepoMap(projectName, projectType, modules, entryPoints, edges);
    }

    // ── Project type detection (reuses ProjectOverviewBuilder logic) ──────

    private static string DetectProjectType(string rootPath)
    {
        try
        {
            var csprojFiles = Directory.GetFiles(rootPath, "*.csproj", SearchOption.TopDirectoryOnly);
            if (csprojFiles.Length > 0)
            {
                var content = File.ReadAllText(csprojFiles[0]);
                var tfm = Regex.Match(content, @"<TargetFramework>(.*?)</TargetFramework>");
                return tfm.Success ? $".NET ({tfm.Groups[1].Value})" : ".NET";
            }
        }
        catch { }

        if (File.Exists(Path.Combine(rootPath, "package.json"))) return "Node.js";
        if (File.Exists(Path.Combine(rootPath, "go.mod"))) return "Go";
        if (File.Exists(Path.Combine(rootPath, "Cargo.toml"))) return "Rust";
        if (File.Exists(Path.Combine(rootPath, "pyproject.toml")) || File.Exists(Path.Combine(rootPath, "requirements.txt"))) return "Python";
        if (File.Exists(Path.Combine(rootPath, "pom.xml"))) return "Java (Maven)";
        if (File.Exists(Path.Combine(rootPath, "build.gradle")) || File.Exists(Path.Combine(rootPath, "build.gradle.kts"))) return "Java (Gradle)";
        if (File.Exists(Path.Combine(rootPath, "CMakeLists.txt"))) return "C/C++ (CMake)";
        if (File.Exists(Path.Combine(rootPath, "Makefile"))) return "C/C++ (Make)";

        return "Unknown";
    }

    // ── Module discovery ──────────────────────────────────────────────────

    private List<ModuleInfo> DiscoverModules(string effectiveRoot, string rootPath, int maxDepth)
    {
        var modules = new List<ModuleInfo>();

        // Check for multi-project repo (.sln with multiple .csproj)
        var slnFiles = Directory.GetFiles(effectiveRoot, "*.sln", SearchOption.TopDirectoryOnly);
        if (slnFiles.Length > 0)
        {
            var csprojFiles = Directory.GetFiles(effectiveRoot, "*.csproj", SearchOption.AllDirectories);
            if (csprojFiles.Length > 1)
            {
                // Multi-project: each .csproj directory is a module
                foreach (var csproj in csprojFiles)
                {
                    var projDir = Path.GetDirectoryName(csproj)!;
                    var relPath = Path.GetRelativePath(rootPath, projDir);
                    if (relPath == ".") relPath = Path.GetFileNameWithoutExtension(csproj);

                    var role = InferRole(Path.GetFileName(projDir));
                    var fileCount = CountCodeFiles(projDir);

                    modules.Add(new ModuleInfo(relPath, role, fileCount, IsProject: true));
                }

                return modules;
            }
        }

        // Single-project: treat major folders as modules
        DiscoverFolderModules(effectiveRoot, rootPath, modules, currentDepth: 0, maxDepth);

        // Also count root-level code files as a pseudo-module if any exist
        var rootFiles = Directory.GetFiles(effectiveRoot)
            .Where(f => CodeExtensions.Contains(Path.GetExtension(f)))
            .ToList();

        if (rootFiles.Count > 0)
        {
            modules.Insert(0, new ModuleInfo("(root)", "Entry Point", rootFiles.Count, IsProject: false));
        }

        return modules;
    }

    private void DiscoverFolderModules(string dir, string rootPath, List<ModuleInfo> modules, int currentDepth, int maxDepth)
    {
        if (currentDepth >= maxDepth) return;

        DirectoryInfo[] subDirs;
        try
        {
            subDirs = new DirectoryInfo(dir).GetDirectories()
                .Where(d => !ExcludedDirs.Contains(d.Name) && !d.Name.StartsWith('.'))
                .OrderBy(d => d.Name)
                .ToArray();
        }
        catch { return; }

        foreach (var subDir in subDirs)
        {
            var relPath = Path.GetRelativePath(rootPath, subDir.FullName).Replace('\\', '/');
            var role = InferRole(subDir.Name);
            var fileCount = CountCodeFiles(subDir.FullName);

            if (fileCount == 0) continue;

            // If this folder has a recognized role, register it as a module
            if (role != "Other")
            {
                modules.Add(new ModuleInfo(relPath, role, fileCount, IsProject: false));

                // Still recurse into sub-folders that have their own distinct role
                DiscoverSubModules(subDir.FullName, rootPath, modules, currentDepth + 1, maxDepth, relPath);
            }
            else
            {
                // Unrecognized folder: check if its children have recognized roles
                var childHasRole = false;
                try
                {
                    foreach (var child in subDir.GetDirectories())
                    {
                        if (!ExcludedDirs.Contains(child.Name) && InferRole(child.Name) != "Other")
                        {
                            childHasRole = true;
                            break;
                        }
                    }
                }
                catch { }

                if (childHasRole)
                {
                    // Recurse: this is a grouping folder (e.g., "src/")
                    DiscoverFolderModules(subDir.FullName, rootPath, modules, currentDepth + 1, maxDepth);
                }
                else if (fileCount >= 2)
                {
                    // Register as Other module if it has enough code files
                    modules.Add(new ModuleInfo(relPath, "Other", fileCount, IsProject: false));
                }
            }
        }
    }

    private void DiscoverSubModules(string parentDir, string rootPath, List<ModuleInfo> modules, int currentDepth, int maxDepth, string parentRelPath)
    {
        if (currentDepth >= maxDepth) return;

        DirectoryInfo[] subDirs;
        try
        {
            subDirs = new DirectoryInfo(parentDir).GetDirectories()
                .Where(d => !ExcludedDirs.Contains(d.Name) && !d.Name.StartsWith('.'))
                .ToArray();
        }
        catch { return; }

        foreach (var subDir in subDirs)
        {
            var role = InferRole(subDir.Name);
            if (role == "Other") continue; // Only promote sub-folders with distinct roles

            var relPath = Path.GetRelativePath(rootPath, subDir.FullName).Replace('\\', '/');
            var fileCount = CountCodeFiles(subDir.FullName);
            if (fileCount == 0) continue;

            modules.Add(new ModuleInfo(relPath, role, fileCount, IsProject: false));
        }
    }

    private static string InferRole(string folderName)
    {
        foreach (var (patterns, role) in RoleRules)
        {
            if (patterns.Any(p => string.Equals(p, folderName, StringComparison.OrdinalIgnoreCase)))
            {
                return role;
            }
        }

        return "Other";
    }

    private static int CountCodeFiles(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true
            })
            .Count(f => CodeExtensions.Contains(Path.GetExtension(f))
                        && !f.Split(Path.DirectorySeparatorChar).Any(ExcludedDirs.Contains));
        }
        catch { return 0; }
    }

    // ── Entry point detection ─────────────────────────────────────────────

    private static List<string> DetectEntryPoints(string effectiveRoot, string rootPath)
    {
        var entryPoints = new List<string>();

        foreach (var file in Directory.EnumerateFiles(effectiveRoot, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true
        }))
        {
            var fileName = Path.GetFileName(file);
            if (EntryPointFiles.Any(ep => string.Equals(ep, fileName, StringComparison.OrdinalIgnoreCase)))
            {
                var relPath = Path.GetRelativePath(rootPath, file).Replace('\\', '/');

                // Skip entry points inside excluded dirs
                if (relPath.Split('/').Any(ExcludedDirs.Contains)) continue;

                var desc = fileName.ToLowerInvariant() switch
                {
                    "program.cs" => "ASP.NET Core / .NET host",
                    "startup.cs" => "ASP.NET Core startup configuration",
                    "main.go" => "Go application entry",
                    "main.py" or "app.py" => "Python application entry",
                    "manage.py" => "Django management script",
                    "index.ts" or "index.js" => "Node.js / frontend entry",
                    "main.ts" or "main.js" => "Application main entry",
                    "server.ts" or "server.js" => "Server entry",
                    "app.ts" or "app.js" => "Application entry",
                    "main.java" or "app.java" or "application.java" => "Java application entry",
                    "main.rs" => "Rust binary entry",
                    "lib.rs" => "Rust library entry",
                    _ => ""
                };

                entryPoints.Add(string.IsNullOrEmpty(desc) ? relPath : $"{relPath} ({desc})");
            }
        }

        return entryPoints;
    }

    // ── Cross-module dependency analysis ──────────────────────────────────

    private Dictionary<string, Dictionary<string, int>> AnalyzeCrossModuleDependencies(
        List<ModuleInfo> modules, string effectiveRoot, string rootPath)
    {
        // edges[sourceModule] = { targetModule -> refCount }
        var edges = new ConcurrentDictionary<string, ConcurrentDictionary<string, int>>();

        // Build module lookup: for each code file, determine which module it belongs to
        var fileToModule = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in modules)
        {
            if (mod.RelPath == "(root)")
            {
                // Root files
                foreach (var f in Directory.GetFiles(effectiveRoot)
                    .Where(f => CodeExtensions.Contains(Path.GetExtension(f))))
                {
                    var rel = Path.GetRelativePath(rootPath, f).Replace('\\', '/');
                    fileToModule[rel] = mod.RelPath;
                }
            }
            else
            {
                var modDir = Path.Combine(rootPath, mod.RelPath.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(modDir)) continue;

                foreach (var f in Directory.EnumerateFiles(modDir, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true
                }).Where(f => CodeExtensions.Contains(Path.GetExtension(f))))
                {
                    var rel = Path.GetRelativePath(rootPath, f).Replace('\\', '/');
                    // Assign to the most specific (longest path) module
                    if (!fileToModule.ContainsKey(rel) ||
                        mod.RelPath.Length > fileToModule[rel].Length)
                    {
                        fileToModule[rel] = mod.RelPath;
                    }
                }
            }
        }

        // Build namespace-to-module map for C# projects
        var nsToModule = BuildNamespaceModuleMap(modules, rootPath);

        // Analyze each file's imports
        Parallel.ForEach(fileToModule, kvp =>
        {
            var (relFile, sourceModule) = kvp;
            var absFile = Path.Combine(rootPath, relFile.Replace('/', Path.DirectorySeparatorChar));
            var ext = Path.GetExtension(absFile).ToLowerInvariant();

            string[] lines;
            try { lines = File.ReadAllLines(absFile); }
            catch { return; }

            var targetModules = ext switch
            {
                ".cs" => ResolveCSharpTargets(lines, nsToModule, sourceModule),
                ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs"
                    => ResolveTsTargets(lines, absFile, rootPath, fileToModule, sourceModule),
                ".py" => ResolvePythonTargets(lines, fileToModule, sourceModule),
                ".java" or ".kt" or ".scala"
                    => ResolveJavaTargets(lines, nsToModule, sourceModule),
                ".go" => ResolveGoTargets(lines, fileToModule, sourceModule),
                _ => new Dictionary<string, int>()
            };

            foreach (var (targetModule, count) in targetModules)
            {
                var srcEdges = edges.GetOrAdd(sourceModule, _ => new ConcurrentDictionary<string, int>());
                srcEdges.AddOrUpdate(targetModule, count, (_, old) => old + count);
            }
        });

        // Convert to regular dictionaries
        return edges.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.ToDictionary(x => x.Key, x => x.Value));
    }

    private static Dictionary<string, string> BuildNamespaceModuleMap(List<ModuleInfo> modules, string rootPath)
    {
        // For C#: infer namespace from folder path
        // e.g., Services/Tools -> AnswerCode.Services.Tools
        var projectName = Path.GetFileName(rootPath);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in modules)
        {
            if (mod.RelPath == "(root)")
            {
                map[projectName] = mod.RelPath;
            }
            else
            {
                var ns = projectName + "." + mod.RelPath.Replace('/', '.');
                map[ns] = mod.RelPath;
            }
        }

        return map;
    }

    // ── Per-language import → module resolution ───────────────────────────

    private static Dictionary<string, int> ResolveCSharpTargets(
        string[] lines, Dictionary<string, string> nsToModule, string sourceModule)
    {
        var targets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            var m = CsUsingRegex.Match(line);
            if (m.Success)
            {
                var ns = m.Groups[1].Value;

                // Try exact match first, then prefix match
                if (nsToModule.TryGetValue(ns, out var targetModule))
                {
                    if (!string.Equals(targetModule, sourceModule, StringComparison.OrdinalIgnoreCase))
                    {
                        targets[targetModule] = targets.GetValueOrDefault(targetModule) + 1;
                    }
                }
                else
                {
                    // Try longest prefix match
                    string? bestMatch = null;
                    int bestLen = 0;
                    foreach (var (knownNs, mod) in nsToModule)
                    {
                        if (ns.StartsWith(knownNs, StringComparison.OrdinalIgnoreCase) && knownNs.Length > bestLen)
                        {
                            bestMatch = mod;
                            bestLen = knownNs.Length;
                        }
                    }

                    if (bestMatch != null && !string.Equals(bestMatch, sourceModule, StringComparison.OrdinalIgnoreCase))
                    {
                        targets[bestMatch] = targets.GetValueOrDefault(bestMatch) + 1;
                    }
                }
            }

            // Stop after using block
            var trimmed = line.TrimStart();
            if (!string.IsNullOrWhiteSpace(trimmed)
                && !trimmed.StartsWith("using")
                && !trimmed.StartsWith("//")
                && !trimmed.StartsWith('#')
                && !trimmed.StartsWith("global"))
            {
                break;
            }
        }

        return targets;
    }

    private static Dictionary<string, int> ResolveTsTargets(
        string[] lines, string absFile, string rootPath,
        Dictionary<string, string> fileToModule, string sourceModule)
    {
        var targets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            string? importPath = null;
            var m = TsImportRegex.Match(line);
            if (m.Success)
            {
                importPath = m.Groups[1].Value;
            }
            else
            {
                m = TsRequireRegex.Match(line);
                if (m.Success) importPath = m.Groups[1].Value;
            }

            if (importPath == null || !importPath.StartsWith('.')) continue;

            // Resolve relative import to absolute path
            var dir = Path.GetDirectoryName(absFile)!;
            var resolved = Path.GetFullPath(Path.Combine(dir, importPath));
            var relResolved = Path.GetRelativePath(rootPath, resolved).Replace('\\', '/');

            // Try with common extensions
            var targetModule = FindModuleForFile(relResolved, fileToModule)
                ?? FindModuleForFile(relResolved + ".ts", fileToModule)
                ?? FindModuleForFile(relResolved + ".tsx", fileToModule)
                ?? FindModuleForFile(relResolved + ".js", fileToModule)
                ?? FindModuleForFile(relResolved + "/index.ts", fileToModule)
                ?? FindModuleForFile(relResolved + "/index.js", fileToModule);

            if (targetModule != null && !string.Equals(targetModule, sourceModule, StringComparison.OrdinalIgnoreCase))
            {
                targets[targetModule] = targets.GetValueOrDefault(targetModule) + 1;
            }
        }

        return targets;
    }

    private static Dictionary<string, int> ResolvePythonTargets(
        string[] lines, Dictionary<string, string> fileToModule, string sourceModule)
    {
        var targets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            var m = PyImportRegex.Match(line);
            if (!m.Success) continue;

            var mod = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            var modPath = mod.Replace('.', '/');

            // Try to find a matching module
            var targetModule = FindModuleByPrefix(modPath, fileToModule);
            if (targetModule != null && !string.Equals(targetModule, sourceModule, StringComparison.OrdinalIgnoreCase))
            {
                targets[targetModule] = targets.GetValueOrDefault(targetModule) + 1;
            }
        }

        return targets;
    }

    private static Dictionary<string, int> ResolveJavaTargets(
        string[] lines, Dictionary<string, string> nsToModule, string sourceModule)
    {
        var targets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            var m = JavaImportRegex.Match(line);
            if (m.Success)
            {
                var pkg = m.Groups[1].Value;
                // Try prefix matching against known modules
                string? bestMatch = null;
                int bestLen = 0;
                foreach (var (knownNs, mod) in nsToModule)
                {
                    if (pkg.StartsWith(knownNs, StringComparison.OrdinalIgnoreCase) && knownNs.Length > bestLen)
                    {
                        bestMatch = mod;
                        bestLen = knownNs.Length;
                    }
                }

                if (bestMatch != null && !string.Equals(bestMatch, sourceModule, StringComparison.OrdinalIgnoreCase))
                {
                    targets[bestMatch] = targets.GetValueOrDefault(bestMatch) + 1;
                }
            }

            var trimmed = line.TrimStart();
            if (!string.IsNullOrWhiteSpace(trimmed)
                && !trimmed.StartsWith("import")
                && !trimmed.StartsWith("//")
                && !trimmed.StartsWith("package"))
            {
                break;
            }
        }

        return targets;
    }

    private static Dictionary<string, int> ResolveGoTargets(
        string[] lines, Dictionary<string, string> fileToModule, string sourceModule)
    {
        var targets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        bool inImportBlock = false;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("import ("))
            {
                inImportBlock = true;
                continue;
            }

            string? importPath = null;

            if (inImportBlock)
            {
                if (trimmed.StartsWith(')'))
                {
                    inImportBlock = false;
                    continue;
                }
                var m = GoImportRegex.Match(trimmed);
                if (m.Success) importPath = m.Groups[1].Value;
            }
            else if (trimmed.StartsWith("import \""))
            {
                var m = GoImportRegex.Match(trimmed["import ".Length..]);
                if (m.Success) importPath = m.Groups[1].Value;
            }

            if (importPath == null) continue;

            var targetModule = FindModuleByPrefix(importPath, fileToModule);
            if (targetModule != null && !string.Equals(targetModule, sourceModule, StringComparison.OrdinalIgnoreCase))
            {
                targets[targetModule] = targets.GetValueOrDefault(targetModule) + 1;
            }
        }

        return targets;
    }

    // ── Helper: find module for a file path ───────────────────────────────

    private static string? FindModuleForFile(string relPath, Dictionary<string, string> fileToModule)
    {
        if (fileToModule.TryGetValue(relPath, out var mod)) return mod;
        return null;
    }

    private static string? FindModuleByPrefix(string pathOrModule, Dictionary<string, string> fileToModule)
    {
        // Find any file whose path starts with this prefix and return its module
        foreach (var (filePath, module) in fileToModule)
        {
            if (filePath.StartsWith(pathOrModule, StringComparison.OrdinalIgnoreCase))
            {
                return module;
            }
        }
        return null;
    }

    // ── Output formatting ─────────────────────────────────────────────────

    private static string FormatRepoMap(
        string projectName,
        string projectType,
        List<ModuleInfo> modules,
        List<string> entryPoints,
        Dictionary<string, Dictionary<string, int>>? edges)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"Repository Map: {projectName}");
        sb.AppendLine($"Type: {projectType}");
        sb.AppendLine();

        // Modules
        sb.AppendLine($"Modules ({modules.Count}):");
        var maxPathLen = modules.Max(m => m.RelPath.Length);
        foreach (var mod in modules)
        {
            var path = mod.IsProject ? mod.RelPath : mod.RelPath + "/";
            sb.AppendLine($"  {path.PadRight(maxPathLen + 2)} [{mod.Role}]  — {mod.FileCount} file{(mod.FileCount != 1 ? "s" : "")}");
        }

        // Entry points
        if (entryPoints.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Entry Points:");
            foreach (var ep in entryPoints)
            {
                sb.AppendLine($"  {ep}");
            }
        }

        // Dependencies
        if (edges != null && edges.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Module Dependencies:");

            foreach (var (source, targets) in edges.OrderBy(e => e.Key))
            {
                var targetList = targets
                    .OrderByDescending(t => t.Value)
                    .Select(t => $"{t.Key} ({t.Value} refs)")
                    .ToList();

                if (targetList.Count > 0)
                {
                    sb.AppendLine($"  {source} → {string.Join(", ", targetList)}");
                }
            }

            // Mermaid diagram
            sb.AppendLine();
            sb.AppendLine("Mermaid:");
            sb.AppendLine("  graph LR");
            foreach (var (source, targets) in edges.OrderBy(e => e.Key))
            {
                foreach (var (target, _) in targets.OrderByDescending(t => t.Value))
                {
                    var srcId = SanitizeMermaidId(source);
                    var tgtId = SanitizeMermaidId(target);
                    sb.AppendLine($"    {srcId}[\"{source}\"] --> {tgtId}[\"{target}\"]");
                }
            }
        }

        return sb.ToString();
    }

    private static string SanitizeMermaidId(string name)
    {
        return Regex.Replace(name, @"[^a-zA-Z0-9]", "_").Trim('_');
    }

    // ── Inner types ───────────────────────────────────────────────────────

    private record ModuleInfo(string RelPath, string Role, int FileCount, bool IsProject);
}
