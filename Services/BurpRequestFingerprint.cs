using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScopePilot.Services;

public static class BurpRequestFingerprint
{
    public static string FromHar(JsonElement request)
    {
        var method = request.GetProperty("method").GetString() ?? "GET";
        var url = request.GetProperty("url").GetString() ?? "";
        var headers = request.TryGetProperty("headers", out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [];
        string Header(string name) => string.Join(name.Equals("Cookie", StringComparison.OrdinalIgnoreCase) ? "; " : ", ", headers
            .Where(x => x.TryGetProperty("name", out var n) && n.GetString()?.Equals(name, StringComparison.OrdinalIgnoreCase) == true)
            .Select(x => x.TryGetProperty("value", out var v) ? (v.GetString() ?? "").Trim() : ""));
        byte[] body = [];
        if (request.TryGetProperty("postData", out var post))
        {
            // Without exact captured text, a body reconstructed from params could be wrong.
            if (!post.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) return "";
            var content = text.GetString() ?? "";
            try { body = post.TryGetProperty("encoding", out var encoding) && encoding.GetString() == "base64" ? Convert.FromBase64String(content) : Encoding.UTF8.GetBytes(content); }
            catch (FormatException) { return ""; }
        }
        return Compute(method, url, body, Header("Cookie"), Header("Authorization"));
    }
    public static string Compute(string method, string url, byte[] body, string cookie, string authorization)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "";
        var canonicalUrl = uri.GetLeftPart(UriPartial.Path) + uri.Query;
        return Hash(Encoding.UTF8.GetBytes(method.ToUpperInvariant() + "\n" + canonicalUrl + "\n" + Hash(body) + "\n" + cookie.Trim() + "\n" + authorization.Trim()));
    }
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
}
