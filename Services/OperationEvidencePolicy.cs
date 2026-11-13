using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ScopePilot.Services;

internal static class OperationEvidencePolicy
{
    internal static string[] AllowedOrigins(JsonElement contract) => contract.TryGetProperty("allowedOrigins", out var origins) && origins.ValueKind == JsonValueKind.Array
        ? origins.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray() : [];

    internal static bool IsOutsideScope(JsonElement item, IReadOnlyList<string> allowedOrigins)
    {
        if (allowedOrigins.Count == 0 || Text(item, "outcome") != "blocked") return false;
        if (item.TryGetProperty("boundary", out var boundary) && boundary.ValueKind == JsonValueKind.Object &&
            Text(boundary, "classification") == "outside_allowed_origin" && Text(boundary, "uiEvidence").Length > 0 &&
            Outside(Text(boundary, "observedUrl"), allowedOrigins)) return true;

        // Compatibility for older reports which did not have a structured boundary.
        // Require an explicitly observed link destination and an explicit mismatch,
        // then compare the destination with the actual engagement, not quoted scope.
        var reason = Text(item, "reason").Split("\n以前の観測（", StringSplitOptions.None)[0];
        var mismatch = Regex.IsMatch(reason, @"許可(?:Origin|オリジン).{0,300}(?:一致しません|一致しない|does not match)", RegexOptions.IgnoreCase);
        var outside = Regex.IsMatch(reason, @"許可(?:Origin|オリジン)\s*外|outside[ _-](?:allowed[ _-])?origin", RegexOptions.IgnoreCase) &&
            !Regex.IsMatch(reason, @"外(?:ではない|ではありません|ではなく)|not outside", RegexOptions.IgnoreCase);
        if (!mismatch && !outside) return false;
        var destination = Regex.Match(reason, @"(?:実リンク先|リンク先|転送先(?:Origin)?|link destination|redirect destination|href)\s*[:：=]?\s*(https?://[^\s、。<>""']+)", RegexOptions.IgnoreCase);
        return destination.Success && Outside(destination.Groups[1].Value, allowedOrigins);
    }

    private static bool Outside(string value, IReadOnlyList<string> origins)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var destination) || destination.Scheme is not ("http" or "https") || destination.UserInfo.Length > 0) return false;
        var parsed = origins.Select(x => Uri.TryCreate(x, UriKind.Absolute, out var origin) && origin.Scheme is "http" or "https" ? origin : null).ToArray();
        return parsed.All(x => x is not null) && parsed.All(x => !destination.GetLeftPart(UriPartial.Authority).Equals(x!.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase));
    }

    internal static string CommunicationStatus(JsonObject row)
    {
        if (row["requests"] is JsonArray requests && requests.Count > 0) return "references_reported";
        if (row["outcome"]?.GetValue<string>() != "observed") return "not_observed";
        var reason = row["reason"]?.GetValue<string>() ?? "";
        return Regex.IsMatch(reason, @"(?:HTTP|通信|リクエスト).{0,15}(?:発生しない|発生なし|発生せず|なし|不要)|no (?:additional )?(?:HTTP|network|requests)|without (?:HTTP|network)", RegexOptions.IgnoreCase)
            ? "no_http_reported" : "unlinked";
    }

    private static string Text(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
