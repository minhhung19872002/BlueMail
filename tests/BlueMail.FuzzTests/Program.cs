using SharpFuzz;

namespace BlueMail.FuzzTests;

/// <summary>
/// Long fuzzing runs with libFuzzer through SharpFuzz (Linux container, see docs/06):
///   dotnet publish tests/BlueMail.FuzzTests -c Release -o out
///   sharpfuzz out/BlueMail.Mime.dll   (instrument each library under test)
///   ./libfuzzer-dotnet --target_path=out/BlueMail.FuzzTests -- mime  corpus/
/// The target name is the first argument (see <see cref="FuzzTargets.All"/>).
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var name = args.Length > 0 ? args[0] : "";
        var target = FuzzTargets.All.FirstOrDefault(t => t.Name == name);
        if (target is null)
        {
            Console.Error.WriteLine("usage: BlueMail.FuzzTests <" + string.Join('|', FuzzTargets.All.Select(t => t.Name)) + ">");
            return 2;
        }

        Fuzzer.LibFuzzer.Run(span =>
        {
            try
            {
                target.Run(span.ToArray());
            }
            catch (Exception ex) when (target.Expected.Contains(ex.GetType()))
            {
                // documented failure mode of the parser
            }
        });
        return 0;
    }
}
