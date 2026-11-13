using System.Text.Json;

namespace ScopePilot.Services;

internal static class ExplorationPhase
{
    internal const string FileName = "exploration-phase.json";
    internal const string LifecycleClarification = "\n通常機能としての行単位削除・一括削除・ごみ箱の復元/永久削除・バージョン削除の検証は、終了処理へ延期せず通常探索・追加点検で行います。依存データを壊さないよう、画面から別の今回用データを作り、削除・復元の要求と結果を確認してください。最後に残るデータを一掃する片付けと、通常機能としての削除テストを区別します。「ごみ箱を空にする」「全件削除」「全件処理」自体が確認対象の通常機能である場合も同様です。依存データの維持は、その機能の永久禁止ではなく操作順の条件です。先に件数・ページ送り等の依存する確認を終え、画面で影響対象と案件の許可範囲を照合してから、許可された通常機能として送信します。まだ依存する確認があればそれを先に実施し、単に依存データがあることだけを理由に全件処理を未実行のまま繰り返し返しません。許可範囲に収まらない場合は送信せず具体的な対象と条件を記録します。ログアウトだけは終了処理フェーズまで行いません。\n";
    internal const string ReviewInstruction = """

        これは通常操作の独立した棚卸しです。前回報告に含まれなかった操作も実際のUIと仕様書に照らして確認してください。
        前回結果の未実行キューは、棚卸し対象の上限ではありません。キューの1件だけを再確認して、この独立点検を終了しません。
        入力資料にUI操作の確認表・未確認候補がある場合、各候補を実画面と照合し、前回報告にない候補も1操作1行で結果に含めます。未実行はnot_attempted、実行不能は具体的なUI状態と必要条件を記録し、確認表の存在だけでobservedにしません。
        各一覧の検索欄・各種絞込・並替・ページ送り、行メニューの個別操作と一括ツールバー、編集内の権限保存、各設定タブの変更保存、詳細画面内の関連項目作成を点検します。
        詳細フォームの名称・日付・識別番号・関連項目は、編集可能な欄の変更保存と復元を確認します。ダウンロードのオプション、保存した絞込やビューを実際に開く操作も点検します。
        選択処理は単体と2件以上を区別します。1件をチェックしただけの操作を、複数件を選択した検証へ代用しません。追加HTTPが生じないUI操作はその事実を記録します。
        同じHTTP先でも、個別/一括、別画面の入口、検索条件種類、本文actionが異なるものは別項目とします。通常更新の本文に全フィールドが含まれるだけで、権限や全設定タブを変更済みとは数えません。
        データが足りなければ通常のUIから今回用のデータを作ります。次ページのために大量データが必要なら、その前提と未確認を記録し、無制限に作成しません。
        複数の画面を1項目にまとめず、1画面・1操作の行に分解し、operation名を以後維持します。実通信と結果に根拠のない完了分類はしません。
        特殊操作はmanual_requiredのまま残します。通常の検索・保存・個別/一括操作を、単に未実行という理由で手動へ移しません。
        """ + ExplorationPackageBuilder.SemanticPolicyReminder;

    internal static async Task SetAsync(string directory, string stage, bool allowLogout, CancellationToken token)
    {
        var target = Path.Combine(directory, FileName);
        await File.WriteAllTextAsync(target + ".tmp", JsonSerializer.Serialize(new { stage, allowLogout, updatedAt = DateTimeOffset.UtcNow }), token);
        File.Move(target + ".tmp", target, true);
    }

    internal static async Task<string?> FinalizationAsync(string directory, CancellationToken token)
    {
        var contractPath = Path.Combine(directory, "engagement.json");
        var reportPath = Path.Combine(directory, "exploration-summary.json");
        if (!File.Exists(contractPath) || !File.Exists(reportPath) || !File.Exists(Path.Combine(directory, "coverage-review-completed.json"))) return null;
        await ExplorationCoverage.ReconcileOperationPagesAsync(directory, token);
        await ExplorationCoverage.SynchronizeInventoryAsync(directory, token);
        using var contract = JsonDocument.Parse(await File.ReadAllTextAsync(contractPath, token));
        if (!contract.RootElement.TryGetProperty("aiInteractions", out var enabled) || !enabled.GetBoolean()) return null;
        var text = await File.ReadAllTextAsync(reportPath, token);
        var normalized = System.Text.Json.Nodes.JsonNode.Parse(text)!.AsObject();
        if (await ExplorationAuthenticationEvidence.ReconcileAsync(directory, normalized, token))
        {
            var backup = Path.Combine(directory, "authentication-report-before-reconciliation.json");
            if (!File.Exists(backup)) await File.WriteAllTextAsync(backup, text, token);
            text = normalized.ToJsonString();
            await File.WriteAllTextAsync(reportPath, text, token);
        }
        using var report = JsonDocument.Parse(text);
        if (!report.RootElement.TryGetProperty("operationCoverage", out var coverage) || coverage.GetArrayLength() == 0 ||
            coverage.EnumerateArray().Any(item => ExplorationCoverage.IsPending(item, OperationEvidencePolicy.AllowedOrigins(contract.RootElement)))) return null;
        if (!await ExplorationCoverage.HasTimeAsync(directory, token)) return null;
        await File.WriteAllTextAsync(Path.Combine(directory, $"exploration-round-{ExplorationCoverage.FinalizationRound}.json"), report.RootElement.GetRawText(), token);
        await SetAsync(directory, "finalization", true, token);
        return """

            ScopePilotが通常操作の独立点検と未実行キューを検査し、終了処理へ進めました。
            今回の最終パスに限り、今回作成したテストデータの片付けと変更した設定の復元をUIから行ってください。
            特殊操作のmanual_requiredは変更・実行せず残します。新しい通常操作の未確認が見つかったらログアウトを止め、その操作と理由を報告します。
            新しい通常操作がなく、片付けが済んだら、案件で許可されている場合だけ最後にUIからログアウトを確認できます。
            今回更新した項目と片付け・ログアウト結果だけ返し、前回確認済みの項目を失わせないでください。
            """ + ExplorationPackageBuilder.SemanticPolicyReminder;
    }
}
