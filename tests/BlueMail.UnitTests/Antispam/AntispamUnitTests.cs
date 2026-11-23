using System.Net;
using BlueMail.Antispam;
using BlueMail.Application.Features.Antispam;

namespace BlueMail.UnitTests.Antispam;

public class TokenizerTests
{
    [Fact]
    public void Latin_words_get_accent_free_twins_cjk_and_thai_become_bigrams()
    {
        Tokenizer.Tokens("Khuyến mãi lớn").Should().Contain(["khuyến", "khuyen", "mãi", "mai", "lớn", "lon"]);
        Tokenizer.Tokens("合同已经签署").Should().Equal("合同", "同已", "已经", "经签", "签署");
        Tokenizer.Tokens("สวัสดีครับ").Should().NotBeEmpty();
        Tokenizer.Tokens("Re: Đơn hàng", "subj:").Should().Contain(["subj:re", "subj:đơn", "subj:don", "subj:hàng", "subj:hang"]);
        Tokenizer.Tokens("a " + new string('x', 200)).Should().Contain("skip:200", "very long words keep only their length class");
    }

    [Fact]
    public void Token_ids_are_stable()
    {
        Tokenizer.Id("khuyen").Should().Be(Tokenizer.Id("khuyen"));
        Tokenizer.Id("khuyen").Should().NotBe(Tokenizer.Id("khuyến"));
    }
}

public class BayesTests
{
    private static InMemoryBayesModel Trained(BayesScope scope = BayesScope.Global)
    {
        var m = new InMemoryBayesModel(scope);
        for (var i = 0; i < 30; i++)
        {
            m.Learn(Tokenizer.Tokens($"win free prize lottery claim now {i}").ToList(), spam: true);
            m.Learn(Tokenizer.Tokens($"meeting agenda project report budget {i}").ToList(), spam: false);
        }

        return m;
    }

    [Fact]
    public void Learned_words_move_the_probability_and_untrained_models_say_nothing()
    {
        var m = Trained();
        Bayes.Classify(Tokenizer.Tokens("claim your free prize now").ToList(), [m])!.Probability.Should().BeGreaterThan(0.95);
        Bayes.Classify(Tokenizer.Tokens("project meeting report").ToList(), [m])!.Probability.Should().BeLessThan(0.05);
        Bayes.Classify(Tokenizer.Tokens("completely unknown words").ToList(), [m]).Should().BeNull("no evidence");
        Bayes.Classify(Tokenizer.Tokens("free prize").ToList(), [new InMemoryBayesModel(BayesScope.User)]).Should().BeNull("an empty model is not used");
    }

    [Fact]
    public void A_users_model_outweighs_the_global_one()
    {
        var global = Trained();
        var user = new InMemoryBayesModel(BayesScope.User);
        for (var i = 0; i < 30; i++)
        {
            // This user wants lottery newsletters.
            user.Learn(Tokenizer.Tokens($"lottery prize results newsletter {i}").ToList(), spam: false);
            user.Learn(Tokenizer.Tokens($"cheap pills pharmacy {i}").ToList(), spam: true);
        }

        var tokens = Tokenizer.Tokens("lottery prize results").ToList();
        Bayes.Classify(tokens, [global])!.Probability.Should().BeGreaterThan(0.9);
        Bayes.Classify(tokens, [global, user])!.Probability.Should().BeLessThan(0.5);
    }

    [Fact]
    public void Chi_square_tail_matches_known_values()
    {
        Bayes.ChiSquareQ(0, 4).Should().BeApproximately(1.0, 1e-9);
        Bayes.ChiSquareQ(9.488, 4).Should().BeApproximately(0.05, 1e-3);   // table value: chi2(0.95, 4) = 9.488
        Bayes.ChiSquareQ(23.209, 10).Should().BeApproximately(0.01, 1e-3); // chi2(0.99, 10) = 23.209
    }
}

public class SpamRuleTests
{
    private static SpamInput Clean() => new()
    {
        RemoteIp = IPAddress.Parse("203.0.113.10"),
        Helo = "mail.partner.example",
        ReverseDns = "mail.partner.example",
        Spf = "pass",
        Dkim = [("pass", "partner.example")],
        Dmarc = "pass",
        FromAddress = "an@partner.example",
        FromName = "Nguyễn Văn An",
        Subject = "Báo giá tháng 11",
        Date = DateTimeOffset.UtcNow,
        HasMessageId = true,
        TextBody = "Chào chị, em gửi báo giá.",
        OurDomains = ["bluemail.test"],
        OurHostname = "mx.bluemail.test",
    };

    [Fact]
    public void A_normal_business_message_scores_low()
    {
        SpamRules.Score(Clean(), null).Score.Should().BeLessThan(0);
    }

