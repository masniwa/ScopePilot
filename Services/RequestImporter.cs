using System.Text.Json;
using ScopePilot.Domain;

namespace ScopePilot.Services;

public sealed class RequestImporter
{
    public async Task<IReadOnlyList<ObservedRequest>> ImportAsync(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".har" => await ImportHarAsync(path),
            ".jsonl" => await ImportJsonLinesAsync(path),
            ".txt" => await ImportUrlListAsync(path),
            _ => throw new InvalidOperationException("対応形式は HAR、JSONL、URL一覧TXTです。")
        };
    }

    private static async Task<IReadOnlyList<ObservedRequest>> ImportHarAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream);
        if (!document.RootElement.TryGetProperty("log", out var log) ||
            !log.TryGetProperty("entries", out var entries))
            throw new InvalidDataException("HARの log.entries が見つかりません。収集ツールからHARを再出力してください。");

        var result = new List<ObservedRequest>();
        // Baselines live only inside this one capture. The exact URL and hashed
        // Cookie/Authorization context prevent comparison with another user/run.
        var baselines = new Dictionary<string, (JsonElement Body, DateTimeOffset CompletedAt)>();
        foreach (var entry in entries.EnumerateArray())
        {
            var request = entry.GetProperty("request");
            var response = entry.TryGetProperty("response", out var responseElement) ? responseElement : default;
            var contentType = string.Empty;
            if (response.ValueKind == JsonValueKind.Object && response.TryGetProperty("content", out var content) &&
                content.TryGetProperty("mimeType", out var mimeType)) contentType = mimeType.GetString() ?? string.Empty;

            var observed = new ObservedRequest
            {
                Method = request.GetProperty("method").GetString() ?? "GET",
                Url = request.GetProperty("url").GetString() ?? string.Empty,
                StatusCode = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("status", out var status)
                    ? status.GetInt32() : null,
                ContentType = contentType,
                Source = "HAR",
                Role = entry.TryGetProperty("_scopepilotRole", out var role) && role.ValueKind == JsonValueKind.String ? role.GetString() ?? "" : "",
                ObservedAt = entry.TryGetProperty("startedDateTime", out var time) && time.TryGetDateTimeOffset(out var stamp) ? stamp : DateTimeOffset.Now
            };
            var hasTime = entry.TryGetProperty("startedDateTime", out var eventTime) && eventTime.TryGetDateTimeOffset(out _);
            var baselineKey = hasTime ? BaselineKey(request, observed) : "";
            JsonElement? baseline = baselineKey.Length > 0 && baselines.TryGetValue(baselineKey, out var prior) && prior.CompletedAt <= observed.ObservedAt
                ? prior.Body : null;
            RequestContentSummary.ReadHar(request, observed, baseline);
            observed.RequestFingerprint = BurpRequestFingerprint.FromHar(request);
            result.Add(observed);
            if (baselineKey.Length > 0 && observed.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) && entry.TryGetProperty("time", out var elapsed) && elapsed.TryGetDouble(out var duration) && duration is >= 0 and <= 86_400_000 && observed.StatusCode is >= 200 and < 300 &&
                contentType.Contains("json", StringComparison.OrdinalIgnoreCase) && response.TryGetProperty("content", out var responseContent) &&
                responseContent.TryGetProperty("text", out var body) && body.ValueKind == JsonValueKind.String)
            {
                var text = body.GetString() ?? "";
                if (responseContent.TryGetProperty("_scopepilotBodyError", out _)) continue;
                if (responseContent.TryGetProperty("encoding", out var encoding) && encoding.GetString() == "base64")
                {
                    if (text.Length > 341_336) continue; // At most 256 KB of decoded JSON.
                    try
                    {
                        var bytes = Convert.FromBase64String(text);
                        if (bytes.Length > 256_000) continue;
                        text = new System.Text.UTF8Encoding(false, true).GetString(bytes);
                    }
                    catch (Exception ex) when (ex is FormatException or System.Text.DecoderFallbackException) { continue; }
                }
                if (text.Length is > 0 and <= 256_000)
                    try
                    {
                        using var snapshot = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
                        if (snapshot.RootElement.ValueKind == JsonValueKind.Object && (baselines.Count < 1024 || baselines.ContainsKey(baselineKey)))
                        {
                            baselines[baselineKey] = (snapshot.RootElement.Clone(), observed.ObservedAt.AddMilliseconds(duration));
                        }
                    }
                    catch (JsonException) { /* No trustworthy JSON snapshot; keep the request's full input summary. */ }
            }
        }
        return result;
    }

    private static string BaselineKey(JsonElement request, ObservedRequest observed)
    {
        if (!request.TryGetProperty("headers", out var headers) || headers.ValueKind != JsonValueKind.Array) return "";
        var credentials = headers.EnumerateArray().Where(x => x.TryGetProperty("name", out var name) &&
            name.GetString() is { } value && (value.Equals("Cookie", StringComparison.OrdinalIgnoreCase) || value.Equals("Authorization", StringComparison.OrdinalIgnoreCase)))
            .Select(x => x.GetProperty("name").GetString()!.ToLowerInvariant() + ":" + (x.TryGetProperty("value", out var value) ? value.GetString() : ""))
            .Order(StringComparer.Ordinal).ToArray();
        if (credentials.Length == 0) return "";
        return observed.Url + "\n" + ObservedRequestIdentity.NormalizeRole(observed.Role) + "\n" +
            DocumentHintService.Sha256(string.Join('\n', credentials));
    }

    private static async Task<IReadOnlyList<ObservedRequest>> ImportJsonLinesAsync(string path)
    {
        var result = new List<ObservedRequest>();
        var lineNumber = 0;
        foreach (var line in await File.ReadAllLinesAsync(path))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var item = JsonSerializer.Deserialize<ObservedRequest>(line,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (item is not null)
                {
                    Normalize(item);
                    if (Uri.TryCreate(item.Url, UriKind.Absolute, out _)) result.Add(item);
                }
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"JSONLの{lineNumber}行目を読み取れません。", ex);
            }
        }
        return result;
    }

    public static void Normalize(ObservedRequest request)
    {
        request.Id = string.IsNullOrWhiteSpace(request.Id) ? Guid.NewGuid().ToString("N") : request.Id;
        request.Method = string.IsNullOrWhiteSpace(request.Method) ? "GET" : request.Method;
        request.Url ??= string.Empty;
        request.ContentType ??= string.Empty;
        request.Source = string.IsNullOrWhiteSpace(request.Source) ? "Import" : request.Source;
        request.Role ??= string.Empty;
        request.PageTitle ??= string.Empty;
        request.FormFieldNames ??= [];
        request.RequestContentType ??= string.Empty;
        request.RequestFieldNames ??= [];
        request.RequestHeaderNames ??= [];
        request.RequestSemanticSignals ??= [];
        request.RequestPriorityEvidence ??= [];
        request.RequestOperationTerms ??= [];
        request.RequestContentLimitations ??= [];
        request.RequestOperations ??= [];
        request.RequestFingerprint ??= string.Empty;
        request.BurpCaptureId ??= string.Empty;
        if (request.BurpProxyRequestId < 0) request.BurpProxyRequestId = null;
    }

    private static async Task<IReadOnlyList<ObservedRequest>> ImportUrlListAsync(string path)
    {
        return (await File.ReadAllLinesAsync(path))
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x) && !x.StartsWith('#'))
            .Where(x => Uri.TryCreate(x, UriKind.Absolute, out var uri) && (uri.Scheme is "http" or "https"))
            .Distinct(StringComparer.Ordinal)
            .Select(x => new ObservedRequest { Url = x, Method = "GET", Source = "URL list" })
            .ToArray();
    }
}
