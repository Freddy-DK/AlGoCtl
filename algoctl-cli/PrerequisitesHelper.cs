namespace AlGoCtl;

/// <summary>
/// Verifies that the external command-line tools required by algoctl are
/// installed and recent enough to be useful.
/// </summary>
internal static class PrerequisitesHelper
{
    // `gh auth token --user` requires gh 2.40.0+.
    private static readonly Version MinGhVersion = new(2, 40, 0);

    // git 2.28+ honours init.defaultBranch.
    private static readonly Version MinGitVersion = new(2, 28, 0);

    /// <summary>Ensures the GitHub CLI (<c>gh</c>) is installed and recent enough.</summary>
    public static bool EnsureGhIsAvailable() =>
        EnsureToolAvailable("gh", ["--version"], MinGhVersion);

    /// <summary>Ensures <c>git</c> is installed and recent enough.</summary>
    public static bool EnsureGitIsAvailable() =>
        EnsureToolAvailable("git", ["--version"], MinGitVersion);

    private static bool EnsureToolAvailable(string tool, string[] versionArgs, Version minimumVersion)
    {
        var (exit, stdOut, stdErr) = ProcessHelper.Run(tool, versionArgs);
        if (exit != 0)
        {
            Console.Error.WriteLine($"Error: '{tool}' was not found on PATH or failed to run. Please install {tool} (>= {minimumVersion}).");
            var detail = string.IsNullOrWhiteSpace(stdErr) ? stdOut : stdErr;
            if (!string.IsNullOrWhiteSpace(detail))
                Console.Error.WriteLine(detail.Trim());
            return false;
        }

        var version = ParseVersion(stdOut);
        if (version is null)
        {
            Console.Error.WriteLine($"Warning: could not determine the {tool} version; continuing anyway.");
            return true;
        }

        if (version < minimumVersion)
        {
            Console.Error.WriteLine($"Error: {tool} {version} is too old. Please upgrade to {minimumVersion} or newer.");
            return false;
        }

        return true;
    }

    private static Version? ParseVersion(string versionOutput)
    {
        // Extract the first X.Y or X.Y.Z sequence (ignoring vendor suffixes like ".windows.1").
        var match = System.Text.RegularExpressions.Regex.Match(versionOutput, @"(\d+)\.(\d+)(?:\.(\d+))?");
        if (!match.Success)
            return null;

        var major = int.Parse(match.Groups[1].Value);
        var minor = int.Parse(match.Groups[2].Value);
        var patch = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
        return new Version(major, minor, patch);
    }
}
