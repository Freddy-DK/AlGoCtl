using System.Text.Json;

namespace ALGoCtl;

/// <summary>
/// <c>downloadartifacts</c> — downloads Business Central artifacts into a local
/// cache using the same folder structure as BcContainerHelper's Download-Artifacts.
/// </summary>
internal sealed class DownloadArtifactsCommand
{
    public const string Name = "downloadartifacts";

    private static readonly string DefaultBasePath = OperatingSystem.IsWindows()
        ? @"c:\bcartifacts.cache"
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bcartifacts.cache");

    public static readonly string Help = $"""
        algoctl downloadartifacts — download Business Central artifacts into a local
        cache, using the same folder structure as BcContainerHelper's Download-Artifacts
        (<basePath>/<type>/<version>/<country> and <basePath>/<type>/<version>/platform).

        Usage:
            algoctl downloadartifacts --artifactUrl <url> [options]

        Options:
            --artifactUrl <url>               URL of the artifact to download. (required)
            --includePlatform                 Also download the platform artifact.
            --force                           Download even if the artifact folder exists
                                              (the existing folder is removed first).
            --basePath <path>                 Root folder of the artifact cache.
                                              Default: {DefaultBasePath}
            --help, -h                        Show this help.
        """;

    public static int Run(string[] args)
    {
        // --help, -h — show command help and exit.
        if (ArgParser.HasHelpFlag(args.Skip(1)))
        {
            Console.WriteLine(Help);
            return 0;
        }

        var parameters = ArgParser.Parse(args.Skip(1));

        // --artifactUrl (required)
        if (!parameters.TryGetValue("artifacturl", out var artifactUrl) || string.IsNullOrWhiteSpace(artifactUrl))
        {
            Console.Error.WriteLine("Error: --artifactUrl is required.");
            Console.Error.WriteLine();
            Console.Error.WriteLine(Help);
            return 1;
        }
        if (!Uri.TryCreate(artifactUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            Console.Error.WriteLine("Error: --artifactUrl must be an absolute https URL.");
            return 1;
        }

        // --basePath (default: c:\bcartifacts.cache on Windows)
        var basePath = parameters.TryGetValue("basepath", out var bp) && !string.IsNullOrWhiteSpace(bp)
            ? bp
            : DefaultBasePath;

        var includePlatform = parameters.ContainsKey("includeplatform");
        var force = parameters.ContainsKey("force");

        try
        {
            return DownloadArtifactsAsync(artifactUrl, Path.GetFullPath(basePath), includePlatform, force).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> DownloadArtifactsAsync(string artifactUrl, string basePath, bool includePlatform, bool force)
    {
        Directory.CreateDirectory(basePath);

        Uri appUri;
        string appArtifactPath;
        JsonElement? appManifest;
        bool redir;
        Task<(bool Ok, string Path)>? platformTask = null;
        string? speculativePlatformUrl = null;
        do
        {
            redir = false;
            appUri = new Uri(artifactUrl);
            appArtifactPath = GetArtifactPath(basePath, appUri);
            var appManifestPath = Path.Combine(appArtifactPath, "manifest.json");
            var exists = Directory.Exists(appArtifactPath);

            if (exists && force)
            {
                Directory.Delete(appArtifactPath, recursive: true);
                exists = false;
            }

            // The platform URL comes from the manifest, so it must be present.
            if (exists && includePlatform && !File.Exists(appManifestPath))
            {
                Directory.Delete(appArtifactPath, recursive: true);
                exists = false;
            }

            if (exists && File.Exists(appManifestPath) && ReadManifest(appManifestPath) is null)
            {
                Console.WriteLine($"Manifest file is corrupt, removing {appArtifactPath} and redownloading");
                Directory.Delete(appArtifactPath, recursive: true);
                exists = false;
            }

            if (!exists)
            {
                // Start the platform in parallel using the derived URL; verified against the manifest below.
                if (includePlatform && platformTask is null)
                {
                    speculativePlatformUrl = GetPlatformUrl(null, appUri);
                    platformTask = DownloadPlatformAsync(speculativePlatformUrl, basePath, force, TimeSpan.FromSeconds(1));
                }

                Console.WriteLine($"Downloading artifact {appUri.AbsolutePath}");
                if (!await DownloadPackageAsync(artifactUrl, appArtifactPath))
                {
                    if (platformTask is not null) await platformTask;
                    return 1;
                }
            }
            TouchLastUsed(appArtifactPath);

            appManifest = File.Exists(appManifestPath) ? ReadManifest(appManifestPath) : null;
            var applicationUrl = GetManifestString(appManifest, "applicationUrl");
            if (!string.IsNullOrEmpty(applicationUrl))
            {
                redir = true;
                artifactUrl = applicationUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    ? applicationUrl
                    : $"https://{appUri.Host}/{applicationUrl}{appUri.Query}";
            }
        } while (redir);

        Console.WriteLine();
        Console.WriteLine($"Application artifact: {appArtifactPath}");

        if (!includePlatform)
            return 0;

        var platformUrl = GetPlatformUrl(appManifest, appUri);
        (bool Ok, string Path) platform;
        if (platformTask is not null && platformUrl == speculativePlatformUrl)
        {
            platform = await platformTask;
        }
        else
        {
            // The manifest (or a redirect) points to a different platform; the speculative one is not needed.
            if (platformTask is not null) await platformTask;
            platform = await DownloadPlatformAsync(platformUrl, basePath, force, TimeSpan.Zero);
        }
        if (!platform.Ok)
            return 1;
        TouchLastUsed(platform.Path);

        Console.WriteLine($"Platform artifact:    {platform.Path}");
        return 0;
    }

    private static string GetPlatformUrl(JsonElement? appManifest, Uri appUri)
    {
        var platformUrl = GetManifestString(appManifest, "platformUrl");
        if (string.IsNullOrEmpty(platformUrl))
        {
            var appPath = appUri.AbsolutePath;
            platformUrl = $"{appPath[..appPath.LastIndexOf('/')]}/platform".TrimStart('/');
        }
        if (!platformUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            platformUrl = $"https://{appUri.Host.TrimEnd('/')}/{platformUrl}{appUri.Query}";
        return platformUrl;
    }

    private static async Task<(bool Ok, string Path)> DownloadPlatformAsync(string platformUrl, string basePath, bool force, TimeSpan delay)
    {
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay);

        var platformUri = new Uri(platformUrl);
        var platformArtifactPath = GetArtifactPath(basePath, platformUri);
        var exists = Directory.Exists(platformArtifactPath);
        if (exists && force)
        {
            Directory.Delete(platformArtifactPath, recursive: true);
            exists = false;
        }
        if (exists)
            return (true, platformArtifactPath);

        Console.WriteLine($"Downloading platform artifact {platformUri.AbsolutePath}");
        return (await DownloadPackageAsync(platformUrl, platformArtifactPath), platformArtifactPath);
    }

    /// <summary>Maps an artifact URL to <c>basePath/&lt;url path&gt;</c>, rejecting paths that escape <paramref name="basePath"/>.</summary>
    private static string GetArtifactPath(string basePath, Uri uri)
    {
        var path = Path.GetFullPath(Path.Combine(basePath, uri.AbsolutePath.TrimStart('/')));
        if (!path.StartsWith(basePath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"artifact path escapes base path: {uri.AbsolutePath}");
        return path;
    }

    /// <summary>
    /// Downloads and extracts a zip into a temp folder next to <paramref name="destinationPath"/>,
    /// then renames it, so a partial download never looks like a cached artifact.
    /// </summary>
    private static async Task<bool> DownloadPackageAsync(string url, string destinationPath)
    {
        var parent = Path.GetDirectoryName(destinationPath)!;
        Directory.CreateDirectory(parent);
        var tmpFolder = Path.Combine(parent, Path.GetRandomFileName());
        try
        {
            if (await DownloadFileCommand.DownloadAsync(url, tmpFolder, unzip: true) != 0)
                return false;
            Directory.Move(tmpFolder, destinationPath);
            return true;
        }
        finally
        {
            if (Directory.Exists(tmpFolder))
                Directory.Delete(tmpFolder, recursive: true);
        }
    }

    private static JsonElement? ReadManifest(string manifestPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetManifestString(JsonElement? manifest, string name)
    {
        if (manifest is not { ValueKind: JsonValueKind.Object } m)
            return null;
        foreach (var property in m.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.ToString();
        }
        return null;
    }

    private static void TouchLastUsed(string artifactPath)
    {
        try { File.WriteAllText(Path.Combine(artifactPath, "lastused"), DateTime.UtcNow.Ticks.ToString()); }
        catch { /* best effort, as in BcContainerHelper */ }
    }
}
