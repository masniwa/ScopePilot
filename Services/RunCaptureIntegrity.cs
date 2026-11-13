using System.Security.Cryptography;
using System.Text.Json;

namespace ScopePilot.Services;

public sealed record RunCaptureIntegrityResult(bool Saved, bool Complete, string Message);

public static class RunCaptureIntegrity
{
    // Metadata-only decision plus the committed HAR hash. A browser-stage audit
    // cannot certify the later wire supplements, even if its counts matched.
    public static async Task<RunCaptureIntegrityResult> CheckAsync(string directory)
    {
        var harPath = Path.Combine(directory, "fast-capture.har");
        var wirePath = Path.Combine(directory, "wire-capture-summary.json");
        var auditPath = Path.Combine(directory, "capture-audit.json");
        if (!File.Exists(harPath) || !File.Exists(wirePath) || !File.Exists(auditPath))
            return new(false, false, "最終HAR・wire照合・最終監査のいずれかが未完了です。原本は部分結果として保持します。");
        try
        {
            using var wire = JsonDocument.Parse(await File.ReadAllTextAsync(wirePath));
            using var audit = JsonDocument.Parse(await File.ReadAllTextAsync(auditPath));
            var w = wire.RootElement; var a = audit.RootElement;
            if (Text(w, "stage") != "reconciled" || Text(a, "stage") != "wire" ||
                !w.TryGetProperty("har", out var identity) || !a.TryGetProperty("har", out var audited))
                return new(false, false, "補完後のHAR保存・最終監査を確認できません。補完前の一致件数を最終成功とは扱いません。");
            var bytes = new FileInfo(harPath).Length;
            await using var stream = File.OpenRead(harPath);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
            var journal = Path.Combine(directory, "http-transactions.jsonl");
            if (identity.GetProperty("bytes").GetInt64() != bytes || audited.GetProperty("bytes").GetInt64() != bytes ||
                Text(identity, "sha256") != hash || Text(audited, "sha256") != hash || !File.Exists(journal) ||
                w.GetProperty("journalBytes").GetInt64() != new FileInfo(journal).Length || a.GetProperty("journalBytes").GetInt64() != new FileInfo(journal).Length)
                return new(false, false, "HARまたは通信台帳が最終照合時から変わっています。完全保存を確認できません。");
            var complete = a.TryGetProperty("complete", out var ac) && ac.ValueKind == JsonValueKind.True &&
                w.TryGetProperty("complete", out var wc) && wc.ValueKind == JsonValueKind.True;
            return new(true, complete, complete ? "補完後のHARと通信台帳の最終照合を確認しました。" : "最終HARは保存済みですが、本文取得制約または未解決の要求が残ります。");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or FormatException or InvalidOperationException or KeyNotFoundException)
        { return new(false, false, "最終通信保存の証拠を検証できません。部分結果として扱います。"); }
    }

    private static string Text(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
