using System.Diagnostics;
using System.Text;
using BlueMail.Scanner;
using BlueMail.TestSamples;

namespace BlueMail.InteropTests;

/// <summary>
/// BlueMail.Scanner vs ClamAV on the sample sets (PROMPT 11). The two block in different ways, so agreement is
/// not required; every disagreement is listed in tests/interop/out/scanner-vs-clamav.md for review.
/// </summary>
public sealed class ScannerClamAvComparisonTests
{
    [Fact]
    public async Task Disagreements_with_ClamAV_are_reported()
    {
        // Samples go to ClamAV as a tar on stdin, never to the host disk: a host antivirus (Windows Defender)
        // deletes the EICAR samples on write, which silently weakened the first version of this test.
        var samples = ScannerSamples.Dangerous().Select(s => (Set: "dangerous", s)).Concat(ScannerSamples.Clean().Select(s => (Set: "clean", s))).ToList();
        var files = new Dictionary<string, (string Set, ScannerSamples.Sample Sample)>();
        using var tar = new MemoryStream();
        using (var writer = new System.Formats.Tar.TarWriter(tar, leaveOpen: true))
        {
            var i = 0;
            foreach (var (set, s) in samples)
            {
                var file = $"{i++:D2}-{set}{Path.GetExtension(s.Name.Replace("‮", "", StringComparison.Ordinal))}";
                var entry = new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, $"{set}/{file}") { DataStream = new MemoryStream(s.Data) };
                writer.WriteEntry(entry);
                files[$"/tmp/samples/{set}/{file}"] = (set, s);
            }
        }

        var psi = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var a in new[] { "compose", "-f", Path.Combine(ToolsContainer.RepoRoot, "deploy", "docker-compose.dev.yml"), "--env-file",
                     Path.Combine(ToolsContainer.RepoRoot, "deploy", ".env.dev"), "--profile", "scanner-compare", "run", "--rm", "-T",
                     "--entrypoint", "sh", "clamav", "-c",
                     "mkdir -p /tmp/samples && tar -x -C /tmp/samples && clamscan --no-summary --infected --recursive --scan-archive=yes --alert-encrypted=yes /tmp/samples" })
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment["MSYS_NO_PATHCONV"] = "1";
        using var p = Process.Start(psi)!;
        tar.Position = 0;
        await tar.CopyToAsync(p.StandardInput.BaseStream);
        p.StandardInput.Close();
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        var stdout = await stdoutTask;
        await p.WaitForExitAsync();
        p.ExitCode.Should().BeOneOf([0, 1], "clamscan exits 0 (clean) or 1 (found); other codes are errors: " + stderr);

        var clamFound = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.Contains(" FOUND", StringComparison.Ordinal))
            .ToDictionary(l => l[..l.IndexOf(':', StringComparison.Ordinal)], l => l[(l.IndexOf(':', StringComparison.Ordinal) + 1)..].Replace("FOUND", "", StringComparison.Ordinal).Trim());

        var scanner = new BlueMailScanner();
        var report = new StringBuilder("# BlueMail.Scanner vs ClamAV\n\n| File | Set | BlueMail | ClamAV |\n|---|---|---|---|\n");
        var disagreements = 0;
        foreach (var (path, (set, s)) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            var ours = await scanner.ScanAsync(new MemoryStream(s.Data), s.Name);
            var oursFlagged = ours.Status != ScanStatus.Clean;
            var clamFlagged = clamFound.TryGetValue(path, out var sig);
            if (oursFlagged != clamFlagged)
            {
                disagreements++;
                report.Append($"| {s.Name} | {set} | {ours.Status} {string.Join(",", ours.Findings.Select(f => f.Code).Distinct())} | {(clamFlagged ? sig : "OK")} |\n");
            }
        }

        report.Append($"\n{disagreements} disagreement(s) out of {files.Count} samples.\n");
        await File.WriteAllTextAsync(Path.Combine(ToolsContainer.RepoRoot, "tests", "interop", "out", "scanner-vs-clamav.md"), report.ToString());

        // Sanity: the comparison is real only if ClamAV saw the files and recognised the standard test file.
        clamFound.Keys.Should().Contain(k => files[k].Sample.Name == "eicar.com", "ClamAV must detect EICAR, otherwise it did not scan");
    }
}
