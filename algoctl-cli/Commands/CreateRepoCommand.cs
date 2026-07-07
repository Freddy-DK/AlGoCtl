namespace AlGoCtl;

/// <summary>
/// <c>createrepo</c> — creates a new GitHub repository (via the GitHub CLI) and
/// seeds it with the contents of a template repository.
/// </summary>
internal static class CreateRepoCommand
{
    public const string Name = "createrepo";

    public const string Help = """
        algoctl createrepo — create a new repository from a template repository.

        Usage:
            algoctl createrepo --repo <owner/repo|url> --ghuser <user> [options]

        Options:
            --templaterepo <owner/repo|url>   Template repository to copy content from.
                                              Short form (owner/repo) defaults to github.com;
                                              a full URL may target a GitHub Enterprise host.
                                              Default: https://github.com/Freddy-DK/AL-Go-PTE
            --templateghuser <user>           GitHub CLI account to use for reading the
                                              template repository. Default: the active account
                                              on the template host, or unauthenticated if none.
            --repo <owner/repo|url>           The repository to create. Short form defaults
                                              to github.com. (required)
            --ghuser <user>                   GitHub CLI account to use for creating the
                                              repository. Default: the active account on the
                                              target host.
            --visibility <private|internal|public>
                                              Visibility of the new repository.
                                              Default: private
            --confirm                         Skip the interactive confirmation prompt.
        """;

