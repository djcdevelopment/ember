using System.Diagnostics;

namespace Ember.Build;

/// <summary>Runs a child process to completion and captures its output. For short,
/// non-streaming commands (git plumbing, diff stats) — the builder process itself is
/// streamed directly in <see cref="BuilderRunner"/>.</summary>
public static class ProcessRunner
{
    /// <summary>Exit code and captured streams of a finished process.</summary>
    public sealed record Result(int ExitCode, string StdOut, string StdErr)
    {
        public bool Ok => ExitCode == 0;
    }

    /// <summary>
    /// Starts <paramref name="fileName"/> with the given arguments, waits for it to exit,
    /// and returns its output. If <paramref name="ct"/> fires, the process tree is killed.
    /// </summary>
    public static async Task<Result> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        string? workingDirectory,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        process.Start();

        await using var kill = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The process already exited — nothing to kill.
            }
        });

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return new Result(process.ExitCode, await stdout, await stderr);
    }
}
