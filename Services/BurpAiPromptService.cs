using ScopePilot.Domain;

namespace ScopePilot.Services;

public sealed record BurpAiPromptResult(bool Ready, string Text, string Message);
public static class BurpAiPromptService
{
    public static BurpAiPromptResult Build(IEnumerable<DiagnosticCandidate> candidates)
    {
        var selected = candidates.Where(x => x.Selected).OrderByDescending(x => x.Priority).ThenBy(x => x.Pattern, StringComparer.Ordinal).ToArray();
        if (selected.Length == 0) return new(false, "", "候補レビューで診断対象を採用してください。");
        var missing = selected.Count(x => !x.Representative.BurpProxyRequestId.HasValue);
        if (missing > 0) return new(false, "", $"採用済み{selected.Length:N0}件中{missing:N0}件のBurp Request IDが未照合です。［Burp IDを照合］を実行してください。未照合分を省略したプロンプトは生成しません。");
        var ids = selected.Select(x => x.Representative.BurpProxyRequestId!.Value).Distinct().ToArray();
        return new(true, "下記request IDを対象にActiveScanを左から順に実施してほしい。" + Environment.NewLine +
            string.Join(',', ids) + Environment.NewLine + "必要に応じて、macro/cph/session handling ruleを設定してほしい。",
            $"採用済み候補を優先度順に並べたRequest ID {ids.Length:N0}件です。接続中のBurpプロジェクトを確認してから引き渡してください。");
    }
}
