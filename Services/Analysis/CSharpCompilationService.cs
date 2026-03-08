using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Caching.Memory;

namespace AnswerCode.Services.Analysis;

public class CSharpCompilationService(IMemoryCache cache,
                                      IWorkspaceFileService workspaceFileService,
                                      ILogger<CSharpCompilationService> logger) : ICSharpCompilationService
{
    public async Task<CSharpCompilationContext> GetCompilationAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        var normalizedRoot = Path.GetFullPath(rootPath);
        var files = workspaceFileService.EnumerateCSharpFiles(normalizedRoot);
        var signature = BuildCacheSignature(files);
        var cacheKey = $"csharp-compilation::{normalizedRoot}::{signature}";

        if (cache.TryGetValue<CSharpCompilationContext>(cacheKey, out var cached)
            && cached is not null)
        {
            return cached;
        }

        logger.LogInformation("Building Roslyn compilation for {RootPath} with {FileCount} source file(s)", normalizedRoot, files.Count);

        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest, DocumentationMode.Parse);
        var syntaxTrees = new List<SyntaxTree>(files.Count + 1);
        var pathToTree = new Dictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
        var treeToPath = new Dictionary<SyntaxTree, string>();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var text = await File.ReadAllTextAsync(file, cancellationToken);
            var tree = CSharpSyntaxTree.ParseText(SourceText.From(text, Encoding.UTF8), parseOptions, file);
            syntaxTrees.Add(tree);
            pathToTree[file] = tree;
            treeToPath[tree] = file;
        }

        var implicitUsingsPath = Path.Combine(normalizedRoot, "__AnswerCode.ImplicitGlobalUsings.g.cs");
        var implicitUsingsTree = CSharpSyntaxTree.ParseText(SourceText.From(BuildImplicitUsingsSource(), Encoding.UTF8),
                                                            parseOptions,
                                                            implicitUsingsPath);
        syntaxTrees.Add(implicitUsingsTree);
        pathToTree[implicitUsingsPath] = implicitUsingsTree;
        treeToPath[implicitUsingsTree] = implicitUsingsPath;

        var compilation = CSharpCompilation.Create(assemblyName: Path.GetFileName(normalizedRoot),
                                                   syntaxTrees: syntaxTrees,
                                                   references: CreateMetadataReferences(),
                                                   options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var context = new CSharpCompilationContext(normalizedRoot,
                                                   compilation,
                                                   files,
                                                   pathToTree,
                                                   treeToPath,
                                                   signature);

        cache.Set(cacheKey, context, new MemoryCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromMinutes(10),
            Size = 1
        });

        return context;
    }

    private static string BuildCacheSignature(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
        {
            return "empty";
        }

        var sb = new StringBuilder();
        foreach (var file in files)
        {
            var lastWrite = File.GetLastWriteTimeUtc(file).Ticks;
            sb.Append(file);
            sb.Append('|');
            sb.Append(lastWrite);
            sb.Append(';');
        }

        return sb.ToString().GetHashCode(StringComparison.Ordinal).ToString("X");
    }

    private static IReadOnlyList<MetadataReference> CreateMetadataReferences()
    {
        var referencePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
        {
            foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                referencePaths.Add(path);
            }
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (assembly.IsDynamic || string.IsNullOrWhiteSpace(assembly.Location))
                {
                    continue;
                }

                referencePaths.Add(assembly.Location);
            }
            catch
            {
                // Ignore dynamic/unresolvable assemblies.
            }
        }

        referencePaths.Add(typeof(object).Assembly.Location);
        referencePaths.Add(typeof(Enumerable).Assembly.Location);
        referencePaths.Add(typeof(Task).Assembly.Location);
        referencePaths.Add(typeof(WebApplication).Assembly.Location);

        return referencePaths.Where(File.Exists)
                             .Select(path => MetadataReference.CreateFromFile(path))
                             .ToList();
    }

    private static string BuildImplicitUsingsSource()
    {
        return """
            global using System;
            global using System.Collections.Generic;
            global using System.IO;
            global using System.Linq;
            global using System.Net.Http;
            global using System.Threading;
            global using System.Threading.Tasks;
            global using Microsoft.AspNetCore.Builder;
            global using Microsoft.AspNetCore.Hosting;
            global using Microsoft.AspNetCore.Http;
            global using Microsoft.AspNetCore.Mvc;
            global using Microsoft.Extensions.Configuration;
            global using Microsoft.Extensions.DependencyInjection;
            global using Microsoft.Extensions.Hosting;
            global using Microsoft.Extensions.Logging;
            """;
    }
}
