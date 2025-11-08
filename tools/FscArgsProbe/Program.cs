using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Graph;
using Microsoft.Build.Locator;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: FscArgsProbe <path-to-fsproj>");
    return 1;
}

var projectPath = Path.GetFullPath(args[0]);
if (!File.Exists(projectPath))
{
    Console.Error.WriteLine($"Project file '{projectPath}' not found.");
    return 2;
}

if (!MSBuildLocator.IsRegistered)
{
    MSBuildLocator.RegisterDefaults();
}

var projectCollection = new ProjectCollection();

var graph = new ProjectGraph(projectPath, projectCollection);
var buildParams = new BuildParameters(projectCollection)
{
    Loggers = []
};

var request = new GraphBuildRequestData(graph, new[] { "Build" });

var buildResult = BuildManager.DefaultBuildManager.Build(buildParams, request);
if (buildResult.OverallResult != BuildResultCode.Success)
{
    Console.Error.WriteLine($"Build failed for '{projectPath}'.");
    return 3;
}

foreach (var node in graph.ProjectNodes)
{
    var language = node.ProjectInstance.GetPropertyValue("Language");
    if (!string.Equals(language, "F#", StringComparison.OrdinalIgnoreCase))
    {
        continue;
    }

    Console.WriteLine($"Project: {node.ProjectInstance.FullPath}");
    var commandLineItems = node.ProjectInstance.GetItems("FscCommandLineArgs");
    if (commandLineItems.Count == 0)
    {
        Console.WriteLine("  FscCommandLineArgs: <none>");
        continue;
    }

    foreach (var item in commandLineItems)
    {
        Console.WriteLine($"  arg: {item.EvaluatedInclude}");
    }
}

return 0;
