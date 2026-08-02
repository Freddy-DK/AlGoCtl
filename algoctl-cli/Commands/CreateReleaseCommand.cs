namespace ALGoCtl;

/// <summary>
/// <c>createrelease</c> — triggers the AL-Go <c>CreateRelease.yaml</c> workflow
/// in an existing repository to promote a build to a GitHub release.
/// </summary>
internal sealed class CreateReleaseCommand : WorkflowCommandBase
{
    public const string Name = "createrelease";

    /// <summary>File name of the workflow, used when triggering it via <c>gh workflow run</c>.</summary>
    private const string WorkflowFileName = "CreateRelease.yaml";

    public const string Help = """
        algoctl createrelease — run the AL-Go CreateRelease.yaml workflow in an
        existing repository to promote a build to a GitHub release.

        Usage:
            algoctl createrelease --repo <owner/repo|url> --ghuser <user> --name <name> --tag <tag> [options]

        Options:
            --repo <owner/repo|url>           The repository to create the release in. Short form
                                              defaults to github.com. (required)
            --ghuser <user>                   GitHub CLI account to use for running the workflow.
                                              Default: the active account on the target host.
            --branch <branch>                 Branch (ref) to run the workflow on.
                                              Default: the repository's default branch.
            --name <name>                     Name of this release. (required)
            --tag <tag>                       Tag of this release; must be a semantic version
                                              string (https://semver.org, e.g. 1.0.0). A pattern
                                              ending in '*' (e.g. 1.0.*) resolves to the latest
                                              matching tag with its patch incremented, or the
                                              first one (e.g. 1.0.0) when none exist. (required)
            --buildversion <version>          Build version to promote to release.
                                              Default: latest
            --releasetype <Release|Prerelease|Draft>
                                              Release, prerelease or draft? Default: Release
            --createreleasebranch             Create a release branch. Default: false
            --releasebranchprefix <prefix>    Prefix for the release branch. Used only if
                                              --createreleasebranch is set. Default: release/
            --updateversionnumber <version>   New version number in the main branch. Use
                                              Major.Minor (optionally add .Build for versioning
                                              strategy 3) for an absolute change, or +1, +0.1
                                              (or +0.0.1 for versioning strategy 3) for an
                                              incremental change. Default: (none)
            --skipupdatingdependencies        Skip updating dependency version numbers in all
                                              apps. Default: false
            --directcommit                    Direct commit? Default: false
            --useghtokenworkflow              Use GhTokenWorkflow for PR/commit? Default: false
            --confirm                         Skip the interactive confirmation prompt.
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

        // --repo (required)
        if (!parameters.TryGetValue("repo", out var repoInput) || string.IsNullOrWhiteSpace(repoInput))
        {
            Console.Error.WriteLine("Error: --repo is required (e.g. owner/repo or a full URL).");
            Console.Error.WriteLine();
            Console.Error.WriteLine(Help);
            return 1;
        }

        // --name (required)
        if (!parameters.TryGetValue("name", out var releaseName) || string.IsNullOrWhiteSpace(releaseName))
        {
            Console.Error.WriteLine("Error: --name is required (the name of this release).");
            Console.Error.WriteLine();
            Console.Error.WriteLine(Help);
            return 1;
        }

        // --tag (required)
        if (!parameters.TryGetValue("tag", out var tag) || string.IsNullOrWhiteSpace(tag))
        {
            Console.Error.WriteLine("Error: --tag is required (a semantic version string, e.g. 1.0.0).");
            Console.Error.WriteLine();
            Console.Error.WriteLine(Help);
            return 1;
        }

        // --ghuser (optional) — defaults to the active account on the target host.
        parameters.TryGetValue("ghuser", out var ghUser);

        // --branch (optional) — the ref to run the workflow on. Defaults to the repo's default branch.
        parameters.TryGetValue("branch", out var branch);

        // --buildversion (default: latest)
        var buildVersion = parameters.TryGetValue("buildversion", out var bv) && !string.IsNullOrWhiteSpace(bv)
            ? bv
            : "latest";

        // --releasetype (default: Release) — one of Release, Prerelease, Draft.
        var releaseType = "Release";
        if (parameters.TryGetValue("releasetype", out var rt) && !string.IsNullOrWhiteSpace(rt))
        {
            releaseType = rt.ToLowerInvariant() switch
            {
                "release" => "Release",
                "prerelease" => "Prerelease",
                "draft" => "Draft",
                _ => string.Empty,
            };
            if (releaseType.Length == 0)
            {
                Console.Error.WriteLine($"Error: --releasetype must be one of Release, Prerelease or Draft (got '{rt}').");
                return 1;
            }
        }

        // --releasebranchprefix (default: release/)
        var releaseBranchPrefix = parameters.TryGetValue("releasebranchprefix", out var rbp) && !string.IsNullOrWhiteSpace(rbp)
            ? rbp
            : "release/";

        // --updateversionnumber (default: none)
        parameters.TryGetValue("updateversionnumber", out var updateVersionNumber);
        updateVersionNumber ??= string.Empty;

        // Boolean flags (default: false; presence sets true unless an explicit value is given).
        var createReleaseBranch = ParseBoolFlag(parameters, "createreleasebranch");
        var skipUpdatingDependencies = ParseBoolFlag(parameters, "skipupdatingdependencies");
        var directCommit = ParseBoolFlag(parameters, "directcommit");
        var useGhTokenWorkflow = ParseBoolFlag(parameters, "useghtokenworkflow");

        // --confirm (flag) — skips the interactive confirmation prompt.
        var skipConfirm = parameters.ContainsKey("confirm");

        RepoSpec target;
        try
        {
            target = RepoSpec.Parse(repoInput);
        }
        catch (FormatException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }

        // Verify the external tools we depend on are installed and recent enough.
        if (!PrerequisitesHelper.EnsureGhIsAvailable()) return 1;

        // Resolve the token for the account that will trigger the workflow in the repo.
        var targetToken = RepoCommandHelpers.GetGhToken(target.Host, ghUser);
        if (targetToken is null)
        {
            if (string.IsNullOrWhiteSpace(ghUser))
                Console.Error.WriteLine($"Error: no authenticated GitHub account found for '{target.Host}'.");
            else
                Console.Error.WriteLine($"Error: could not obtain a GitHub token for user '{ghUser}' on '{target.Host}'.");
            Console.Error.WriteLine("Make sure the account is authenticated: gh auth login");
            return 1;
        }
        var targetLogin = RepoCommandHelpers.ResolveLogin(target.Host, targetToken) ?? ghUser;

        // Resolve a wildcard tag pattern (e.g. 1.0.*) to a concrete tag by
        // inspecting existing tags in the repository.
        var resolvedTag = ResolveTag(target, targetToken, tag);
        if (resolvedTag is null)
            return 1;
        tag = resolvedTag;

        Console.WriteLine("Creating release:");
        Console.WriteLine($"  Repository:              {target.HttpsUrl}");
        Console.WriteLine($"  Branch:                  {(string.IsNullOrWhiteSpace(branch) ? "(default branch)" : branch)}");
        Console.WriteLine($"  Name:                    {releaseName}");
        Console.WriteLine($"  Tag:                     {tag}");
        Console.WriteLine($"  Build version:           {buildVersion}");
        Console.WriteLine($"  Release type:            {releaseType}");
        Console.WriteLine($"  Create release branch:   {createReleaseBranch}");
        Console.WriteLine($"  Release branch prefix:   {releaseBranchPrefix}");
        Console.WriteLine($"  Update version number:   {(string.IsNullOrWhiteSpace(updateVersionNumber) ? "(none)" : updateVersionNumber)}");
        Console.WriteLine($"  Skip updating deps:      {skipUpdatingDependencies}");
        Console.WriteLine($"  Direct commit:           {directCommit}");
        Console.WriteLine($"  Use GhTokenWorkflow:     {useGhTokenWorkflow}");
        Console.WriteLine($"  GitHub user:             {(string.IsNullOrWhiteSpace(targetLogin) ? $"(active account on {target.Host})" : $"{targetLogin} on {target.Host}")}");
        Console.WriteLine();

        if (!skipConfirm && !RepoCommandHelpers.Confirm())
        {
            Console.WriteLine("Aborted.");
            return 1;
        }

        Console.WriteLine($"Running workflow {WorkflowFileName} in {target.Owner}/{target.Name}...");
        var inputs = new KeyValuePair<string, string>[]
        {
            new("buildVersion", buildVersion),
            new("name", releaseName),
            new("tag", tag),
            new("releaseType", releaseType),
            new("createReleaseBranch", createReleaseBranch),
            new("releaseBranchPrefix", releaseBranchPrefix),
            new("updateVersionNumber", updateVersionNumber),
            new("skipUpdatingDependencies", skipUpdatingDependencies),
            new("directCommit", directCommit),
            new("useGhTokenWorkflow", useGhTokenWorkflow),
        };

        if (!RunWorkflow(target, targetToken, WorkflowFileName, branch, inputs))
            return 1;

        Console.WriteLine();
        Console.WriteLine($"Release workflow triggered: {target.HttpsUrl}");
        return 0;
    }

    /// <summary>
    /// Interprets a boolean command-line flag as a workflow input value
    /// (<c>"true"</c>/<c>"false"</c>). A missing flag is <c>false</c>; a bare
    /// flag is <c>true</c>; an explicit value is parsed.
    /// </summary>
    private static string ParseBoolFlag(Dictionary<string, string> parameters, string key)
    {
        if (!parameters.TryGetValue(key, out var value))
            return "false";
        if (string.IsNullOrWhiteSpace(value))
            return "true"; // bare flag present
        var isTrue = value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value == "1";
        return isTrue ? "true" : "false";
    }

    /// <summary>
    /// Resolves a tag value. When <paramref name="tagPattern"/> ends in <c>*</c>
    /// (e.g. <c>1.0.*</c>) the repository's existing tags are queried; the
    /// highest matching tag has its trailing number incremented by one. When no
    /// tag matches, the first value is used (e.g. <c>1.0.0</c>). Any other value
    /// is returned unchanged.
    /// </summary>
    /// <returns>The resolved tag, or <c>null</c> if querying tags failed.</returns>
    private static string? ResolveTag(RepoSpec target, string token, string tagPattern)
    {
        if (!tagPattern.EndsWith('*'))
            return tagPattern;

        var prefix = tagPattern[..^1]; // strip the trailing '*'

        Console.WriteLine($"Resolving tag pattern '{tagPattern}' against existing tags...");
        var environment = new Dictionary<string, string>
        {
            ["GH_TOKEN"] = token,
            ["GH_HOST"] = target.Host,
        };
        var (exit, stdOut, err) = ProcessHelper.Run(
            "gh",
            ["api", "--paginate", $"repos/{target.Owner}/{target.Name}/tags", "--jq", ".[].name"],
            environment: environment);
        if (exit != 0)
        {
            Console.Error.WriteLine($"Error: failed to query tags for {target.Owner}/{target.Name}:{Environment.NewLine}{RepoCommandHelpers.Mask(err, token)}");
            return null;
        }

        var highest = -1;
        foreach (var line in stdOut.Split('\n'))
        {
            var name = line.Trim();
            if (name.Length == 0 || !name.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            var remainder = name[prefix.Length..];
            if (int.TryParse(remainder, out var value) && value >= 0 && value > highest)
                highest = value;
        }

        var resolved = $"{prefix}{highest + 1}";
        Console.WriteLine($"  Latest matching tag: {(highest < 0 ? "(none)" : $"{prefix}{highest}")} -> using {resolved}");
        return resolved;
    }
}