    [Fact]
    public void One_authentication_failure_alone_is_not_spam()
    {
        SpamRules.Score(Clean() with { Spf = "fail", Dkim = [], Dmarc = "none" }, null).Score.Should().BeLessThan(5, "false positives hurt more than missed spam");
    }

    [Theory]
    [InlineData("[198.51.100.1]", "HELO_IP_MISMATCH")]
    [InlineData("localhost", "HELO_NO_DOT")]
    [InlineData("bluemail.test", "HELO_FORGED_OURS")]
    public void Helo_checks(string helo, string code) =>
        SpamRules.Score(Clean() with { Helo = helo }, null).Hits.Select(h => h.Code).Should().Contain(code);

    [Fact]
    public void Look_alike_and_spoofing_signals()
    {
        var hits = SpamRules.Score(Clean() with
        {
            FromAddress = "it@bluemaiI.test", FromName = "paypal.com Support", ReplyTo = "claims@gmail.com",
            Links = [("http://192.0.2.7/login", "https://www.paypal.com/account")],
            Subject = "Your password expires today",
        }, null).Hits.Select(h => h.Code).ToList();
        hits.Should().Contain(["FROM_LOOKALIKE_DOMAIN", "FROM_NAME_SPOOFS_DOMAIN", "REPLYTO_FREEMAIL", "LINK_TEXT_MISMATCH", "URL_IP_LITERAL", "PHISHING_PHRASE"]);
    }

    [Fact]
    public void Known_correspondent_gets_reputation_and_first_seen_domain_gets_no_auth_discount()
    {
        SpamRules.Score(Clean() with { SenderKnown = true }, null).Hits.Should().Contain(h => h.Code == "SENDER_KNOWN" && h.Score < 0);
        var fresh = SpamRules.Score(Clean() with { SenderDomainFirstSeen = true }, null).Hits.Select(h => h.Code).ToList();
        fresh.Should().NotContain(["DMARC_PASS", "DKIM_VALID_ALIGNED"]).And.Contain("SENDER_DOMAIN_NEW");
    }

    [Fact]
    public void Mixed_script_domain_is_flagged()
    {
        SpamRules.MixedScripts("bluemаil.test").Should().BeTrue("Cyrillic а inside a Latin label");
        SpamRules.MixedScripts("ví-dụ.vn").Should().BeFalse("Vietnamese letters are Latin");
        SpamRules.MixedScripts("例子.广告").Should().BeFalse();
    }

    [Fact]
    public void Html_facts_and_block_list_names()
    {
        var (links, images) = SpamInputs.HtmlFacts("<p><a href=\"https://x.test/a\">Xem &amp; tải</a><img src=a><IMG src=b><a href='mailto:a@b'>m</a></p>");
        links.Should().Equal(("https://x.test/a", "Xem & tải"), ("mailto:a@b", "m"));
        images.Should().Be(2);
        DnsblChecker.ReverseName(IPAddress.Parse("192.0.2.99")).Should().Be("99.2.0.192");
        DnsblChecker.ReverseName(IPAddress.Parse("2001:db8::1")).Should().StartWith("1.0.0.0").And.EndWith("8.b.d.0.1.0.0.2");
        Greylisting.Network(IPAddress.Parse("203.0.113.77")).Should().Be("203.0.113.0/24");
    }
}

public class LookalikeTests
{
    [Theory]
    [InlineData("bluemaiI.test", true)]
    [InlineData("bluemaii-support.test", true)]
    [InlineData("secure-bluemail.com", true)]
    [InlineData("bluemail.test", false)]
    [InlineData("mx.bluemail.test", false)]
    [InlineData("blue.test", false)]
    [InlineData("doitac.test", false)]
    public void Imitations_of_our_domain(string domain, bool imitates) =>
        (SpamRules.LooksLike(domain, ["bluemail.test"]) is not null).Should().Be(imitates);
}

public class SpamRuleTranslationTests
{
    [Fact]
    public void Every_rule_code_the_scorer_emits_is_explained_in_the_web_ui()
    {
        var root = BlueMail.UnitTests.Conventions.Repo.Root;
        var source = File.ReadAllText(Path.Combine(root, "src", "BlueMail.Antispam", "SpamRules.cs"));
        var codes = System.Text.RegularExpressions.Regex.Matches(source, "\"([A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+)\"").Select(m => m.Groups[1].Value).ToHashSet();
        codes.Should().Contain(["SPF_FAIL", "BAYES_999", "FROM_LOOKALIKE_DOMAIN"]);
        foreach (var lang in new[] { "en", "vi" })
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "web", "src", "i18n", "locales", lang + ".json")));
            var rules = doc.RootElement.GetProperty("spam").GetProperty("rule");
            codes.Where(c => !rules.TryGetProperty(c, out _)).Should().BeEmpty($"{lang}.json spam.rule needs every code");
        }
    }
}
