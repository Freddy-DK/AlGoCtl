namespace AlGoCtl;

/// <summary>
/// A parsed repository reference. Accepts either a full URL
/// (e.g. <c>https://github.com/Owner/Repo</c> or a GitHub Enterprise host) or a
/// short <c>owner/repo</c> form that defaults to <c>github.com</c>.
/// </summary>
internal sealed record RepoSpec(string Host, string Owner, string Name)
{
    private const string DefaultHost = "github.com";

    public string HttpsUrl => $"https://{Host}/{Owner}/{Name}";

    /// <summary>Returns an HTTPS clone/push URL with an embedded access token.</summary>
    public string AuthenticatedUrl(string token) => $"https://x-access-token:{token}@{Host}/{Owner}/{Name}.git";

    public static RepoSpec Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new FormatException("Repository reference is empty.");

        input = input.Trim();

        if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
                throw new FormatException($"'{input}' is not a valid repository URL.");

            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2)
                throw new FormatException($"'{input}' must include an owner and repository name.");

            return new RepoSpec(uri.Host, segments[^2], TrimGitSuffix(segments[^1]));
        }

        // Short form: owner/repo (defaults to github.com).
        var parts = input.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            throw new FormatException($"'{input}' must be in the form owner/repo or a full URL.");

        return new RepoSpec(DefaultHost, parts[0], TrimGitSuffix(parts[1]));
    }

    private static string TrimGitSuffix(string name) =>
        name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
}
