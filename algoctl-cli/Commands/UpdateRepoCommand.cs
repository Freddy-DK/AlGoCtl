namespace AlGoCtl;

/// <summary>
/// <c>updaterepo</c> — copies the AL-Go system-file update workflow from a
/// template repository into an existing repository and triggers that workflow.
/// </summary>
internal static class UpdateRepoCommand
{
    public const string Name = "updaterepo";

    /// <summary>Path (relative to the repository root) of the workflow to copy and run.</summary>
    private const string WorkflowRelativePath = ".github/workflows/UpdateGitHubGoSystemFiles.yaml";

    /// <summary>File name of the workflow, used when triggering it via <c>gh workflow run</c>.</summary>
    private const string WorkflowFileName = "UpdateGitHubGoSystemFiles.yaml";

    public const string Help = """
        algoctl updaterepo — update an existing repository with the AL-Go system-file
        update workflow from a template repository, then run that workflow.

        Only the .github/workflows/UpdateGitHubGoSystemFiles.yaml file is copied; no
        other content from the template is touched.

        Usage:
            algoctl updaterepo --repo <owner/repo|url> --ghuser <user> [options]

        Options:
            --templaterepo <owner/repo|url>   Template repository to copy the workflow from.
                                              Short form (owner/repo) defaults to github.com;
                                              a full URL may target a GitHub Enterprise host.
                                              Default: https://github.com/Freddy-DK/AL-Go-PTE
            --templateghuser <user>           GitHub CLI account to use for reading the
                                              template repository. Default: the active account
                                              on the template host, or unauthenticated if none.
            --repo <owner/repo|url>           The repository to update. Short form defaults
                                              to github.com. (required)
            --ghuser <user>                   GitHub CLI account to use for updating the
                                              repository. Default: the active account on the
                                              target host.
            --branches <list>                 Comma-separated list of branches to update, passed
                                              to the workflow's includeBranches input. Wildcards
                                              are supported. Default: main
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

        // --branches (default: main) — passed to the workflow's includeBranches input.
        var branches = parameters.TryGetValue("branches", out var b) && !string.IsNullOrWhiteSpace(b)
            ? b
            : "main";

        // --confirm (flag) — skips the interactive confirmation prompt.
        var skipConfirm = parameters.ContainsKey("confirm");

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

        // Resolve the token for the account that will push to / trigger the workflow in the repo.
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

        Console.WriteLine("Updating repository with AL-Go from template:");
        Console.WriteLine($"  Template:      {template.HttpsUrl}");
        Console.WriteLine($"  Template auth: {(string.IsNullOrWhiteSpace(templateLogin) ? "(unauthenticated)" : $"{templateLogin} on {template.Host}")}");
        Console.WriteLine($"  Repository:    {target.HttpsUrl}");
        Console.WriteLine($"  Workflow:      {WorkflowRelativePath}");
        Console.WriteLine($"  Branches:      {branches}");
        Console.WriteLine($"  GitHub user:   {(string.IsNullOrWhiteSpace(targetLogin) ? $"(active account on {target.Host})" : $"{targetLogin} on {target.Host}")}");
        Console.WriteLine();

        if (!skipConfirm && !RepoCommandHelpers.Confirm())
        {
            Console.WriteLine("Aborted.");
            return 1;
        }

        var templateDir = Path.Combine(Path.GetTempPath(), $"algoctl-tmpl-{Guid.NewGuid():N}");
        var targetDir = Path.Combine(Path.GetTempPath(), $"algoctl-repo-{Guid.NewGuid():N}");

        try
        {
            // 1. Clone the template content (shallow) to obtain the workflow file.
            Console.WriteLine($"Downloading template content from {template.Owner}/{template.Name}...");
            var templateCloneUrl = templateToken is null
                ? template.HttpsUrl
                : template.AuthenticatedUrl(templateToken);
            var (tmplExit, _, tmplErr) = ProcessHelper.Run(
                "git",
                ["clone", "--depth", "1", templateCloneUrl, templateDir]);
            if (tmplExit != 0)
            {
                Console.Error.WriteLine($"Error: failed to clone template:{Environment.NewLine}{RepoCommandHelpers.Mask(tmplErr, templateToken)}");
                return 1;
            }

            var sourceWorkflow = Path.Combine(templateDir, ".github", "workflows", WorkflowFileName);
            if (!File.Exists(sourceWorkflow))
            {
                Console.Error.WriteLine($"Error: '{WorkflowRelativePath}' was not found in template {template.Owner}/{template.Name}.");
                return 1;
            }

            // 2. Clone the target repository (shallow, authenticated so we can push back).
            Console.WriteLine($"Cloning target repository {target.Owner}/{target.Name}...");
            var targetCloneUrl = target.AuthenticatedUrl(targetToken);
            var (repoExit, _, repoErr) = ProcessHelper.Run(
                "git",
                ["clone", "--depth", "1", targetCloneUrl, targetDir]);
            if (repoExit != 0)
            {
                Console.Error.WriteLine($"Error: failed to clone target repository:{Environment.NewLine}{RepoCommandHelpers.Mask(repoErr, targetToken)}");
                return 1;
            }

            // 3. Copy only the workflow file into the target repository.
            var destinationDir = Path.Combine(targetDir, ".github", "workflows");
            Directory.CreateDirectory(destinationDir);
            var destinationWorkflow = Path.Combine(destinationDir, WorkflowFileName);
            File.Copy(sourceWorkflow, destinationWorkflow, overwrite: true);

            // 4. Commit and push if the workflow changed.
            if (!RepoCommandHelpers.RunGit(targetDir, ["add", WorkflowRelativePath], targetToken)) return 1;

            var (statusExit, statusOut, statusErr) = ProcessHelper.Run(
                "git", ["status", "--porcelain"], targetDir);
            if (statusExit != 0)
            {
                Console.Error.WriteLine($"Error: git status failed:{Environment.NewLine}{RepoCommandHelpers.Mask(statusErr, targetToken)}");
                return 1;
            }

            if (string.IsNullOrWhiteSpace(statusOut))
            {
                Console.WriteLine("Workflow file is already up to date; no changes to push.");
            }
            else
            {
                Console.WriteLine("Committing and pushing the updated workflow...");
                if (!RepoCommandHelpers.RunGit(targetDir,
                        ["-c", "user.name=algoctl", "-c", "user.email=algoctl@users.noreply.github.com",
                         "commit", "-m", $"Update {WorkflowFileName} from template {template.Owner}/{template.Name}"],
                        targetToken))
                    return 1;
                if (!RepoCommandHelpers.RunGit(targetDir, ["push", "origin", "HEAD"], targetToken)) return 1;
            }

            // 5. Trigger the workflow in the target repository.
            Console.WriteLine($"Running workflow {WorkflowFileName} in {target.Owner}/{target.Name}...");
            var runEnv = new Dictionary<string, string>
            {
                ["GH_TOKEN"] = targetToken,
                ["GH_HOST"] = target.Host,
            };
            var (runExit, _, runErr) = ProcessHelper.Run(
                "gh",
                ["workflow", "run", WorkflowFileName, "--repo", $"{target.Owner}/{target.Name}",
                 "-f", $"templateUrl={template.HttpsUrl}",
                 "-f", "downloadLatest=true",
                 "-f", "directCommit=true",
                 "-f", $"includeBranches={branches}"],
                environment: runEnv);
            if (runExit != 0)
            {
                Console.Error.WriteLine($"Error: failed to run workflow:{Environment.NewLine}{RepoCommandHelpers.Mask(runErr, targetToken)}");
                return 1;
            }

            Console.WriteLine();
            Console.WriteLine($"Repository updated and workflow triggered: {target.HttpsUrl}");
            return 0;
        }
        finally
        {
            RepoCommandHelpers.TryDeleteDirectory(templateDir);
            RepoCommandHelpers.TryDeleteDirectory(targetDir);
        }
    }
}
