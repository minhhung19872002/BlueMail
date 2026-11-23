using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BlueMail.Api.Auth;
using BlueMail.Infrastructure.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BlueMail.UnitTests.Conventions;

internal static class Repo
{
    public static string Root { get; } = Find();

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BlueMail.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    public static IEnumerable<string> SourceFiles(string pattern) =>
        Directory.EnumerateFiles(Path.Combine(Root, "src"), pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
}

public partial class LocalizationTests
{
    [Fact]
    public void Every_language_has_every_key_of_english()
    {
        var en = JsonStringLocalizerFactory.Table("en").Keys.ToHashSet(StringComparer.Ordinal);
        JsonStringLocalizerFactory.Languages.Should().Contain(["en", "vi"]);
        foreach (var lang in JsonStringLocalizerFactory.Languages)
        {
            var keys = JsonStringLocalizerFactory.Table(lang).Keys.ToHashSet(StringComparer.Ordinal);
            en.Except(keys).Should().BeEmpty($"language '{lang}' must translate every key");
            keys.Except(en).Should().BeEmpty($"language '{lang}' must not have keys unknown to English");
        }
    }

    [Fact]
    public void Placeholders_are_the_same_in_every_language()
    {
        var en = JsonStringLocalizerFactory.Table("en");
        foreach (var lang in JsonStringLocalizerFactory.Languages)
        {
            foreach (var (key, text) in JsonStringLocalizerFactory.Table(lang))
            {
                Placeholders(text).Should().BeEquivalentTo(Placeholders(en[key]), $"{lang}:{key}");
            }
        }
    }

    [Fact]
    public void Every_error_code_and_page_key_used_in_code_exists_in_english()
    {
        var en = JsonStringLocalizerFactory.Table("en");
        var missing = new List<string>();
        foreach (var file in Repo.SourceFiles("*.cs").Concat(Repo.SourceFiles("*.cshtml")))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in ErrorCode().Matches(text))
            {
                if (!en.ContainsKey("error." + m.Groups["code"].Value))
                {
                    missing.Add($"error.{m.Groups["code"].Value} ({Path.GetFileName(file)})");
                }
            }

            foreach (Match m in PageKey().Matches(text))
            {
                if (!en.ContainsKey(m.Groups["key"].Value))
                {
                    missing.Add($"{m.Groups["key"].Value} ({Path.GetFileName(file)})");
                }
            }
        }

        foreach (var f in typeof(BlueMail.Application.Common.ValidationCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var code = (string)f.GetValue(null)!;
            if (!en.ContainsKey("error." + code))
            {
                missing.Add($"error.{code} (ValidationCodes)");
            }
        }

        missing.Distinct().Should().BeEmpty();
    }

    private static IEnumerable<string> Placeholders(string s) => Regex.Matches(s, "\\{([a-zA-Z0-9_]+)\\}").Select(m => m.Groups[1].Value).Order();

    // AppException.X("code"), new AppException("code", ...), Problems.WriteAsync(..., "code"), ValidationCodes, ErrorKey = "..."
    [GeneratedRegex("(?:AppException\\.(?:NotFound|Forbidden|Conflict|BadRequest)|new AppException|: AppException)\\(\\s*\"(?<code>[a-z0-9_]+)\"|HttpStatusCode\\.[A-Za-z]+, \"(?<code>[a-z0-9_]+)\"|WithErrorCode\\(\"(?<code>[a-z0-9_]+)\"\\)")]
    private static partial Regex ErrorCode();

    [GeneratedRegex("\"(?<key>id\\.[a-z0-9_.]+)\"")]
    private static partial Regex PageKey();
}

public class LicenceAllowListTests
{
    [Fact]
    public void Every_nuget_package_is_listed_in_docs_09()
    {
        var doc = File.ReadAllText(Path.Combine(Repo.Root, "docs", "09-giay-phep-thanh-phan.md"));
        var packages = XDocument.Load(Path.Combine(Repo.Root, "Directory.Packages.props"))
            .Descendants("PackageVersion").Select(e => e.Attribute("Include")!.Value).ToList();
        packages.Should().NotBeEmpty();
        packages.Where(p => !doc.Contains($"| {p} |", StringComparison.Ordinal)).Should().BeEmpty("unlisted packages are not approved");
    }

    [Fact]
    public void Every_npm_package_is_listed_in_docs_09()
    {
        var path = Path.Combine(Repo.Root, "web", "package.json");
        if (!File.Exists(path))
        {
            return; // web app not created in this checkout
        }

        var doc = File.ReadAllText(Path.Combine(Repo.Root, "docs", "09-giay-phep-thanh-phan.md"));
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var names = new[] { "dependencies", "devDependencies" }
            .Where(s => json.RootElement.TryGetProperty(s, out _))
            .SelectMany(s => json.RootElement.GetProperty(s).EnumerateObject().Select(p => p.Name));
        names.Where(n => !doc.Contains($"| {n} |", StringComparison.Ordinal)).Should().BeEmpty("unlisted packages are not approved");
    }
}

public class MigrationTests
{
    [Fact]
    public void Every_migration_class_has_the_Migration_attribute()
    {
        var types = typeof(BlueMail.Infrastructure.Persistence.BlueMailDbContext).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(Migration)) && !t.IsAbstract).ToList();
        types.Should().NotBeEmpty();
        types.Where(t => t.GetCustomAttribute<MigrationAttribute>() is null).Should().BeEmpty();
    }
}

public partial class MailStoreGateTests
{
    [Fact]
    public void Mail_store_tables_are_written_only_inside_BlueMail_MailStore()
    {
        var offenders = Repo.SourceFiles("*.cs")
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}BlueMail.MailStore{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => MailWrite().IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .ToList();
        offenders.Should().BeEmpty("every mailbox write goes through IMailStore (PROMPT principle 1)");
    }

    [GeneratedRegex("\\.(Messages|MessageLabels|Labels|Mailboxes|Threads|Attachments|LabelChanges|Blobs)\\s*\\.\\s*(Add|AddRange|Update|UpdateRange|Remove|RemoveRange|Attach|ExecuteUpdate|ExecuteUpdateAsync|ExecuteDelete|ExecuteDeleteAsync)\\b|(INSERT INTO|UPDATE|DELETE FROM)\\s+mail\\.")]
    private static partial Regex MailWrite();
}

public class EndpointGuardTests
{
    [Fact]
    public void Every_api_action_declares_permission_anonymous_or_self_guard()
    {
        var actions = typeof(ApiAuthentication).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any())
                .Select(m => (Controller: t, Action: m)))
            .ToList();
        actions.Should().NotBeEmpty();

        var unguarded = actions.Where(a =>
        {
            bool Has<T>() where T : Attribute => a.Action.GetCustomAttribute<T>() is not null || a.Controller.GetCustomAttribute<T>() is not null;
            return !Has<RequirePermissionAttribute>() && !Has<AllowAnonymousAttribute>() && !Has<SelfGuardedAttribute>();
        }).Select(a => $"{a.Controller.Name}.{a.Action.Name}");
        unguarded.Should().BeEmpty();

        actions.Select(a => a.Action.GetCustomAttribute<SelfGuardedAttribute>() ?? a.Controller.GetCustomAttribute<SelfGuardedAttribute>())
            .Where(s => s is not null).Should().OnlyContain(s => s!.Reason.Length >= 20, "a self-guard must explain itself");
    }
}
