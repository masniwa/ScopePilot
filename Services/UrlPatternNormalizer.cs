using System.Text.RegularExpressions;

namespace ScopePilot.Services;

public static partial class UrlPatternNormalizer
{
    private static readonly HashSet<string> CacheQueryKeys = new(StringComparer.OrdinalIgnoreCase)
        { "_", "v", "ver", "version", "cb", "cache", "cachebuster", "rev", "hash" };

    public static string Normalize(string value, ISet<string>? ignoredQueryKeys = null)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return value;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeSegment);
        var path = "/" + string.Join('/', segments);
        var queryPairs = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split('=', 2))
            .Select(x => (Name: Uri.UnescapeDataString(x[0].Replace('+', ' ')), Value: x.Length > 1 ? Uri.UnescapeDataString(x[1].Replace('+', ' ')) : ""))
            .Where(x => !string.IsNullOrWhiteSpace(x.Name) && (ignoredQueryKeys is null || !ignoredQueryKeys.Contains(x.Name)))
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => RequestOperationIdentity.IsSelector(group.Key)
                ? group.Select(x => group.Key + "=" + Uri.EscapeDataString(x.Value))
                : [group.Key + "={value}"])
            .ToArray();
        return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant() + path +
               (queryPairs.Length > 0 ? $"?{string.Join('&', queryPairs)}" : string.Empty);
    }

    public static string NormalizeForSelection(string value) => Normalize(value, CacheQueryKeys);

    public static string NormalizeSegment(string value)
    {
        // URL paths are case-sensitive. Preserve escaped static segments as well:
        // decoding %2F here would turn part of one segment into a path separator.
        var segment = Regex.Replace(value, "%7B(guid|date|id|value)%7D", match => "{" + match.Groups[1].Value.ToLowerInvariant() + "}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (Guid.TryParse(segment, out _)) return "{guid}";
        if (PureId().IsMatch(segment)) return "{id}";

        var extension = Path.GetExtension(segment);
        var stem = extension.Length > 0 ? segment[..^extension.Length] : segment;
        var suffix = extension.Length > 0 ? extension : string.Empty;

        var dated = PrefixedDate().Match(stem);
        if (dated.Success)
            return dated.Groups["prefix"].Success
                ? dated.Groups["prefix"].Value.TrimEnd('-', '_') + "-{date}" + suffix
                : "{date}" + suffix;

        var prefixedId = PrefixedLongNumber().Match(stem);
        if (prefixedId.Success)
            return prefixedId.Groups["prefix"].Value.TrimEnd('-', '_') + "{id}" + suffix;

        return segment;
    }

    [GeneratedRegex("^(?:\\d+|[0-9a-f]{16,})$", RegexOptions.IgnoreCase)]
    private static partial Regex PureId();

    [GeneratedRegex("^(?:(?<prefix>[a-z][a-z0-9_-]*?)[-_])?(?:19|20)\\d{2}[-_]\\d{1,2}[-_]\\d{1,2}$", RegexOptions.IgnoreCase)]
    private static partial Regex PrefixedDate();

    [GeneratedRegex("^(?<prefix>[a-z][a-z_-]*?)[-_]?(?<id>\\d{4,})$", RegexOptions.IgnoreCase)]
    private static partial Regex PrefixedLongNumber();
}
