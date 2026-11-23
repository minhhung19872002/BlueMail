namespace BlueMail.Antispam;

public enum BayesScope { Global = 0, Tenant = 1, User = 2 }

/// <summary>Counts of one token in one model.</summary>
public readonly record struct TokenCounts(int Spam, int Ham);

/// <summary>A trained model: per-token counts plus how many spam / ham messages it learned from.</summary>
public interface IBayesModel
{
    BayesScope Scope { get; }
    int SpamMessages { get; }
    int HamMessages { get; }
    TokenCounts Get(long tokenId);
}

public sealed class InMemoryBayesModel(BayesScope scope) : IBayesModel
{
    private readonly Dictionary<long, TokenCounts> _tokens = [];

    public BayesScope Scope { get; } = scope;
    public int SpamMessages { get; private set; }
    public int HamMessages { get; private set; }

    public TokenCounts Get(long tokenId) => _tokens.GetValueOrDefault(tokenId);

    public void Learn(IEnumerable<string> tokens, bool spam)
    {
        foreach (var id in tokens.Select(Tokenizer.Id).Distinct())
        {
            var c = _tokens.GetValueOrDefault(id);
            _tokens[id] = spam ? c with { Spam = c.Spam + 1 } : c with { Ham = c.Ham + 1 };
        }

        if (spam)
        {
            SpamMessages++;
        }
        else
        {
            HamMessages++;
        }
    }

    public void Set(long tokenId, TokenCounts counts) => _tokens[tokenId] = counts;

    public void SetTotals(int spam, int ham)
    {
        SpamMessages = spam;
        HamMessages = ham;
    }
}

public sealed record BayesResult(double Probability, BayesScope? Model, int TokensUsed, IReadOnlyList<(string Token, double P)> Strongest);

/// <summary>
/// Naive Bayes in the SpamBayes form (Robinson / Fisher, PROMPT 2.3): each token's spam probability is
/// smoothed toward 0.5 with strength s (so rare tokens say little), the 150 most decisive tokens are combined with
/// Fisher's chi-square method into S (spamminess) and H (hamminess), and the result is (1 + S - H) / 2.
/// Models are user, tenant and global; a model takes part only after it learned enough of both classes, and more
/// specific models weigh more (user 3, tenant 2, global 1).
/// </summary>
public static class Bayes
{
    public const double Strength = 0.45;      // s
    public const double Prior = 0.5;          // x
    public const double MinDeviation = 0.1;   // tokens with |p - 0.5| below this are ignored
    public const int MaxDiscriminators = 150;
    public const int MinTrainingPerClass = 20;

    public static BayesResult? Classify(IReadOnlyList<string> tokens, IReadOnlyList<IBayesModel> models)
    {
        double weighted = 0, weights = 0;
        BayesResult? best = null;
        foreach (var m in models.Where(m => m.SpamMessages >= MinTrainingPerClass && m.HamMessages >= MinTrainingPerClass))
        {
            var r = ClassifyOne(tokens, m);
            if (r.TokensUsed == 0)
            {
                continue;
            }

            var w = m.Scope switch { BayesScope.User => 3.0, BayesScope.Tenant => 2.0, _ => 1.0 };
            weighted += r.Probability * w;
            weights += w;
            if (best is null || m.Scope > best.Model)
            {
                best = r;
            }
        }

        return weights == 0 ? null : best! with { Probability = weighted / weights };
    }

    public static BayesResult ClassifyOne(IReadOnlyList<string> tokens, IBayesModel m)
    {
        var scored = new List<(string Token, double P)>();
        foreach (var t in tokens.Distinct())
        {
            var c = m.Get(Tokenizer.Id(t));
            var n = c.Spam + c.Ham;
            if (n == 0)
            {
                continue;
            }

            // Ratio of frequencies, so unequal training sizes do not bias the result.
            var spamRatio = (double)c.Spam / Math.Max(1, m.SpamMessages);
            var hamRatio = (double)c.Ham / Math.Max(1, m.HamMessages);
            var p = spamRatio / (spamRatio + hamRatio);
            var f = ((Strength * Prior) + (n * p)) / (Strength + n);
            if (Math.Abs(f - 0.5) >= MinDeviation)
            {
                scored.Add((t, Math.Clamp(f, 0.0001, 0.9999)));
            }
        }

        var top = scored.OrderByDescending(x => Math.Abs(x.P - 0.5)).Take(MaxDiscriminators).ToList();
        if (top.Count == 0)
        {
            return new BayesResult(0.5, m.Scope, 0, []);
        }

        var s = ChiSquareQ(-2 * top.Sum(x => Math.Log(1 - x.P)), 2 * top.Count);
        var h = ChiSquareQ(-2 * top.Sum(x => Math.Log(x.P)), 2 * top.Count);
        // S = 1 - Q(spam evidence), H = 1 - Q(ham evidence); indicator in [0, 1].
        var prob = (1 + (1 - s) - (1 - h)) / 2;
        return new BayesResult(prob, m.Scope, top.Count, top.Take(10).ToList());
    }

    /// <summary>Upper tail of chi-square with even degrees of freedom (exact series).</summary>
    public static double ChiSquareQ(double x2, int v)
    {
        var m = x2 / 2.0;
        var term = Math.Exp(-m);
        var sum = term;
        for (var i = 1; i < v / 2; i++)
        {
            term *= m / i;
            sum += term;
        }

        return Math.Min(sum, 1.0);
    }
}
