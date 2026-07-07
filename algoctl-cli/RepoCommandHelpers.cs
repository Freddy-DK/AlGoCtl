namespace AlGoCtl;

/// <summary>
/// Shared helpers used by the repository commands (<c>createrepo</c>,
/// <c>updaterepo</c>) for talking to the GitHub CLI and driving git.
/// </summary>
internal static class RepoCommandHelpers
{
    /// <summary>Runs a git command in <paramref name="workingDirectory"/> and reports failures.</summary>
    public static bool RunGit(string workingDirectory, string[] arguments, string? tokenToMask)
    {
        var (exit, _, err) = ProcessHelper.Run("git", arguments, workingDirectory);
        if (exit != 0)
        {
            Console.Error.WriteLine($"Error: git {arguments[0]} failed:{Environment.NewLine}{Mask(err, tokenToMask)}");
            return false;
        }
        return true;
    }

    /// <summary>Prompts the user to confirm the operation.</summary>
    public static bool Confirm()
    {
        Console.Write("Do you want to proceed? [y/N] ");
        var answer = Console.ReadLine()?.Trim();
        return string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)
            || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Resolves a GitHub token for the given host (optionally for a specific user).</summary>
    public static string? GetGhToken(string host, string? user)
    {
        var arguments = new List<string> { "auth", "token", "--hostname", host };
        if (!string.IsNullOrWhiteSpace(user))
        {
            arguments.Add("--user");
            arguments.Add(user);
        }

        var (exit, stdOut, _) = ProcessHelper.Run("gh", arguments);
        var token = stdOut.Trim();
        return exit == 0 && !string.IsNullOrWhiteSpace(token) ? token : null;
    }

    /// <summary>Resolves the login name for the account owning the given token on a host.</summary>
    public static string? ResolveLogin(string host, string token)
    {
        var env = new Dictionary<string, string>
        {
            ["GH_TOKEN"] = token,
            ["GH_HOST"] = host,
        };
        var (exit, stdOut, _) = ProcessHelper.Run("gh", ["api", "user", "--jq", ".login"], environment: env);
        var login = stdOut.Trim();
        return exit == 0 && !string.IsNullOrWhiteSpace(login) ? login : null;
    }

    /// <summary>Replaces occurrences of a secret in text with <c>***</c>.</summary>
    public static string Mask(string text, string? secret)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(text))
            return text;
        return text.Replace(secret, "***");
    }

    /// <summary>Best-effort recursive delete of a directory.</summary>
    public static void TryDeleteDirectory(string path)
    {
        try
        {
            ForceDeleteDirectory(path);
        }
        catch
        {
            // Best-effort cleanup of the temp directory.
        }
    }

    /// <summary>Deletes a directory, clearing read-only attributes git sets on Windows.</summary>
    public static void ForceDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;

        // Git marks files under .git read-only on Windows; clear the attribute first.
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            if (attributes.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        }

        Directory.Delete(path, recursive: true);
    }
}
