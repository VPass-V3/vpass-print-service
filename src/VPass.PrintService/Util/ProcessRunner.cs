using System.Diagnostics;

namespace VPass.PrintService.Util;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

public static class ProcessRunner
{
    public static ProcessResult Run(string fileName, string[] args, TimeSpan timeout, bool throwOnTimeout = true)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {fileName}");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            if (throwOnTimeout) throw new TimeoutException($"{fileName} did not finish within {timeout.TotalSeconds:0}s");
            return new ProcessResult(-1, stdout.Result, stderr.Result);
        }
        return new ProcessResult(process.ExitCode, stdout.Result, stderr.Result);
    }
}
