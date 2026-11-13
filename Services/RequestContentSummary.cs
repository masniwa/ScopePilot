using System.Text.Json;
using System.Text.RegularExpressions;
using ScopePilot.Domain;

namespace ScopePilot.Services;

// Inspect captured request content locally. Keep field names and fixed semantic
// categories, never passwords, tokens, cookies, personal values or file bytes.
public static class RequestContentSummary
{
    private const int MaxFields = 128;
    private const int MaxBodyCharacters = 256_000;
    private static readonly Regex Personal = new("email|e_mail|phone|address|birth|passport|national.?id|personal|customer.?name|氏名|住所|電話|生年月日", RegexOptions.IgnoreCase);
    private static readonly Regex Financial = new("amount|balance|credit.?card|card.?number|iban|bank.?account|payment|billing|給与|口座|金額", RegexOptions.IgnoreCase);
    private static readonly Regex Privilege = new("role|permission|is.?admin|privilege|tenant|権限", RegexOptions.IgnoreCase);
    private static readonly Regex Secret = new("password|passwd|secret|credential|otp|mfa|パスワード", RegexOptions.IgnoreCase);
    private static readonly Regex External = new("url|uri|webhook|callback|redirect|destination|転送先", RegexOptions.IgnoreCase);
    private static readonly Regex AiInput = new("prompt|embedding|conversation|chat|モデル", RegexOptions.IgnoreCase);
    private static readonly HashSet<string> EnumFields = new(StringComparer.OrdinalIgnoreCase)
        { "action", "operation", "op", "command", "method", "role", "permission", "type" };
    private static readonly HashSet<string> SafeTerms = new(StringComparer.OrdinalIgnoreCase)
        { "create", "update", "delete", "remove", "export", "import", "upload", "download", "purchase", "checkout", "payment", "approve", "transfer", "admin", "administrator", "superuser", "owner", "read", "write", "get", "put", "post", "patch", "head", "options" };

