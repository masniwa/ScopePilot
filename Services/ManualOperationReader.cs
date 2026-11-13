using System.Text.Json;

namespace ScopePilot.Services;

public sealed record ManualOperationItem(string Page, string Operation, string Reason, IReadOnlyList<string> Requests);

public sealed record ManualOperationReadResult(string Status, string Message, IReadOnlyList<ManualOperationItem> Items, string SourcePath);

/// <summary>Reads existing exploration reports without executing or changing the recorded operations.</summary>
public sealed class ManualOperationReader
{
    internal const long MaximumFileBytes = 16 * 1024 * 1024;
    internal const int MaximumRows = 10_000;
    private static readonly string[] FileNames = ["manual-operations.json", "operation-inventory.json", "exploration-summary.json"];

    public async Task<ManualOperationReadResult> ReadAsync(string runDirectory, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var source = "";
        try
        {
            if (string.IsNullOrWhiteSpace(runDirectory)) return Missing("実行を選択してください。手動項目の有無は未確認です。");
            var root = Path.GetFullPath(runDirectory);
            foreach (var name in FileNames)
            {
                token.ThrowIfCancellationRequested();
                source = Path.Combine(root, name);
                try { File.GetAttributes(source); }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { continue; }
                ProjectPaths.EnsureChild(root, source);
                await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length > MaximumFileBytes)
                    return Error("手動項目の記録が読取上限（16 MB）を超えています。元ファイルを確認してください。", source);
                using var document = await JsonDocument.ParseAsync(input, cancellationToken: token);
                var rows = document.RootElement;
                if (name != "manual-operations.json")
                {
                    if (rows.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Report root must be an object.");
                    if (!rows.TryGetProperty("operationCoverage", out rows))
                        return Missing("この実行には手動項目の記録がありません。有無は未確認です。", source);
                }
                if (rows.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Operation list must be an array.");
                if (rows.GetArrayLength() > MaximumRows)
                    return Error("操作一覧が読取上限（10,000行）を超えています。元ファイルを確認してください。", source);

                var result = new List<ManualOperationItem>();
                foreach (var row in rows.EnumerateArray())
                {
                    token.ThrowIfCancellationRequested();
                    if (row.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Operation row must be an object.");
                    if (RequiredText(row, "outcome") != "manual_required") continue;
                    var operation = RequiredText(row, "operation");
                    if (string.IsNullOrWhiteSpace(operation)) throw new InvalidDataException("Operation name is missing.");
                    var page = RequiredText(row, "page");
                    var reason = RequiredText(row, "reason");
                    var requests = new List<string>();
                    if (row.TryGetProperty("requests", out var references))
                    {
                        if (references.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Request references must be an array.");
                        foreach (var reference in references.EnumerateArray())
                        {
                            if (reference.ValueKind != JsonValueKind.String) throw new InvalidDataException("Request reference must be text.");
                            requests.Add(reference.GetString()!);
                        }
                    }
                    result.Add(new(page, operation, reason, requests.ToArray()));
                }
                return result.Count == 0
                    ? new("empty", "この実行の記録に手動項目はありません（0件）。", result, source)
                    : new("loaded", $"手動項目 {result.Count:N0}件。項目を選択すると、理由と記録された通信を確認できます。", result, source);
            }
            return Missing("この実行には手動項目の記録ファイルがありません。有無は未確認です。");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // An unreadable authoritative report must not become a misleading
            // empty list or silently fall back to an older report.
            return Error("手動項目を読み取れません。記録ファイルの形式・アクセス権を確認してください。", source);
        }
    }

    private static string RequiredText(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : throw new InvalidDataException($"{name} must be text.");

    private static ManualOperationReadResult Missing(string message, string source = "") => new("missing", message, [], source);
    private static ManualOperationReadResult Error(string message, string source) => new("error", message, [], source);
}