    public static int Run(string[] args)
    {
        var parameters = ArgParser.Parse(args.Skip(1));

        // --repo (required)
        if (!parameters.TryGetValue("repo", out var repoInput) || string.IsNullOrWhiteSpace(repoInput))
        {
            Console.Error.WriteLine("Error: --repo is required (e.g. owner/repo or a full URL).");
            Console.Error.WriteLine();
            Console.Error.WriteLine(Help);
            return 1;
        }

        // --ghuser (optional) — defaults to the active account on the target host.
        parameters.TryGetValue("ghuser", out var ghUser);

        // --templaterepo (default: Freddy-DK/AL-Go-PTE on github.com)
        var templateInput = parameters.TryGetValue("templaterepo", out var t) && !string.IsNullOrWhiteSpace(t)
            ? t
            : "https://github.com/Freddy-DK/AL-Go-PTE";

        // --templateghuser (optional)
        parameters.TryGetValue("templateghuser", out var templateGhUser);

        // --confirm (flag) — skips the interactive confirmation prompt.
        var skipConfirm = parameters.ContainsKey("confirm");

        // --visibility (default: private)
        var visibility = parameters.TryGetValue("visibility", out var v) && !string.IsNullOrWhiteSpace(v)
            ? v.ToLowerInvariant()
            : "private";
        if (visibility is not ("private" or "internal" or "public"))
        {
            Console.Error.WriteLine($"Error: --visibility must be one of private, internal or public (got '{visibility}').");
            return 1;
        }

        RepoSpec target, template;
        try
        {
            target = RepoSpec.Parse(repoInput);
            template = RepoSpec.Parse(templateInput);
        }
        catch (FormatException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }

        // Verify the external tools we depend on are installed and recent enough.
        if (!PrerequisitesHelper.EnsureGitIsAvailable()) return 1;
        if (!PrerequisitesHelper.EnsureGhIsAvailable()) return 1;

        // Resolve the token for the account that will create/push to the new repo.
        // If --ghuser was supplied, use that account; otherwise use the active account on the host.
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

        // Resolve the token for reading the template.
        // - If --templateghuser was supplied, that account is required.
        // - Otherwise fall back to the active account on the template host, and
        //   if there is none, read the template unauthenticated.
        string? templateToken;
        if (!string.IsNullOrWhiteSpace(templateGhUser))
        {
            templateToken = RepoCommandHelpers.GetGhToken(template.Host, templateGhUser);
            if (templateToken is null)
            {
                Console.Error.WriteLine($"Error: could not obtain a GitHub token for template user '{templateGhUser}' on '{template.Host}'.");
                return 1;
            }
        }
        else
        {
            templateToken = RepoCommandHelpers.GetGhToken(template.Host, null); // active account, or null (unauthenticated)
        }
        var templateLogin = templateToken is null ? null : (RepoCommandHelpers.ResolveLogin(template.Host, templateToken) ?? templateGhUser);

        Console.WriteLine("Creating repository from template:");
        Console.WriteLine($"  Template:      {template.HttpsUrl}");
        Console.WriteLine($"  Template auth: {(string.IsNullOrWhiteSpace(templateLogin) ? "(unauthenticated)" : $"{templateLogin} on {template.Host}")}");
        Console.WriteLine($"  New repo:      {target.HttpsUrl}");
        Console.WriteLine($"  Visibility:    {visibility}");
        Console.WriteLine($"  GitHub user:   {(string.IsNullOrWhiteSpace(targetLogin) ? $"(active account on {target.Host})" : $"{targetLogin} on {target.Host}")}");
        Console.WriteLine();

        if (!skipConfirm && !RepoCommandHelpers.Confirm())
        {
            Console.WriteLine("Aborted.");
            return 1;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"algoctl-{Guid.NewGuid():N}");

        try
        {
            // 1. Create the new (empty) repository via gh CLI.
            Console.WriteLine($"Creating {visibility} repository {target.Owner}/{target.Name} on {target.Host}...");
            var createEnv = new Dictionary<string, string>
            {
                ["GH_TOKEN"] = targetToken,
                ["GH_HOST"] = target.Host,
            };
            var (createExit, _, createErr) = ProcessHelper.Run(
                "gh",
                ["repo", "create", $"{target.Owner}/{target.Name}", $"--{visibility}"],
                environment: createEnv);
            if (createExit != 0)
            {
                Console.Error.WriteLine($"Error: failed to create repository:{Environment.NewLine}{RepoCommandHelpers.Mask(createErr, targetToken)}");
                return 1;
            }

            // 2. Clone the template content (shallow).
            Console.WriteLine($"Downloading template content from {template.Owner}/{template.Name}...");
            var templateCloneUrl = templateToken is null
                ? template.HttpsUrl
                : template.AuthenticatedUrl(templateToken);
            var (cloneExit, _, cloneErr) = ProcessHelper.Run(
                "git",
                ["clone", "--depth", "1", templateCloneUrl, tempDir]);
            if (cloneExit != 0)
            {
                Console.Error.WriteLine($"Error: failed to clone template:{Environment.NewLine}{RepoCommandHelpers.Mask(cloneErr, templateToken)}");
                return 1;
            }

            // 3. Re-initialize as a fresh repository (drop template history).
            var gitDir = Path.Combine(tempDir, ".git");
            RepoCommandHelpers.ForceDeleteDirectory(gitDir);

            if (!RepoCommandHelpers.RunGit(tempDir, ["-c", "init.defaultBranch=main", "init"], targetToken)) return 1;
            if (!RepoCommandHelpers.RunGit(tempDir, ["add", "-A"], targetToken)) return 1;
            if (!RepoCommandHelpers.RunGit(tempDir,
                    ["-c", "user.name=algoctl", "-c", "user.email=algoctl@users.noreply.github.com",
                     "commit", "-m", $"Initial commit from template {template.Owner}/{template.Name}"],
                    targetToken))
                return 1;

            // 4. Push to the new repository.
            Console.WriteLine("Pushing content to the new repository...");
            var pushUrl = target.AuthenticatedUrl(targetToken);
            if (!RepoCommandHelpers.RunGit(tempDir, ["remote", "add", "origin", pushUrl], targetToken)) return 1;
            if (!RepoCommandHelpers.RunGit(tempDir, ["push", "-u", "origin", "HEAD:main"], targetToken)) return 1;

            Console.WriteLine();
            Console.WriteLine($"Repository created successfully: {target.HttpsUrl}");
            return 0;
        }
        finally
        {
            RepoCommandHelpers.TryDeleteDirectory(tempDir);
        }
    }
}