    public static void ReadHar(JsonElement request, ObservedRequest target, JsonElement? baseline = null)
    {
        var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var personalFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var semantics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var operations = new List<RequestOperation>();
        var limitations = new List<string>();
        string[]? changedFieldsForPriority = null;
        string[]? changedSignalsForPriority = null;
        if (request.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Array)
            target.RequestHeaderNames = headers.EnumerateArray().Select(x => Text(x, "name"))
                .Where(x => Regex.IsMatch(x, "^[A-Za-z][A-Za-z0-9-]{0,79}$")).Distinct(StringComparer.OrdinalIgnoreCase).Take(64).ToArray();
        if (request.TryGetProperty("postData", out var post) && post.ValueKind == JsonValueKind.Object)
        {
            target.RequestBodyCaptured = true;
            target.RequestContentType = Text(post, "mimeType").Split(';')[0];
            if (post.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Array)
                foreach (var parameter in parameters.EnumerateArray())
                {
                    AddField(Text(parameter, "name"));
                    personalFields.Add(Text(parameter, "name"));
                    InspectValue(Text(parameter, "name"), Text(parameter, "value"));
                    if (!string.IsNullOrWhiteSpace(Text(parameter, "fileName"))) semantics.Add("file-upload");
                }
            var text = Text(post, "text");
            if (text.Length > MaxBodyCharacters)
            {
                text = text[..MaxBodyCharacters];
                limitations.Add("要求本文は256,000文字まで解析。残りの内容は未評価。");
            }
            if (text.Length > 0)
            {
                try
                {
                    if (target.RequestContentType.Contains("json", StringComparison.OrdinalIgnoreCase))
                    {
                        using var json = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
                        Walk(json.RootElement, "");
                        if (baseline is { ValueKind: JsonValueKind.Object } prior && json.RootElement.ValueKind == JsonValueKind.Object &&
                            json.RootElement.EnumerateObject().Any() && json.RootElement.EnumerateObject().All(x => prior.TryGetProperty(x.Name, out _)))
                        {
                            var changedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            var changedSignals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            if (Compare(json.RootElement, prior, "", changedFields, changedSignals))
                            {
                                changedFieldsForPriority = changedFields.ToArray();
                                changedSignalsForPriority = changedSignals.ToArray();
                            }
                            else limitations.Add("要求差分が項目数上限を超えたため、差分で重要度を絞らず本文全体の観測材料を使います。");
                        }
                    }
                    else if (target.RequestContentType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
                        foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries).Take(MaxFields))
                        {
                            var parts = pair.Split('=', 2);
                            var name = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
                            AddField(name);
                            personalFields.Add(name);
                            if (parts.Length == 2) InspectValue(name, Uri.UnescapeDataString(parts[1].Replace('+', ' ')));
                        }
                    else if (target.RequestContentType.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (Match match in Regex.Matches(text, @"Content-Disposition:[^\r\n]*?\bname=""([^""\r\n]+)""", RegexOptions.IgnoreCase)) AddField(match.Groups[1].Value);
                        if (Regex.IsMatch(text, @"Content-Disposition:[^\r\n]*\bfilename=", RegexOptions.IgnoreCase)) semantics.Add("file-upload");
                    }
                    else limitations.Add("要求本文の形式に専用解析がなく、機能・データの詳細は未評価。");
                }
                catch (JsonException) { limitations.Add("要求本文のJSONを解析できず、詳細は未評価。"); }
                // Detect personal data even when a field name is opaque. Do not retain values.
                if (Regex.IsMatch(text, @"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")) semantics.Add("personal-data");
            }
        }
        foreach (var field in fields)
        {
            if (personalFields.Contains(field) && IsPersonalField(field)) semantics.Add("personal-data");
            if (Financial.IsMatch(field)) semantics.Add("financial-data");
            if (Privilege.IsMatch(field)) semantics.Add("authorization-data");
            if (Secret.IsMatch(field)) semantics.Add("authentication-data");
            if (External.IsMatch(field)) semantics.Add("external-target");
            if (AiInput.IsMatch(field)) semantics.Add("ai-input");
        }
        target.RequestFieldNames = fields.ToArray();
        target.RequestSemanticSignals = semantics.ToArray();
        target.RequestOperationTerms = terms.ToArray();
        target.RequestOperations = operations.Distinct().Take(MaxFields).ToArray();
        target.RequestContentLimitations = limitations.Distinct().ToArray();
        if (target.RequestBodyCaptured)
        {
            var comparisonFields = changedFieldsForPriority ?? target.RequestFieldNames;
            var comparisonTerms = changedFieldsForPriority is null ? target.RequestOperationTerms : target.RequestOperations
                .Where(x => comparisonFields.Any(field => field == x.Name || x.Name.StartsWith(field + ".", StringComparison.OrdinalIgnoreCase)))
                .Select(x => x.DisplayValue).Where(SafeTerms.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            // Fingerprint includes the credential context, while the timestamp
            // separates repeated sends with identical content.
            var fingerprint = string.IsNullOrEmpty(target.RequestFingerprint) ? BurpRequestFingerprint.FromHar(request) : target.RequestFingerprint;
            var eventKey = DocumentHintService.Sha256(fingerprint + "\n" + target.ObservedAt.ToUniversalTime().ToString("O"));
            target.RequestPriorityEvidence = [new(eventKey, changedFieldsForPriority is not null, comparisonFields, changedSignalsForPriority ?? target.RequestSemanticSignals, comparisonTerms)];
        }

        void AddField(string value)
        {
            if (fields.Count >= MaxFields) { limitations.Add("要求項目名は128種類まで解析。"); return; }
            var clean = new string(value.Where(c => !char.IsControl(c)).Take(100).ToArray());
            if (!string.IsNullOrWhiteSpace(clean)) fields.Add(clean);
        }
        void InspectValue(string field, string value)
        {
            var leaf = field.Split('.').LastOrDefault() ?? field;
            if ((EnumFields.Contains(leaf) || RequestOperationIdentity.IsSelector(leaf)) && SafeTerms.Contains(value)) terms.Add(value.ToLowerInvariant());
            if (RequestOperationIdentity.IsSelector(leaf)) operations.Add(new(field, RequestOperationIdentity.ValueHash(value), RequestOperationIdentity.DisplayValue(value)));
        }
        void Walk(JsonElement node, string prefix)
        {
            if (fields.Count >= MaxFields) { limitations.Add("要求項目名は128種類まで解析。"); return; }
            if (node.ValueKind == JsonValueKind.Object)
                foreach (var property in node.EnumerateObject())
                {
                    var field = prefix.Length == 0 ? property.Name : prefix + "." + property.Name;
                    AddField(field);
                    if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number) personalFields.Add(field);
                    if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) InspectValue(field, property.Value.ToString());
                    Walk(property.Value, field);
                }
            else if (node.ValueKind == JsonValueKind.Array)
                foreach (var child in node.EnumerateArray().Take(20)) Walk(child, prefix + "[]");
        }
    }

    private static bool Compare(JsonElement current, JsonElement previous, string prefix, HashSet<string> fields, HashSet<string> signals)
    {
        if (JsonElement.DeepEquals(current, previous)) return true;
        if (fields.Count >= MaxFields) return false;
        if (current.ValueKind == JsonValueKind.Array && current.GetArrayLength() > 20) return false;
        if (current.ValueKind == JsonValueKind.Object && previous.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in current.EnumerateObject())
            {
                var field = prefix.Length == 0 ? property.Name : prefix + "." + property.Name;
                if (previous.TryGetProperty(property.Name, out var old))
                { if (!Compare(property.Value, old, field, fields, signals)) return false; }
                else Changed(property.Value, field, fields, signals);
            }
        }
        else Changed(current, prefix, fields, signals);
        return fields.Count < MaxFields;
    }

    private static void Changed(JsonElement value, string field, HashSet<string> fields, HashSet<string> signals)
    {
        if (fields.Count >= MaxFields) return;
        var clean = new string(field.Where(c => !char.IsControl(c)).Take(100).ToArray());
        if (!string.IsNullOrWhiteSpace(clean)) fields.Add(clean);
        // Classify the changed input point, including an explicit clearing of a
        // permission. Retain no previous/current values in the summary.
        if (value.ValueKind is JsonValueKind.String or JsonValueKind.Number && IsPersonalField(field)) signals.Add("personal-data");
        if (Financial.IsMatch(field)) signals.Add("financial-data");
        if (Privilege.IsMatch(field)) signals.Add("authorization-data");
        if (Secret.IsMatch(field)) signals.Add("authentication-data");
        if (External.IsMatch(field)) signals.Add("external-target");
        if (AiInput.IsMatch(field)) signals.Add("ai-input");
        if (value.ValueKind == JsonValueKind.String && Regex.IsMatch(value.GetString() ?? "", @"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")) signals.Add("personal-data");
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject()) Changed(property.Value, field + "." + property.Name, fields, signals);
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray().Take(20)) Changed(child, field + "[]", fields, signals);
    }

    public static void Merge(ObservedRequest target, ObservedRequest incoming)
    {
        // Legacy records without an identifiable event remain unknown. A legacy
        // record with a matching fingerprint/time can be repaired only by that
        // exact event's freshly parsed capture, not by a different later write.
        var previousEvidence = EvidenceFor(target);
        var incomingEvidence = EvidenceFor(incoming);
        if (!string.IsNullOrEmpty(incoming.RequestFingerprint) &&
            (string.IsNullOrEmpty(target.RequestFingerprint) || incoming.ObservedAt >= target.ObservedAt))
        {
            // Fingerprint, event time and Proxy ID must describe the same request.
            // Page inspection metadata can survive aggregation; event provenance cannot.
            if (target.RequestFingerprint != incoming.RequestFingerprint || target.ObservedAt != incoming.ObservedAt)
            { target.BurpProxyRequestId = null; target.BurpCaptureId = ""; target.BurpRequestTime = null; }
            target.RequestFingerprint = incoming.RequestFingerprint;
            target.ObservedAt = incoming.ObservedAt;
            target.StatusCode = incoming.StatusCode;
            target.ContentType = incoming.ContentType;
            if (incoming.BurpProxyRequestId.HasValue)
            { target.BurpProxyRequestId = incoming.BurpProxyRequestId; target.BurpCaptureId = incoming.BurpCaptureId; target.BurpRequestTime = incoming.BurpRequestTime; }
        }
        // Only the same send event can replace an older/less complete summary.
        // Other events without a baseline retain their complete input signals.
        var evidence = previousEvidence.Concat(incomingEvidence)
            .GroupBy(x => x.EventKey, StringComparer.Ordinal).Select(group =>
            {
                var preferred = group.Any(x => x.BaselineMatched) ? group.Where(x => x.BaselineMatched) : group;
                var rows = preferred.ToArray();
                return new RequestPriorityEvidence(group.Key, rows.All(x => x.BaselineMatched),
                    rows.SelectMany(x => x.FieldNames).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxFields).ToArray(),
                    rows.SelectMany(x => x.SemanticSignals).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    rows.SelectMany(x => x.OperationTerms).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            }).ToArray();
        target.RequestPriorityEvidence = evidence.Length <= MaxFields ? evidence : evidence.Take(MaxFields - 1).Append(new RequestPriorityEvidence("overflow", false,
            evidence.Skip(MaxFields - 1).SelectMany(x => x.FieldNames).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxFields).ToArray(),
            evidence.Skip(MaxFields - 1).SelectMany(x => x.SemanticSignals).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            evidence.Skip(MaxFields - 1).SelectMany(x => x.OperationTerms).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())).ToArray();
        if (evidence.Length > MaxFields) target.RequestContentLimitations = Union(target.RequestContentLimitations,
            ["採点用イベントは128件まで保持し、超過分の入力項目・重要情報の信号を未比較の集合として残しています。"]);
        target.RequestFieldNames = Union(target.RequestFieldNames, incoming.RequestFieldNames);
        target.RequestHeaderNames = Union(target.RequestHeaderNames, incoming.RequestHeaderNames);
        target.RequestSemanticSignals = Union(target.RequestSemanticSignals, incoming.RequestSemanticSignals);
        target.RequestOperationTerms = Union(target.RequestOperationTerms, incoming.RequestOperationTerms);
        target.RequestOperations = (target.RequestOperations ?? []).Concat(incoming.RequestOperations ?? []).Distinct().Take(MaxFields).ToArray();
        target.RequestContentLimitations = Union(target.RequestContentLimitations, incoming.RequestContentLimitations);
        target.RequestBodyCaptured |= incoming.RequestBodyCaptured;
        if (string.IsNullOrWhiteSpace(target.RequestContentType)) target.RequestContentType = incoming.RequestContentType;
    }
    private static string[] Union(string[]? left, string[]? right) => (left ?? []).Concat(right ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxFields).ToArray();
    private static RequestPriorityEvidence[] EvidenceFor(ObservedRequest request) => (request.RequestPriorityEvidence?.Length ?? 0) > 0 ? request.RequestPriorityEvidence! :
        request.RequestBodyCaptured ? [new(string.IsNullOrEmpty(request.RequestFingerprint) ? "legacy-" + request.Id :
            DocumentHintService.Sha256(request.RequestFingerprint + "\n" + request.ObservedAt.ToUniversalTime().ToString("O")), false,
            request.RequestFieldNames ?? [], request.RequestSemanticSignals ?? [], request.RequestOperationTerms ?? [])] : [];
    private static bool IsPersonalField(string field) => Personal.IsMatch(field) &&
        !Regex.IsMatch(field.Split('.').Last(), @"(?:^|[_-])(?:enabled|disabled|visible|hidden|show|hide|available|configured)(?:$|[_-])", RegexOptions.IgnoreCase);
    private static string Text(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
