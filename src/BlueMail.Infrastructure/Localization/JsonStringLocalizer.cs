using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Localization;

namespace BlueMail.Infrastructure.Localization;

/// <summary>
/// Server-side strings (API errors, BlueID pages, system mail) live in embedded JSON files
/// Localization/Resources/&lt;lang&gt;.json - one flat object of key -> text. English is the source
/// and fallback; adding a language means adding one file. Placeholders are named ({name}) and filled
/// by <see cref="Format"/>, never by string concatenation.
/// </summary>
public sealed class JsonStringLocalizerFactory : IStringLocalizerFactory
{
    public const string Fallback = "en";
    private static readonly Lazy<FrozenDictionary<string, FrozenDictionary<string, string>>> Tables = new(Load);

    public static IReadOnlyCollection<string> Languages => Tables.Value.Keys;

    public static IReadOnlyDictionary<string, string> Table(string language) => Tables.Value[language];

    public IStringLocalizer Create(Type resourceSource) => new JsonStringLocalizer();

    public IStringLocalizer Create(string baseName, string location) => new JsonStringLocalizer();

    private static FrozenDictionary<string, FrozenDictionary<string, string>> Load()
    {
        var asm = typeof(JsonStringLocalizerFactory).Assembly;
        var result = new Dictionary<string, FrozenDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.Contains(".Localization.Resources.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)))
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
            var lang = name.Split('.')[^2];
            result[lang] = dict.ToFrozenDictionary(StringComparer.Ordinal);
        }

        return result.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Best supported language for a culture: exact ("pt-BR"), then neutral ("pt"), then English.</summary>
    public static string Resolve(CultureInfo culture)
    {
        for (var c = culture; !string.IsNullOrEmpty(c.Name); c = c.Parent)
        {
            if (Tables.Value.ContainsKey(c.Name))
            {
                return c.Name;
            }
        }

        return Fallback;
    }

    public static string Get(string key, CultureInfo culture)
    {
        var lang = Resolve(culture);
        if (Tables.Value[lang].TryGetValue(key, out var v) || Tables.Value[Fallback].TryGetValue(key, out v))
        {
            return v;
        }

        return key;
    }

    /// <summary>Replaces {name} placeholders. Values are formatted with the target culture.</summary>
    public static string Format(string template, CultureInfo culture, IReadOnlyDictionary<string, object?>? args)
    {
        if (args is null || args.Count == 0)
        {
            return template;
        }

        var sb = new System.Text.StringBuilder(template.Length + 32);
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c == '{')
            {
                var end = template.IndexOf('}', i + 1);
                if (end > i && args.TryGetValue(template[(i + 1)..end], out var value))
                {
                    sb.Append(value is IFormattable f ? f.ToString(null, culture) : value?.ToString());
                    i = end;
                    continue;
                }
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}

public sealed class JsonStringLocalizer : IStringLocalizer
{
    private static readonly ConcurrentDictionary<string, bool> Missing = new();

    public LocalizedString this[string name]
    {
        get
        {
            var value = JsonStringLocalizerFactory.Get(name, CultureInfo.CurrentUICulture);
            var notFound = ReferenceEquals(value, name) || value == name;
            if (notFound)
            {
                Missing.TryAdd(name, true);
            }

            return new LocalizedString(name, value, notFound);
        }
    }

    public LocalizedString this[string name, params object[] arguments] =>
        new(name, string.Format(CultureInfo.CurrentCulture, this[name].Value, arguments));

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
        JsonStringLocalizerFactory.Table(JsonStringLocalizerFactory.Resolve(CultureInfo.CurrentUICulture))
            .Select(kv => new LocalizedString(kv.Key, kv.Value));
}

/// <summary>Marker for the assembly that carries the resources (used by tests).</summary>
public static class LocalizationAssembly
{
    public static Assembly Value => typeof(JsonStringLocalizerFactory).Assembly;
}
