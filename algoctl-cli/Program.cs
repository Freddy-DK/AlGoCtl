using System.Reflection;
using ALGoCtl;

const string Help = """
AL-Go for GitHub Control CLI

Usage:
    algoctl <command> [--key "value" ...]

Commands:
    createrepo       Create a new repository from a template repository
    updaterepo       Update an existing repository's AL-Go system-file workflow and run it
    createrelease    Run the AL-Go CreateRelease workflow to create a release
    downloadfile     Download a file using parallel byte-range streams (optionally unzip)
    --version, -v    Show the algoctl version
    --help, -h       Show this help

Run 'algoctl <command> --help' for command-specific help.
""";

#if NET8_0
Console.Error.WriteLine("Warning: You are running the .NET 8 build of algoctl. .NET 10 is the preferred runtime; install .NET 10 to use the preferred build.");
#endif

// Top-level help: no arguments, or a help flag as the first argument.
if (args.Length == 0
    || string.Equals(args[0], "--help", StringComparison.OrdinalIgnoreCase)
    || string.Equals(args[0], "-h", StringComparison.OrdinalIgnoreCase)
    || string.Equals(args[0], "-?", StringComparison.Ordinal))
{
    Console.WriteLine(Help);
    return 0;
}

if (args.Length > 0 && (string.Equals(args[0], "--version", StringComparison.OrdinalIgnoreCase)
    || string.Equals(args[0], "-v", StringComparison.OrdinalIgnoreCase)))
{
    var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "unknown";
    // Strip build metadata (+hash) and leading zeros from segments
    var plusIndex = version.IndexOf('+');
    if (plusIndex >= 0) version = version[..plusIndex];
    version = string.Join('.', version.Split('.').Select(s => s.TrimStart('0') is "" ? "0" : s.TrimStart('0')));
    Console.WriteLine(version);
    return 0;
}

if (args.Length > 0 && string.Equals(args[0], CreateRepoCommand.Name, StringComparison.OrdinalIgnoreCase))
{
    return CreateRepoCommand.Run(args);
}

if (args.Length > 0 && string.Equals(args[0], UpdateRepoCommand.Name, StringComparison.OrdinalIgnoreCase))
{
    return UpdateRepoCommand.Run(args);
}

if (args.Length > 0 && string.Equals(args[0], CreateReleaseCommand.Name, StringComparison.OrdinalIgnoreCase))
{
    return CreateReleaseCommand.Run(args);
}

if (args.Length > 0 && string.Equals(args[0], DownloadFileCommand.Name, StringComparison.OrdinalIgnoreCase))
{
    return DownloadFileCommand.Run(args);
}

Console.Error.WriteLine(Help);
return 1;
