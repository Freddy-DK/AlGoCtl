namespace ALGoCtl;

/// <summary>
/// A parsed repository reference. Accepts either a full URL
/// (e.g. <c>https://github.com/Owner/Repo</c> or a GitHub Enterprise host) or a
/// short <c>owner/repo</c> form that defaults to <c>github.com</c>.
/// An optional <c>@branch</c> suffix selects a branch (e.g. <c>owner/repo@preview</c>).
/// </summary>
internal sealed record RepoSpec(string Host, string Owner, string Name, string? Branch = null)
{
    private const string DefaultHost = "github.com";

    public string HttpsUrl => $"https://{Host}/{Owner}/{Name}";

    /// <summary>Display form including the <c>@branch</c> suffix when one was specified.</summary>
    public string DisplayUrl => Branch is null ? HttpsUrl : $"{HttpsUrl}@{Branch}";

    /// <summary>Arguments for a shallow <c>git clone</c> of this repository (honoring <see cref="Branch"/>).</summary>
    public string[] ShallowCloneArgs(string url, string directory) => Branch is null
        ? ["clone", "--depth", "1", url, directory]
        : ["clone", "--depth", "1", "--branch", Branch, url, directory];

    /// <summary>Returns an HTTPS clone/push URL with an embedded access token.</summary>
    public string AuthenticatedUrl(string token) => $"https://x-access-token:{token}@{Host}/{Owner}/{Name}.git";

    public static RepoSpec Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new FormatException("Repository reference is empty.");

        input = input.Trim();

        string? branch = null;
        var at = input.LastIndexOf('@');
        if (at > input.LastIndexOf('/'))
        {
            branch = input[(at + 1)..];
            input = input[..at];
            if (string.IsNullOrWhiteSpace(branch))
                throw new FormatException("Branch name after '@' is empty.");
        }

        if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
                throw new FormatException($"'{input}' is not a valid repository URL.");

            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2)
                throw new FormatException($"'{input}' must include an owner and repository name.");

            return new RepoSpec(uri.Host, segments[^2], TrimGitSuffix(segments[^1]), branch);
        }

        // Short form: owner/repo (defaults to github.com).
        var parts = input.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            throw new FormatException($"'{input}' must be in the form owner/repo or a full URL.");

        return new RepoSpec(DefaultHost, parts[0], TrimGitSuffix(parts[1]), branch);
    }

    private static string TrimGitSuffix(string name) =>
        name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
}
