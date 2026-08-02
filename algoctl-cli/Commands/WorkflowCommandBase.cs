namespace ALGoCtl;

/// <summary>
/// Base class for commands that trigger a GitHub Actions workflow in a target
/// repository via the GitHub CLI (<c>gh workflow run</c>). Derived commands
/// gather their own inputs and delegate the actual dispatch to
/// <see cref="RunWorkflow"/>.
/// </summary>
internal abstract class WorkflowCommandBase
{
    /// <summary>
    /// Triggers a workflow in <paramref name="target"/> using the GitHub CLI.
    /// </summary>
    /// <param name="target">The repository that owns the workflow.</param>
    /// <param name="token">GitHub token used to authenticate the dispatch.</param>
    /// <param name="workflowFileName">File name of the workflow (e.g. <c>CreateRelease.yaml</c>).</param>
    /// <param name="branch">
    /// Branch (ref) to run the workflow on. When null or empty the repository's
    /// default branch is used.
    /// </param>
    /// <param name="inputs">Workflow <c>workflow_dispatch</c> inputs (key/value pairs).</param>
    /// <returns><c>true</c> if the workflow was dispatched successfully.</returns>
    protected static bool RunWorkflow(
        RepoSpec target,
        string token,
        string workflowFileName,
        string? branch,
        IEnumerable<KeyValuePair<string, string>> inputs)
    {
        var arguments = new List<string>
        {
            "workflow", "run", workflowFileName,
            "--repo", $"{target.Owner}/{target.Name}",
        };

        if (!string.IsNullOrWhiteSpace(branch))
        {
            arguments.Add("--ref");
            arguments.Add(branch);
        }

        foreach (var input in inputs)
        {
            arguments.Add("-f");
            arguments.Add($"{input.Key}={input.Value}");
        }

        var environment = new Dictionary<string, string>
        {
            ["GH_TOKEN"] = token,
            ["GH_HOST"] = target.Host,
        };

        var (exit, _, err) = ProcessHelper.Run("gh", arguments, environment: environment);
        if (exit != 0)
        {
            Console.Error.WriteLine($"Error: failed to run workflow:{Environment.NewLine}{RepoCommandHelpers.Mask(err, token)}");
            return false;
        }

        return true;
    }
}
