using System.Diagnostics;
using System.Text;
using BlueMail.TestSamples;

namespace BlueMail.FuzzTests;

/// <summary>
/// Short in-process mutation fuzzing that runs with every test run (PROMPT 8.1 "chạy trong CI một khoảng ngắn").
/// Deterministic (fixed seed) so a failure reproduces; the failing input is written to tests/interop/out/fuzz.
/// Long runs use libFuzzer through SharpFuzz (see Program.cs and docs/06).
/// </summary>
public sealed class MutationFuzzTests
{
    private static readonly int Iterations = int.TryParse(Environment.GetEnvironmentVariable("BM_FUZZ_ITERATIONS"), out var n) ? n : 1500;
    private static readonly TimeSpan PerInput = TimeSpan.FromSeconds(5);

    /// <summary>Time per target: 60 s in every test run, longer for scheduled runs (BM_FUZZ_SECONDS, docs/06).</summary>
    private static readonly TimeSpan PerTarget = TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("BM_FUZZ_SECONDS"), out var sec) ? sec : 60);

    public static TheoryData<string> Targets => new(FuzzTargets.All.Select(t => t.Name));

    [Theory]
    [MemberData(nameof(Targets))]
    public async Task No_unexpected_exception_hang_or_memory_blowup(string name)
    {
        var target = FuzzTargets.All.Single(t => t.Name == name);
        var seeds = Seeds(name);
        var rng = new Random(int.TryParse(Environment.GetEnvironmentVariable("BM_FUZZ_SEED"), out var seed) ? seed : 20261123);
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < Iterations && watch.Elapsed < PerTarget; i++)
        {
            var input = Mutate(seeds[rng.Next(seeds.Count)], seeds, rng);
            Exception? error = null;
            var task = Task.Run(() =>
            {
                try
                {
                    target.Run(input);
                }
                catch (Exception ex) when (target.Expected.Contains(ex.GetType()))
                {
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            });
            var finished = await Task.WhenAny(task, Task.Delay(PerInput)) == task;
            if (!finished || error is not null)
            {
                var file = Save(name, i, input);
                Assert.Fail(finished ? $"{name} #{i}: {error!.GetType().Name}: {error.Message} (input {file})\n{error.StackTrace}"
                                     : $"{name} #{i}: no result after {PerInput.TotalSeconds}s (input {file})");
            }

            GC.GetTotalMemory(false).Should().BeLessThan(1_500_000_000, $"{name} #{i} keeps memory bounded");
        }
    }

    private static string Save(string target, int i, byte[] input)
    {
        var dir = Path.Combine(Root(), "tests", "interop", "out", "fuzz");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{target}-{i}.bin");
        File.WriteAllBytes(path, input);
        return path;
    }

    private static List<byte[]> Seeds(string target)
    {
        var corpus = Directory.EnumerateFiles(Path.Combine(Root(), "tests", "BlueMail.Corpus", "messages"), "*.eml", SearchOption.AllDirectories)
            .Select(File.ReadAllBytes).ToList();
        return target switch
        {
            "dns" =>
            [
                BlueMail.Dns.DnsWire.EncodeQuery(1, "mx.bluemail.test", BlueMail.Dns.DnsType.MX, 1232, false),
                [0x12, 0x34, 0x81, 0x80, 0, 1, 0, 1, 0, 0, 0, 0, 3, (byte)'a', (byte)'b', (byte)'c', 0, 0, 1, 0, 1, 0xC0, 12, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, 10, 0, 0, 1],
                [0x12, 0x34, 0x81, 0x80, 0, 1, 0, 1, 0, 0, 0, 0, 3, (byte)'a', (byte)'b', (byte)'c', 0, 0, 16, 0, 1, 0xC0, 12, 0, 16, 0, 1, 0, 0, 0, 60, 0, 6, 5, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o'],
                DnssecSeed(),
            ],
            "mime-headers" =>
            [
                Encoding.UTF8.GetBytes("\"Le, Minh\" <minh@x.test>, Team: a@b.test;, =?utf-8?B?TMOibg==?= <lan@y.test>"),
                Encoding.UTF8.GetBytes("Mon, 2 Nov 2026 09:15:00 +0700 (ICT)"),
                Encoding.UTF8.GetBytes("attachment; filename*0*=utf-8''%E1%BA%A1; filename*1=b.pdf"),
            ],
            "scanner" => [.. ScannerSamples.Dangerous().Concat(ScannerSamples.Clean()).Where(s => s.Data.Length < 2_000_000).Select(s => s.Data)],
            "imap" =>
            [
                Encoding.UTF8.GetBytes("* OK [CAPABILITY IMAP4rev1 LITERAL+ AUTH=PLAIN] ready\r\n* LIST (\\HasNoChildren \\Sent) \"/\" \"Sent\"\r\n* 3 EXISTS\r\nA1 OK [READ-WRITE] done\r\n"),
                Encoding.UTF8.GetBytes("* 1 FETCH (UID 7 FLAGS (\\Seen) INTERNALDATE \" 7-Feb-2026 09:05:00 +0700\" RFC822.SIZE 12 X-GM-LABELS (\\Inbox \"Dự án\") BODY[] {12}\r\nSubject: x\r\n)\r\n"),
                Encoding.UTF8.GetBytes("* LIST () \"/\" &U,BTFw-/&ZeVnLIqe-\r\n* SEARCH 1 2 3\r\n+ go\r\n* BYE bye\r\n"),
            ],
            "smtp" =>
            [
                Encoding.ASCII.GetBytes("\x01DATA\r\nSubject: x\r\n\r\n..dot\r\n.\r\nQUIT\r\n"),
                Encoding.ASCII.GetBytes("\x01BDAT 5\r\nabcdeBDAT 3 LAST\r\nxyzRSET\r\nBDAT 2\r\nhi"),
                Encoding.UTF8.GetBytes("EHLO a\r\nMAIL FROM:<ân@ví.test> SMTPUTF8 SIZE=10 BODY=8BITMIME\r\nRCPT TO:<b@c> NOTIFY=NEVER\r\nDATA\r\nx\n.\nVRFY x\r\nSTARTTLS\r\n"),
                Encoding.ASCII.GetBytes("HELO\r\nMAIL FROM:<>\r\nRCPT TO:<\"q uo\\\"te\"@[192.0.2.1]>\r\nBDAT 99999999999999999999\r\nNOOP\r\n"),
            ],
            "text" =>
            [
                Encoding.UTF8.GetBytes("RE[2]: Fwd: TL: [team] 回复：Báo giá"),
                Encoding.UTF8.GetBytes("v=DMARC1; p=reject; sp=none; pct=50; adkim=s; aspf=r; rua=mailto:a@b"),
                Encoding.UTF8.GetBytes("MAIL FROM:<@a,@b:x@y.test> SIZE=1 SIZE=1"),
                Encoding.UTF8.GetBytes("สวัสดี 合同已经签署 👨‍👩‍👧 \0 �"),
            ],
            _ => corpus,
        };
    }

    /// <summary>Bit flips, byte edits, insertions of interesting tokens, truncation, duplication and splicing.</summary>
    private static byte[] Mutate(byte[] seed, List<byte[]> all, Random rng)
    {
        var data = new List<byte>(seed);
        var rounds = 1 + rng.Next(8);
        byte[][] tokens =
        [
            "\r\n"u8.ToArray(), "\n"u8.ToArray(), "--"u8.ToArray(), "=?utf-8?B?"u8.ToArray(), "?="u8.ToArray(), "Content-Type: multipart/mixed; boundary=X\r\n"u8.ToArray(),
            ":"u8.ToArray(), ";"u8.ToArray(), "\""u8.ToArray(), "("u8.ToArray(), "<"u8.ToArray(), [0], [0xFF], [0xC0, 12], "PK\x03\x04"u8.ToArray(), "%PDF-"u8.ToArray(),
            "stream\r\n"u8.ToArray(), "/FlateDecode"u8.ToArray(), "b="u8.ToArray(), "i=1;"u8.ToArray(),
        ];
        for (var r = 0; r < rounds; r++)
        {
            var pos = data.Count == 0 ? 0 : rng.Next(data.Count);
            switch (rng.Next(7))
            {
                case 0 when data.Count > 0:
                    data[pos] ^= (byte)(1 << rng.Next(8));
                    break;
                case 1 when data.Count > 0:
                    data[pos] = (byte)rng.Next(256);
                    break;
                case 2:
                    data.InsertRange(pos, tokens[rng.Next(tokens.Length)]);
                    break;
                case 3 when data.Count > 1:
                    data.RemoveRange(pos, Math.Min(data.Count - pos, 1 + rng.Next(64)));
                    break;
                case 4 when data.Count > 0:
                    var len = Math.Min(data.Count - pos, 1 + rng.Next(256));
                    data.InsertRange(pos, data.GetRange(pos, len));
                    break;
                case 5:
                    var other = all[rng.Next(all.Count)];
                    var cut = other.Length == 0 ? 0 : rng.Next(other.Length);
                    data = [.. data.Take(pos), .. other.Skip(cut)];
                    break;
                default:
                    if (data.Count > 0)
                    {
                        data.RemoveRange(pos, data.Count - pos);
                    }

                    break;
            }
        }

        return [.. data];
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BlueMail.slnx")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }

    /// <summary>A response with the DNSSEC record types (RRSIG, DNSKEY, DS, NSEC, NSEC3) for the "dns" target.</summary>
    private static byte[] DnssecSeed()
    {
        var b = new List<byte> { 0x12, 0x34, 0x81, 0xA0, 0, 1, 0, 5, 0, 0, 0, 0, 3, (byte)'a', (byte)'b', (byte)'c', 0, 0, 1, 0, 1 };
        void Rr(int type, byte[] rdata)
        {
            b.AddRange([0xC0, 12, (byte)(type >> 8), (byte)type, 0, 1, 0, 0, 0, 60, (byte)(rdata.Length >> 8), (byte)rdata.Length]);
            b.AddRange(rdata);
        }

        Rr(46, [0, 1, 13, 1, 0, 0, 0, 60, 0x70, 0, 0, 0, 0x60, 0, 0, 0, 0xEE, 0x42, 0xC0, 12, 1, 2, 3, 4, 5, 6, 7, 8]); // RRSIG
        Rr(48, [1, 1, 3, 13, 9, 8, 7, 6, 5, 4, 3, 2]); // DNSKEY
        Rr(43, [0xEE, 0x42, 13, 2, 1, 2, 3, 4, 5, 6, 7, 8]); // DS
        Rr(47, [0xC0, 12, 0, 6, 0x40, 0, 0, 0, 0, 0x03]); // NSEC: next name, bitmap A, RRSIG, NSEC
        Rr(50, [1, 1, 0, 10, 2, 0xAB, 0xCD, 4, 1, 2, 3, 4, 0, 1, 0x40]); // NSEC3 (opt-out), bitmap A
        return [.. b];
    }
}

