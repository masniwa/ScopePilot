using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ScopePilot.Domain;

namespace ScopePilot.Services;

public static class RequestOperationIdentity
{
    private const string Fragment = "#scopepilot-body=";
    private static readonly HashSet<string> Keys = new(StringComparer.OrdinalIgnoreCase)
        { "action", "_action", "operation", "operationName", "op", "command", "cmd", "method", "_method", "function", "fn", "do", "intent", "mode", "event", "task", "dispatch" };
    public static bool IsSelector(string name) => Keys.Contains(name);
    public static string ValueHash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string DisplayValue(string value) => Regex.IsMatch(value, "^[A-Za-z][A-Za-z0-9_.-]{0,30}$") ? value : "（値は非表示）";
    public static string BodyKey(ObservedRequest request)
    {
        var parts = (request.RequestOperations ?? []).Where(x => !string.IsNullOrEmpty(x.Name) && IsSelector(x.Name.Split('.').Last()) &&
            !string.IsNullOrEmpty(x.ValueHash) && Regex.IsMatch(x.ValueHash, "^[0-9a-f]{64}$"))
            .Select(x => x.Name + "=" + x.ValueHash).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return parts.Length == 0 ? "" : ValueHash(string.Join('\n', parts));
    }
    public static string Pattern(ObservedRequest request) => request.Method.ToUpperInvariant() + " " +
        UrlPatternNormalizer.NormalizeForSelection(request.Url) + (BodyKey(request) is { Length: > 0 } key ? Fragment + key : "");
    public static string NormalizePattern(string pattern)
    {
        var separator = pattern.IndexOf(' ');
        if (separator <= 0 || separator == pattern.Length - 1) return pattern.Trim();
        var value = pattern[(separator + 1)..].Trim();
        var fragment = value.IndexOf(Fragment, StringComparison.Ordinal);
        var suffix = fragment >= 0 && Regex.IsMatch(value[(fragment + Fragment.Length)..], "^[0-9a-f]{64}$") ? value[fragment..] : "";
        var url = fragment >= 0 ? value[..fragment] : value;
        return Uri.TryCreate(url, UriKind.Absolute, out _) ? pattern[..separator].ToUpperInvariant() + " " + UrlPatternNormalizer.NormalizeForSelection(url) + suffix : pattern.Trim();
    }
    public static string Display(ObservedRequest request)
    {
        var labels = (request.RequestOperations ?? []).Select(x => $"{x.Name}={x.DisplayValue}（本文）").ToList();
        if (Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                var name = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
                if (IsSelector(name) && parts.Length == 2) labels.Add(name + "=" + DisplayValue(Uri.UnescapeDataString(parts[1].Replace('+', ' '))));
            }
        return string.Join("、", labels.Distinct(StringComparer.Ordinal));
    }
    public static IEnumerable<string> QueryValues(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) yield break;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && IsSelector(Uri.UnescapeDataString(parts[0].Replace('+', ' '))))
                yield return Uri.UnescapeDataString(parts[1].Replace('+', ' '));
        }
    }
}
