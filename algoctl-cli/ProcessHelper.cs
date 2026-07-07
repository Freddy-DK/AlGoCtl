using System.Diagnostics;
using System.Text;

namespace AlGoCtl;

/// <summary>
/// Helper for running external processes (e.g. <c>gh</c> and <c>git</c>) and
/// capturing their output.
/// </summary>
internal static class ProcessHelper
{
    public static (int ExitCode, string StdOut, string StdErr) Run(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        IDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        if (!string.IsNullOrEmpty(workingDirectory))
            startInfo.WorkingDirectory = workingDirectory;

        if (environment is not null)
        {
            foreach (var kvp in environment)
                startInfo.Environment[kvp.Key] = kvp.Value;
        }

        using var process = new Process { StartInfo = startInfo };

        var stdOut = new StringBuilder();
        var stdErr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdOut.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stdErr.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, $"Failed to start '{fileName}': {ex.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();

        return (process.ExitCode, stdOut.ToString(), stdErr.ToString());
    }
}
