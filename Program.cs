using AnswerCode.Models;
using AnswerCode.Services.Analysis;
using AnswerCode.Services;
using AnswerCode.Services.Providers;
using AnswerCode.Services.Tools;

using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog
Log.Logger = new LoggerConfiguration().ReadFrom.Configuration(builder.Configuration).WriteTo
                                               .Console().WriteTo
                                               .File($"logs/log-{DateTime.Now:yyyy-MM-dd_HHmmss}.txt")
                                               .CreateLogger();

builder.Host.UseSerilog();

// Load appsettings.Local.json for local overrides (gitignored - copy from appsettings.Example.json)
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddMemoryCache();

// Configure LLM options from appsettings
builder.Services.Configure<LLMSettings>(builder.Configuration.GetSection(LLMSettings.SectionName));

// Register LLM provider creators (OCP-compliant: add new creators to support new providers)
builder.Services.AddSingleton<ILLMProviderCreator, AzureOpenAIProviderCreator>();
builder.Services.AddSingleton<ILLMProviderCreator, OpenAIProviderCreator>(); // fallback for all OpenAI-compatible

// Register LLM Service Factory (Singleton - creates providers once)
builder.Services.AddSingleton<ILLMServiceFactory, LLMServiceFactory>();

// Register LLM Service (uses Factory to get providers)
builder.Services.AddSingleton<ILLMService, LLMService>();

// Register Code Explorer Service
builder.Services.AddScoped<ICodeExplorerService, CodeExplorerService>();

// Register analysis services for symbol-aware tools
builder.Services.AddSingleton<IWorkspaceFileService, WorkspaceFileService>();
builder.Services.AddSingleton<ICSharpCompilationService, CSharpCompilationService>();
builder.Services.AddSingleton<ILanguageHeuristicService, LanguageHeuristicService>();
builder.Services.AddSingleton<ISymbolAnalysisService, SymbolAnalysisService>();
builder.Services.AddSingleton<IReferenceAnalysisService, ReferenceAnalysisService>();
builder.Services.AddSingleton<ITestDiscoveryService, TestDiscoveryService>();

// Register repo map service
builder.Services.AddSingleton<IRepoMapService, RepoMapService>();

// Register call graph service
builder.Services.AddSingleton<ICallGraphService, CallGraphService>();

// Register tools via DI (add new tools here)
builder.Services.AddSingleton<ITool, GrepTool>();
builder.Services.AddSingleton<ITool, ReadFileTool>();
builder.Services.AddSingleton<ITool, ReadSymbolTool>();
builder.Services.AddSingleton<ITool, ListDirectoryTool>();
builder.Services.AddSingleton<ITool, GlobTool>();
builder.Services.AddSingleton<ITool, FileOutlineTool>();
builder.Services.AddSingleton<ITool, FindDefinitionTool>();
builder.Services.AddSingleton<ITool, FindReferencesTool>();
builder.Services.AddSingleton<ITool, FindTestsTool>();
builder.Services.AddSingleton<ITool, RelatedFilesTool>();
builder.Services.AddSingleton<ITool, RepoMapTool>();
builder.Services.AddSingleton<ITool, CallGraphTool>();
builder.Services.AddSingleton<ToolRegistry>();

// Register Agent Service (agentic tool-calling loop)
builder.Services.AddScoped<IAgentService, AgentService>();

// Register upload cleanup background service (auto-delete expired uploads)
builder.Services.AddHostedService<UploadCleanupService>();

// Add CORS for development
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

// Serve static files (for the frontend)
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseCors();

app.UseAuthorization();

app.MapControllers();

// Fallback to index.html for SPA-like behavior
app.MapFallbackToFile("index.html");

// Log available providers on startup
var factory = app.Services.GetRequiredService<ILLMServiceFactory>();
var providers = factory.GetAvailableProviders().ToList();
Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════╗
║                                                               ║
║     AnswerCode - Source Code Q&A System                       ║
║                                                               ║
║     Web UI:  http://localhost:5000                            ║
║              https://localhost:5001                           ║
║                                                               ║
║     Available LLM Providers:                                  ║");

foreach (var provider in providers)
{
    Console.WriteLine(
$"║       - {provider}                                            ║");
}

Console.WriteLine(
@"║                                                               ║
║     Configure LLM in appsettings.json (LLM:Providers:...)     ║
║     Use appsettings.Local.json for local overrides            ║
║                                                               ║
╚═══════════════════════════════════════════════════════════════╝
");

app.Run();