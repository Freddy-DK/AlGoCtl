using System.Reflection;

const string Help = """
AL-Go for GitHub Control CLI

Usage:
    algoctl <command> [--key "value" ...]

Commands:
    --version, -v    Show the algoctl version
""";

#if NET8_0
Console.Error.WriteLine("Warning: You are running the .NET 8 build of algoctl. .NET 10 is the preferred runtime; install .NET 10 to use the preferred build.");
#endif

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

Console.Error.WriteLine(Help);
return 1;
