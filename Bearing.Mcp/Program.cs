using Bearing.Core;
using Bearing.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Corpus root: --corpus <path>, then BEARING_CORPUS, then ./corpus.
var corpusRoot = ReadArgument(args, "--corpus")
                 ?? Environment.GetEnvironmentVariable("BEARING_CORPUS")
                 ?? Path.Combine(AppContext.BaseDirectory, "corpus");

var builder = Host.CreateApplicationBuilder(args);

// THE stdio gotcha: stdout carries JSON-RPC frames. Anything else written there
// corrupts the protocol and the client disconnects with an opaque parse error.
// All logging goes to stderr, always.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services.AddSingleton(new BearingOptions
{
    CorpusRoot = corpusRoot,
    WatchForChanges = true
});

builder.Services.AddSingleton<Corpus>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var host = builder.Build();

// Fail loudly at startup rather than on the first tool call — a missing corpus
// folder is a setup mistake, not a runtime condition.
var corpus = host.Services.GetRequiredService<Corpus>();
var logger = host.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("Bearing corpus loaded from {Root}: {Count} documents",
    corpusRoot, corpus.List(null).Count);

await host.RunAsync();

static string? ReadArgument(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

// Marker type for ILogger<Program> in a top-level program.
public partial class Program;
