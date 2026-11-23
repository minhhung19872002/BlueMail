using System.Diagnostics;
using System.Text;

namespace BlueMail.InteropTests;

/// <summary>
/// Runs reference tools inside the dev "tools" container (PROMPT Z2). The container must be running:
///   docker compose -f deploy/docker-compose.dev.yml --env-file deploy/.env.dev up -d
/// Interop tests fail (not skip) when it is missing: an untested comparison is not a pass.
/// </summary>
public static class ToolsContainer
{
    public static string Name => Environment.GetEnvironmentVariable("BM_TOOLS_CONTAINER") ?? "bluemail-dev-tools-1";

    public static string RepoRoot { get; } = FindRoot();

    public static (int ExitCode, string Stdout, string Stderr) Exec(params string[] args) => ExecWithInput(null, args);

    public static (int ExitCode, string Stdout, string Stderr) ExecWithInput(byte[]? stdin, params string[] args)
    {
        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("exec");
        if (stdin is not null)
        {
            psi.ArgumentList.Add("-i");
        }

        psi.ArgumentList.Add(Name);
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment["MSYS_NO_PATHCONV"] = "1";
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("docker not found");
        if (stdin is not null)
        {
            p.StandardInput.BaseStream.Write(stdin);
            p.StandardInput.Close();
        }

        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(TimeSpan.FromMinutes(3)))
        {
            p.Kill(true);
            throw new TimeoutException("docker exec timed out: " + string.Join(' ', args));
        }

        var stdout = stdoutTask.Result;
        var stderr = stderrTask.Result;
        if (p.ExitCode != 0 && stderr.Contains("No such container", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Reference tools container '{Name}' is not running. Start the dev stack (docs/06).");
        }

        return (p.ExitCode, stdout, stderr);
    }

    /// <summary>Path of a repository file as seen inside the container (repo is mounted at /work).</summary>
    public static string InContainer(string repoRelative) => "/work/" + repoRelative.Replace('\\', '/');

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BlueMail.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
