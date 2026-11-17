# AI入力・送信の検証

## 明示禁止の全探索経路への適用（2026-11-11）

修正前の実Edge・ローカル受信照合はtemp/forbidden-actions-audit-20261110/result.json。aria-labelledby、role=menuitem内のアイコン、file inputのchange、初期GETの禁止URL、フォーム自動送信の禁止語句で遮断漏れを確認した。新しい実装は、全フェーズでHTTPメソッドによらずURLを照合し、全ドキュメントへ初期化時に操作ガードを設定する。認証/フォーム送信の許可窓や終了処理が明示禁止を解除しない。手動の認証にもこの指定を適用する。

ForbiddenActionsTestsとforbidden-actions-integration.cjsの合格条件:
- AI入力送信ON/OFFとフォーム自動送信ON/OFFの4組合せ、および認証待ちで、禁止URL・表示名の要求を受信側へ送らない。
- aria-label/aria-labelledby、画像alt、ネストしたメニュー、open shadow DOM、iframe、ポインター、Enter、ファイル選択/input/changeを通じても同じ禁止語句を適用する。
- 送信先URLの禁止はGET/HEAD/POST/PUT/PATCH/DELETE/OPTIONS/PROPFINDで維持し、許可された同メソッドはAIモードで実際に受信する。
- formAction/formMethodの上書き、外部に関連付けられた送信ボタン、同じ先へ送る禁止フォームと許可フォーム、複数送信ボタン、action/method/elements/requestSubmitという名前の入力欄を区別する。禁止指定だけのために安全な保存操作を止めない。
- 初期GETのリンク表示名とURLを照合し、ページの背景通信も禁止URLなら送信しない。禁止されたフォームは仮入力の前に除外し、実送信時もガードで再検査する。
- 手動認証の許可済みログインは成立し、禁止操作とURLには認証待ちの例外を作らない。終了フェーズのallowLogout=trueでも明示禁止のURLは解除しない。
- 設定保存/再読込、Codex/GLMが共有する実行入力とプロンプト、レポートに同じ指定を維持する。空欄はHTTPメソッド制限を追加せず、旧既定値だけの読込移行は元ファイルを書き換えない。
- 禁止の試行と許可された通信をジャーナル/HARへ保持し、件数・対応の監査が成功する。

変更の検証はtemp/forbidden-actions-fix-20261111配下に保存する。最初の通常出力のビルドは起動中のScopePilot.exeのファイルロックでテスト実行前に失敗し、targeted/targeted-correctedに記録した。別出力のtargeted-isolatedはfixtureのshadow DOM生成用JavaScriptの誤記で1失敗、targeted-isolated-correctedは同一送信先フォームの取り違えで1失敗。後者は実装でフォームの識別を補い、form-identity-testsで2合格。途中版の全回帰complete-regressionは268合格・失敗/skip0。その後の旧既定値移行とフォーム名衝突を含む関連11件がfinal-targetedで成功し、最終版の全269件をfinal-complete-regression/results.trxで再実行して合格・失敗/skip0を確認した（実ZAP4件を含む）。実Codexの限定代表runでも、許可した単独Save draftのbrowser_click/POSTが1回、禁止操作/URLの受信0件、HAR/台帳5/5一致を確認。範囲外の候補は未実行のままpartial/no_progressへ残した。ai-workflow/verification.json参照。検証用fixtureは停止した。通常起動先は実行中ScopePilot.exeにロックされているため配備待ちで、検証先DLL・実AIのDLL・配備予定DLLのSHA-256一致をdeployment-plan.jsonへ保存した。過去の失敗を合格へ数えない。

## Juice Shopの最終探索と終了判定の追加修正（2026-11-10）

42000再開の明示承認後、公式19.1.1・commit c8c407d503ce9c8e6582b39b40db84db06989744に合わせた5資料を通常の複数取込・AI抽出へ渡し、formal-3/run 20261110-220728-691-aed38b7f2a2を実行した。以後の評価はこのrunだけを分子に用いる。開始時の提供アカウントは先行runでDeluxeになっており、非会員の開始状態とは異なる。画像/PDFの準備と、登録後の認証切れに対する手動再認証を補助した。攻撃・Scanは実施していない。

推定分母は独立に作成した79候補から同一遷移の重複2件、現主体に適用できない加入経路2件を分け、UI・公式ソースに照らした別入口/状態36件を追加した。現主体のUI操作111中103（92.8%）、HTTPを伴う操作83中75（90.4%）。通常HTTP操作は77中75（97.4%）、手動6件、通常未取得2件は一覧の同一商品の再追加とログアウト。非会員の条件分岐も含める拡張分母ではHTTP85中75（88.2%）。クーポン404・売切れ400も送信取得として数え、業務成功率とはしない。管理・会計、外部Origin、ハードdisabledのPDF/Excel、根拠のないパスキー例は成功にも現主体の分母にも加算しない。判定表と採否理由はtemp/juice-shop-20261110/formal-3/coverage-assessment.json。

正常終了、captureFinalized=true、closeFailed=false。ジャーナル/HARは1065/1065、欠落/余剰0。NetLog送信1037と固定範囲の公開MCP履歴1037をMETHOD・URL・時刻で対応付け、欠落/余剰0（うちMCPで応答観測がない17件は要求対応だけ確認）。ブラウザ側で応答行がないsocket polling 1件も要求として残り、Burp側は200。要求本文不足0・未解決0。応答本文は5リダイレクト未公開と画像1件の部分取得が残り、complete=falseを維持する。遷移先/profileの200も保存した。socket pollingのPlaywright本文エラー2件はNetLog実バイトで補完した。通信原本は評価で変更していない。

終了処理は、既に会員・ハードdisabledの3件が通常キューへ残りno_progressとなったため未実行。ログアウトと全体清掃を確認済みにしない。現runの画像345は元画像のSHA-256と一致し、表示名と作成住所の編集値も復元を確認したが、作成した注文・カード・住所・投稿・ユーザー、ウォレット残高などは残る。restoration-review.json参照。

この実探索後、unavailableへ任意のunavailability記録を追加した。unsupported_in_version/not_applicable_to_current_subjectと、実UI・対象版ソースの両方の根拠が揃った場合だけ再点検キューを解消し、outcomeはunavailable/partialのまま維持する。旧報告は従来判定を継続する。件数不足・認証切れ・根拠不足は未確認のままとする。Codex用の新スキーマは各項目にnullableなunavailabilityを要求する。独立点検前に終了できないこと、未取得をobservedに変えないこと、証拠の片方不足や不明分類が終了を許さないことを追加検証した。

終了判定修正を含む全回帰265合格・失敗/スキップ0（実ZAP4件含む）はpost-availability-complete-regression/results.trx。その後のスキーマ必須/nullable整合はavailability-schema-final-testsへ別記録する。初回追加テストのusing不足によるビルド失敗、次のfixture必須reason不足による1失敗も各ディレクトリに保持し、合格へ算入しない。availability-ai-reviewでは実Codexへ非秘密の現run証拠と版固定ソースを渡し、非対応2件・現主体非適用1件を分類し、件数不足と同一商品再追加の未実行を確定非対応へ移さない出力を確認した。この追加終了判定版で対象サイト全体の再探索は行っておらず、上記の75/83を追加修正後の再測定値としない。

認証前の可視リンク保持、認証のやり直しと公開登録/再設定閲覧の区別、外部Originの未実行判定、固定UI識別子の維持を修正し、formal-3で登録・再設定画面と新規登録を確認した。文書の一般例を実在機能に数えない。同一商品再追加の別枝は次回用資料へ追記し、今回の未実行判定とrun保存資料は変更しない。PDFの新規タブ、フォームを使わないSPAログイン、#/ルート、HAR終了時の要求保持は後続の全回帰にも含めた。


### 追加した限定UI回帰

focused-regression/run 20261110-231715-140-60ce42fecf9は、追加終了判定版と更新資料を使い、同一商品再追加・作成行削除・3件の可用性・終了ログアウトだけを標準サービスで確認した。案件は正式型の設定を用い、生成promptへ対象限定の回帰範囲を明示した。初回の作業用案件でGuidelinesを文字列にしたロード失敗はhost-project-load-failure.logへ保持し、通信runに加算しない。

AIが商品一覧の同じAdd to Basketを2回クリックし、POST160とPUT165が200、数量2を表示。今回作成した行18のDELETE172が200となり空バスケットへ戻った。非対応2件・現主体非適用1件はunavailability付きunavailableを維持し、独立点検後queue_emptyで終了処理へ移行。最後にAccount→Logoutを1回クリックし、関連GET /rest/saveLoginIp要求193が200、AccountメニューがLoginだけになることを確認した。ログアウト専用APIがあるという主張ではない。以前の全探索のデータは片付けていない。

終了コード0・captureFinalized=true・closeFailed=false。ジャーナル/HAR215/215、Wire/Burp202/202の欠落・余剰0、要求/記録済み応答本文エラー0、pending0。ブラウザに応答行がないsocket polling要求108は残り、Burpは200。全応答がブラウザと一致したとはしない。verification-summary.jsonとburp-wire-correlated-audit.json参照。この限定回帰を全探索の75/83へ足さず、率は90.4%のまま維持する。追加スキーマ関連30件も失敗/スキップ0で完了した。

## Juice Shop準備で検出したSPA認証・終了保存（2026-11-10）

明示的な42000再開承認後、準備ブラウザでapplication-versionの200応答から19.1.1を確認した。直接TCPの確認は一時的に失敗したが、Burp経由の実ブラウザで正常応答を受けているため、直接TCPだけで対象停止と判定しない。公式v19.1.1コミットc8c407d503ce9c8e6582b39b40db84db06989744へ資料を合わせ、4資料を通常の複数取込・文書AI抽出へ渡した。準備runを本探索の取得実績へ合算しない。

初回準備の終了時にHAR45件とブラウザ要求46件が不一致となった。閉じる直前のsocket.ioポーリング1件をChromiumがHARへ出力していない。append-only台帳にある実際の要求だけをHARへ補い、元のブラウザHARはbrowser-capture.original.harへ保持する。台帳由来の試行は実送信とみなさず、応答・失敗・完了を推測しない。NetLog照合は従来どおり行う。不明な余剰HARは引き続きエラー。実記録のコピーでは46/46、実送信41件の既存要求への対応、原本ハッシュ不変を確認したが、未解決2件・本文中断1件は不完全のまま残した。修正は通信・応答の完全成功を意味しない。証拠はtemp/juice-shop-20261110/shutdown-replay/。元の失敗runを成功へ書き換えない。

実UIのログインがformタグ外の入力欄と#/loginであるため、可視の独立入力欄を検出し、可視のLogoutボタン・メニュー項目も肯定的な表示として扱う。入力値は検出結果に含めない。ブラウザ開始URLはSPAフラグメントを保持し、HTTP送信用のAPI URL検証は変更しない。認証情報埋込み・制御文字・外部Originは拒否する。認証後の明示ルートの不一致も未到達とする。表示用URLは通常のSPAルートだけを残し、クエリーとOAuth形式のフラグメントは除去する。

同一Originのredirectリンクに明示された外部URLへ自動巡回した事実も確認し、外部転送先をURLのクエリーに持つリンクを巡回前とナビゲーション要求時に除外する。これは保守的な事前検査であり、未知のサーバー応答だけで決まる外部リダイレクトをすべて送信前に遮断したという証明ではない。現在の本探索では観測済みの外部GitHub転送リンクの除外を確認した。

WireCapture回帰は反復URL・HAR欠落要求・保存済み応答本文・要求本文・未解決の非推定・元HAR保持・余剰HAR拒否を追加。SpaAuthenticationTestsは実Edgeで非form入力・可視Logout・非表示除外・秘密値の非出力・ハッシュルート・外部転送先の事前検査を確認する。関連3件、認証関連20件、その時点の全回帰245件（実ZAP4件含む、失敗/スキップ0）が成功。その後に判明したC#開始URL検証の修正は関連22件で成功。証拠は同temp配下のshutdown-targeted-tests、spa-auth-targeted-tests、complete-regression-after-shutdown-spa、navigation-validation-tests。その後の新規タブ・結果取込の最終修正も含む全回帰は249合格・失敗/スキップ0で、after-popup-complete-regression/results.trxに保存した。途中のspa-collector-validation-testsは先行テストのDLLロックでテスト実行前に失敗しており、合格件数へ含めない。先行テスト終了後にpopup-and-collector-testsを直列実行し24合格を確認した。

初回本探索run20261110-204609-841-68741d3d69cは注文確認PDFの新規タブで、frame生成前のrequest.frame()参照によりクローラが停止した。部分結果と未実行キューを保持し、2回の進展なしで終了した。さらに結果取込時にSPA到達URLの拒否が顕在化した。このrunはpartialのまま保持し、最終取得率へ加算しない。新規タブ初回要求のframeが未生成なら上位ナビゲーションとして保守的に上限へ数え、結果取込にはブラウザ用のOrigin検証を用いる。ApiRequestFormatterの送信URL検証は変更しない。実Edgeの通常popupとframe未生成分岐、同一OriginのSPA到達のロール保持・外部Origin拒否を回帰で確認した。

再探索run20261110-205942-930-0a3c93db4d6は4資料を通常の複数取込で改めて保存し、同一Origin・同一資料名/順序/内容の有効な文書ヒントだけを再利用する。一般ユーザーの認証後到達を確認してAI探索を開始した。実行版と通常起動先のDLL・JSハッシュ一致はformal-2/run-source-manifest.jsonへ保存する。ここで最終網羅率・保存完全性は未判定。原本と進行はtemp/juice-shop-20261110/formal-1/、formal-2/とresumption-checkpoint.jsonを参照する。


## 複数資料の入力と参照（2026-11-10）

Web探索用の`ExplorationDocument.Files`へ資料の一覧を保存し、既存の`FileName/Content/Source`だけの案件は1件として読み込む。新しい選択画面は複数追加と個別解除・全解除に対応し、APIモードの資料は独立したままである。解除してもコピー済み原本と過去runを削除しない。最大20件・区切り見出しを含む合計500,000文字を取り込み前に検証し、不正ファイルや上限超過で一部だけを読み込まない。同名でも内容が違う資料は別に保持する。

個別資料は各runの`reference-documents/NNN.txt`と出典/ハッシュの`index.json`へ保存し、既存の`reference-document.txt`へ全資料を区切ってまとめる。Codex/GLMの共通プロンプトに全資料本文と索引を渡す。文書ヒントの抽出も全資料を入力とし、資料名・順序・内容の変更は結合内容のSHAでキャッシュを無効化する。単一の既存資料は従来と同じ本文を保持する。全資料の取り込み元ハッシュを確認してからrunを作成する。

`MultipleExplorationDocumentTests`は、旧1件指定からの追加、同名資料の保持、重複選択の抑止、手動保存、個別解除、フォルダー移動、全資料のスナップショット/AI入力、読取AIの維持、不正な追加・件数/総文字数超過の取り消し、2件目原本の改変拒否、全資料のヒント入力とキャッシュ失効を検証する。新規7ケースと既存文書・ヒント・WPFレイアウトを合わせ30合格・失敗0。全回帰は実ZAP4件も含め244合格・スキップ0・失敗0。証拠は`temp/multi-documents-20261110/test-results/multiple-targeted-safe/results.trx`と`complete-regression/results.trx`。

実画面では通常の`Start-ScopePilot.cmd`から起動し、専用の検証案件でnativeファイル選択の2件同時選択、一覧2件、保存、1件だけの解除、再保存と原本2件の保持を確認した。`temp/multi-documents-20261110/ui-verification.json`と`ui-check/project.scopepilot.json`を参照。新しい本体をテストしており、現在実行中の旧探索へ変更が遡って適用されるとは扱わない。実サイトの網羅率と、資料参照による効果は別の探索で確認する。

公開ソースから作成した3資料を実際のCodex CLI（gpt-6.1-sol・Middle）へまとめて渡す追加確認も実施した。全資料名と機能・状態遷移・制約を参照した抽出結果を得た。対象版が未確認のため結果は正しくpartialで、具体的なAPI URLを捏造せずendpoints/API variationsは空。検証案件のOriginはexample.testで、文書抽出後に終了する専用hints-only実行を用い、ブラウザ・プロキシ・実対象への接続は行っていない。`temp/juice-shop-20261110/document-ai-check/`と`document-ai-verification.json`を参照。本探索へこの下書きやexample.test用ヒントのキャッシュをそのまま転用しない。

## 実ZAP連携テストの準備（2026-11-10）

公式の[ZAP 2.17.0 Crossplatform版](https://github.com/zaproxy/zaproxy/releases/tag/v2.17.0)を`.cache/zap/ZAP_2.17.0`へ展開した。取得ZIPのSHA-256は公式値`94c8f767b1c2e94f0db66b3ae56514d5e3f5a728ee1b6c798e0c8fe2d61fbff0`と一致。Javaは既存のTemurin 21.0.12.1を利用する。6つの必須add-on（automation、exim、network、pscan、pscanrules、reports）は配布ZIPに含まれ、実起動での読込確認は`zap-environment.json`に記録する。ローカル配置とハッシュは`temp/zap-setup-20261110/installation-manifest.json`、テスト用パスは`.cache/zap/runtime.json`へ保存する。

`Test-With-Zap.cmd`は`SCOPEPILOT_TEST_ZAP_JAR`・`SCOPEPILOT_TEST_JAVA`を設定して全回帰を直列実行する。4件だけの場合は`Test-With-Zap.cmd -InstalledZapOnly`を使用する（PowerShellからcmdへOR条件を渡す際のパイプ解釈を避ける）。既定の出力先は`temp/zap-tests/日時-ID/`で、`-ResultsDirectory`で変更できる。TRX・コンソールログ・検証成果物を保持し、fixture用のTEMP/TMPもその配下へ配置する。設定は子プロセスだけへ渡し、ユーザー/システム環境変数や既存案件のプロキシ設定は変更しない。PowerShellの実行ポリシー指定も起動したプロセス内だけである。

4件の合格条件は、オフラインHARの取込・パッシブ解析・HTML/JSONレポート生成と結果取込、ZAP経由のローカルHTTP/HTTPS巡回とHAR出力・終了、ブラウザ記録とZAP履歴の照合、ユーザー停止時の証拠保存後のZAP終了である。対象は合成HARとローカルfixtureで、実対象サイトの探索やアクティブ診断ではない。

初回にHAR解析の起動が8080番ポート競合で失敗した。ZAPの`-cmd`もメインプロキシを開始し、この版ではCLIの`-port 0`指定が既定ポートを置き換えなかった。`ZapCliRunner`は既存のmanaged daemonと同じ空きloopbackポートの引渡し方式へ修正した。既存の待受を停止・設定変更して回避せず、HAR・レポートの合格条件も維持する。CLI起動時のポート引渡しには短い競合余地が残る点はmanaged daemonと同じで、起動失敗を正常終了とは扱わない。

最初のテストランチャーには、Windows PowerShellがテスト失敗の標準エラーを例外化し、完了出力を受け取る前にランチャーが終了する問題もあった。標準エラーをログへ取り込み、nativeの終了コードを返すよう修正した。初回の`InstalledZap`だけの絞込は3件しか含まず、停止ケースが含まれなかったため、上記の4件を選ぶOR条件へ案内を訂正した。オフラインテストの失敗ログは一時fixtureの破棄前に任意の成果物フォルダーへ保持する。初回結果は`temp/zap-setup-20261110/targeted-tests`（2合格・1失敗）、原因ログは`offline-diagnostic`と`offline-fixed`、ポート修正後の単体成功は`offline-free-port`へ保存し、過去の失敗記録は上書きしない。

最終の全回帰は237合格・スキップ0・失敗0で、上記4件すべてのPassedをTRXから確認した。結果は`temp/zap-setup-20261110/complete-regression/results.trx`、ログとHAR/レポート等は同`artifacts/`へ保存。停止ケースの`status=recorded/stopReason=user_stopped`と照合結果は`observed-stop-case/`にも保持した。通常の起動先Release-reviewのDLLとテスト対象DLLのSHA-256一致を確認した。実サイトの探索・アクティブ診断・網羅率向上の確認は含まず、42000への通信も実施していない。

## 未知サイト向けの操作候補保持と進展に応じた点検（2026-11-10）

既存のAI・Playwright MCP・ブラウザ・Burp/ZAP連携と案件設定を維持し、入力・送信ONの結果スキーマへ`operationCandidates`を追加した。アプリが候補と結果を`operation-inventory.json`へ保持し、候補だけ報告された操作を`not_attempted`へ補い、後の報告から抜けても未実行キューと終了判定へ残す。入力・送信OFFでは候補の要求・保持を追加しない。状態の異なる操作はoperation名で区別する指示を両AIへ渡す。

追加点検は時間・ページ・通信の既存上限内で最大6回。初回の独立棚卸しは維持し、新しい候補の発見またはpendingからの解消が2回連続でない場合は停止する。理由や要求件数だけの変化は進展に数えない。通常未実行は`pending-operations.json/txt`、手動の特殊操作は従来の`manual-operations.json/txt`へ残す。`coverage-stop.json`にはqueue_empty、no_progress、round_limit、time_limitを記録する。終了処理の退避番号は追加点検の番号と衝突しない7とした。

`ExplorationInventoryTests`の合格条件は、候補だけの操作が次の報告で省略されても残りログアウトを止めること、再作成したデータへの成功で同じ操作を更新できること、手動対象を再試行しないこと、進展があれば3回目以降へ進むこと、2回停滞・回数上限・時間上限でpartialと未実行一覧を維持すること。理由が曖昧なunavailable、現在の未認証と過去の403の混同、理由の言換えだけによる進展判定、閲覧モードへの影響も検証する。

Release-reviewでビルド成功。対象回帰25合格・失敗0、同じビルドの全回帰233合格・4スキップ・失敗0。実Edge/MCPのローカルHTTP・プロキシfixtureによる非GET送信、記録、Origin/禁止操作制限、認証・ログアウト制御も含む。4スキップはインストール済みZAPが前提の従来ケース。証拠は`temp/unknown-site-coverage/test-results/inventory-targeted-host.trx`と`complete-regression.trx`。最初のサンドボックス内実行はテストホストとの接続が90秒でタイムアウトし、テスト実行前に中止（`inventory-targeted.trx`）。通常Windows環境で同じ対象を実行して合格した。

通常の`Start-ScopePilot.cmd`が参照する`bin/Release-review/net10.0-windows`へ反映済みで、テスト対象DLLとのSHA-256一致を確認した。実サイトでAIが新しい候補を発見・報告する効果と網羅率の増加は未測定。候補としても報告されなかった機能を機械的に検出する実装や、全サイト共通の分母確定は含まない。Paperlessの過去の161/168を修正版の測定結果へ転用しない。42000への探索はユーザーの停止指示を維持し、再開していない。

## Paperless全探索の通常終了確認（2026-11-10）

修正版の標準サービスによる独立run `20261110-082022-961-163de79dba5` は通常終了コード0で正規HARを保存した。v3.3.1公式仕様・UIソースと実画面から候補200、現ロールで対象外32、推定分母168とした。タスクの単体・複数選択・表示分を従来の1項目から3項目へ分けた改訂を含む。成功応答は161/168、通常操作は161/161、手動の特殊操作7件は未取得のまま残す。準備と過去runは加算しない。件数不足だった6一覧の次ページも今回のUI操作と200応答・実データで確認した。

終了処理への遷移を観測した後、表示件数50の保存3214と絞込みなしの通常一覧取得3225を確認した。ログアウト3279は302、ログインフォーム3280は200、sessionid消去を確認した。最後のphaseが`finished/allowLogout=false`になるため、実行中に保存した`finalization/allowLogout=true`の遷移記録と送信時刻を照合する。終了済みphaseだけを見てログアウトを未確認に戻さない。

台帳/HARは3337件一致、Chromium送信要求とBurpの今回分は3123件全件一致、欠落・余剰0、要求本文エラー0・終端未解決0である。約1.1GBのNetLogを進行通知付きで処理し、AI終了から通常保存完了までは約13分だった。復旧コピーによる補正は行っていない。応答本文12件はChromiumの中断に対応し、NetLogが受信したバイト数と保存済みバイト数が全件一致した。未完了の応答は`capture.complete=false`のまま保持する。

アプリ設定は初回と最後のGETで全項目一致し、プロフィールの氏名・メールは最後の復元PATCH応答で初回値との一致を確認した。復元後のプロフィールGETは未実施として区別する。削除済みテストビュー15の参照はUI設定配列に残り、内部参照の整理は未実施として報告した。これらを「全設定が完全に初期状態」と一括報告しない。

証拠は`temp/exploration-cycle-20261110/complete-review/coverage-final-report.txt`と同名summary、`burp-request-audit.json`、`finalization-verification.json`、`response-interruption-verification.json`にまとめた。ユーザー指定により、このPaperless確認後は42000へアクセスする前に停止する。

## 操作の同一性と保存終了の進行（2026-11-10）

実探索`20261110-054937-236-21ee0626e0f`では、同じoperationのpageが`/tasks → 文書リンク`から実URLへ変わり、旧失敗行と新しい成功行が別キーになった。制御側が旧行を未完了と扱って終了処理へ進まなかった。明示されたチェックリストIDとoperation名はpage表記の変更を超えて維持し、URL・相対パスの先頭に続く説明文も同一経路として扱う。一般的な同名操作を別ページ間で無条件に結合しない。新しい結果を採用しても以前の要求と失敗理由を残し、新しい失敗を以前の成功で隠さない。既存の重複報告はバックアップ後に再照合する。

`ExplorationOperationIdentityTests`は上記の履歴保持、別ページの一般的なSaveの分離、新しい失敗の未完了維持、旧報告の重複解消と手動項目維持を確認する。実報告の別コピーでも旧失敗と成功が1行になり、終了処理へ進める制御判定を確認した（`temp/exploration-cycle-20261110/operation-merge-realcase/result.json`）。この判定再現は実際のログアウト送信の証拠とは区別する。旧探索ではログアウト未送信を維持して報告する。

同探索の約915MBのNetLog・約330MBのHARは固定5分の終了待ちを超え、保存途中で打ち切られた。通信保存は段階・読取量等の進行記録を更新し、5分の進行停止か30分の全体上限で打ち切る。`CaptureShutdownProgressTests`は、進行中処理が無進行窓を超えて完了できること、停止した処理を待ち続けないこと、進行通知で全体上限を回避できないことを確認する。全実記録の別コピー再照合は約6分で完了し、原本のハッシュ不変を確認した。次の実探索で通常の終了処理まで確認する。

修正版の新しい準備セッション`20261110-075707-314-f2e62cc7f85`では、約888MBのNetLogを進行記録を更新しながら処理し、終了合図から464,890msで通常終了コード0となった。正規HAR/台帳は2049件一致、要求本文エラー0・終端未解決0。中断したPDF workerの部分本文5件と本文非公開1件は不完全なまま保持し、`capture.complete=false`を維持した。証拠は`temp/exploration-cycle-20261110/replenishment-complete/manual-20261110-075706/normal-shutdown-verification.json`。これは準備ブラウザの通常保存の確認であり、AI全探索・ログアウト完了の証拠や新しい探索の網羅率には合算しない。

## 部分本文の保持と完全性（2026-11-10）

上記実記録にはContent-Lengthが約1.3MBのJavaScript応答4件で数十〜数百KBだけが記録されていた。本文ファイルがあるだけで完全と扱わず、非圧縮のContent-Lengthと受信バイト数が不一致なら本文を保持したまま不完全と明示する。HARにも部分本文の警告を残す。圧縮前後の長さは直接比較せず、HEAD・204・304の本文なし応答は除外する。`wire-capture-regression.cjs`はこれらの例外と部分バイトの保持を確認する。

検証済み復旧版は`temp/exploration-cycle-20261110/auth-review/recovered-capture-verified`。HAR/台帳3067件一致、実送信2828件とBurp2828件が全件一致、要求本文エラー0・終端未解決0・部分本文4である。原本と復旧版を区別し、部分本文を成功した完全本文へ読み替えない。リクエスト保存の欠落ゼロと、応答本文の完全性は別の指標である。

ローカルの完全保存用統合ケースは、要求到着だけで終了せず有限のfixture通信が落ち着いてからCDP接続を閉じる。失敗時には不完全本文のID・パス・長さを診断へ出す。完全保存の合格条件は維持する。最新全回帰は221合格・4スキップ・失敗0（`temp/exploration-cycle-20261110/test-results/post-response-settling-complete-regression.trx`）。

## 接続待ちの要求と送信前中断の対応付け（2026-11-10）

Paperless実記録の未解決6件では、要求生成からヘッダー送信まで約8秒待つ通信を送信時刻だけで照合し、次の同一URL要求へ結び付けていた。NetLogの`URL_REQUEST_START_JOB`時刻を保存し、この開始時刻で既存の要求へ対応付ける。送信時刻・回数・転送先の記録は残す。ヘッダー未送信の中断は、開始・中断・終了がそろい、未解決ブラウザ要求との対応が双方向で一意の場合だけ失敗終端を補う。対応が曖昧なものや実送信の有無が不明なものは未解決に残し、要求・応答を捏造しない。

`WireCapturePreservesQueuedRequestAndResponseOwnership`は従来の成功応答と中断要求の分離に加え、8秒の接続待ちで次要求へ移動しないこと、送信前中断を実通信件数に増やさないこと、曖昧な中断を未解決のまま保持することを確認する。実記録の開始時刻・送信時刻・source IDだけを別ディレクトリーへ切り出した再現では、誤対応2件と重複補完3件が正しく元の5要求へ対応し、残る1件を送信前中断として記録して未解決6件が0件になった。通信原本を変更せず、修正前後を独立再現している。証拠は`temp/exploration-cycle-20261110/real-queue-replay/result.json`。全回帰は213合格・4スキップ・失敗0で、`test-results/post-queue-lifecycle-complete-regression.trx`に保存した。実行中の別探索にはこの変更を途中適用せず、終了後の評価で使用版を区別する。

## 同じrunの認証準備と終了処理（2026-11-10）

実探索では通常の150操作を確認できた一方、AIが高速巡回中の利用者ログインを別の過去観測と扱い、ログイン未実行を返したため終了処理へ進めなかった。`ExplorationAuthenticationEvidence`は、このrunの開始以降・認証後到達以前の要求と成功応答、許可Origin・認証済み到達・ロールを照合する。観測済みフォーム、認証経路または認証フィールドを持つ送信に限定し、照合できたログイン行だけを取得済みに補正する。AI用の根拠には方法・クエリーを除いたURL・応答状態・時刻等を渡し、要求ヘッダー・入力値・応答本文は渡さない。元のAI報告を別名で保持し、通信原本は変更しない。

`ExplorationAuthenticationEvidenceTests`では、通常フォームとJSON認証、別runの要求・到達、未応答・401・403、他の未実行操作と手動対象の維持、秘密値とクエリーの非転記を確認する。`AiInteractionTests`の既存終了処理・独立点検・範囲制御も実行する。実際の認証記録を使用する制御判定の再現と、新しい実探索でログアウトまで送信される確認は区別し、後者の確認前に全体解決とはしない。

修正前の独立探索`20261110-034350-566-162379ea001`は150/158操作、通常未確認はログアウト1件と手動特殊7件。HAR/台帳1993件一致、本文取得エラー0、終端未解決6件であり、原本の完全取得フラグはfalseのまま保持する。Burp観測1867要求は全件対応し、追加のChromium送信試行2件には接続エラー後の再試行・中断が記録された。実認証要求16を秘密値なしの別ディレクトリーへ再現した制御判定では、ログイン行の取得済み補正と終了処理への進行を確認した（実ログアウト送信の証明とは区別）。回帰テストは213合格・4スキップ・失敗0、結果は`temp/exploration-cycle-20261110/test-results/post-auth-evidence-complete-regression.trx`。新しい全体探索での終了処理確認を続ける。

## 独立点検の対象と全件処理の順序（2026-11-10）

前回報告がごみ箱全消去だけを未実行としていた実探索では、追加点検もその1件へ偏り、入力資料の通常操作候補を棚卸ししなかった。全消去も依存データ維持と両立しないとして2回とも未送信となった。独立点検の指示を前回結果・キューより後へ置き、キューを対象の上限にしないこと、資料の候補を実UIと照合して行単位で記録することを明確にする。全件処理が通常機能の確認対象なら、依存する確認を先に終える操作順とし、案件の許可範囲外への送信を認めない。特殊操作の手動扱いとログアウトの終了処理ゲートは維持する。

`AiInteractionTests.IndependentInventoryFollowsTheNarrowPreviousQueueAndOrdersGlobalOperations`で、1件だけの前回キューが独立棚卸しを上書きしない指示順と、依存確認・許可範囲・手動対象の条件を確認する。既存のログアウト、権限不足、一時的な件数不足、証拠結合のケースも実行する。実AI再探索で候補の操作漏れ・依存操作の先行・全件処理の要求を確認するまでは、指示の回帰テストだけで探索網羅性が解決したとはしない。

## PDFダウンロード後のAI移行（2026-11-10）

クローラ終了時の各フレームへの`evaluate`は、PDFダウンロードで残る一時的な`about:blank`ターゲットの実行コンテキストを無期限に待つ場合がある。操作ガードはブラウザ作成後・最初の画面遷移前に`addInitScript`で登録し、終了時のフレーム走査を不要にする。Origin・HTTPメソッド・ログアウトの通信制御は従来のrouteで継続する。

`ai-interaction-integration.cjs`は実Edgeで最後に拡張子のないPDFプレビューと添付ダウンロードを辿る。`CRAWL_DONE`を得て通常画面へ戻れ、同じ実行のMCP操作で非GET送信が成功し、禁止ラベル・禁止URL・Origin外送信が遮断され、HARと台帳の全件照合・本文保存が成功することを合格条件とする。ローカルの同一再現ケースでは修正前は移行せず、修正後は移行と終了保存が完了した。

## 同一URLの中断・応答の対応付け（2026-11-10）

同一URLへの複数要求で、先に中断されたブラウザ要求が、後の正常応答を持つ要求のWire割当を再帰的に押し出すケースを回帰対象にする。応答を持つブラウザ要求には応答ヘッダーを持つWire候補だけを割り当て、中断要求・未対応Wire要求も原本から除去しない。Workerの転送ではブラウザが表示するstatusとWireのstatusが異なるため、statusの完全一致を新たな必須条件にしない。

`AiInteractionTests.WireResponseDoesNotMoveToAnAbortedRepeatedRequest`と`wire-capture-regression.cjs`で、同じURLの中断・403応答・別の中断Wireを異なる入力順で照合する。403の正しい本文が応答を持つ要求へ保存され、中断要求と未対応Wireも残り、本文取得エラー・未解決が0になることを合格条件にする。既存の実Edge/MCP統合ケースでWorkerの302・転送先200を含む3回の重複転送も保持する。

実対象の旧版探索`20261110-003120-953-11b5b736fe7`では、本文取得不能だった403要求939に中断Wire7624が割り当てられ、先行する中断要求932に本文のあるWire7461が割り当てられていた。この2要求を原本から別フォルダーへ切り出して再照合し、修正前の本文エラー1件、修正後0件を確認。元の探索原本は変更せず`capture-summary.complete=false`を維持する。再現結果は`temp/exploration-cycle-20261110/real-gap-replay.json`、対象回帰2合格・失敗0は同`test-results/wire-match-regression.trx`。修正版での新しい全体探索・最終評価は別途確認する。

## 実行

### URL用途のフォーム入力

`FormInputSafetyTests`と`form-input-integration.cjs`は実Edgeと独立HTTP fixtureを使用する。`type=url`、`type=text`のcamelCase・小文字・underscore項目名、関連label、aria-labelledby、URLのplaceholder、autocomplete、textarea、form属性で外部関連付けされた入力、既存値・readonly値を対象とする。AI入力ON/OFFの両方で、11個のURL用途フォームへの送信0件、既存値の維持、同じフォームの別項目へのinputイベント0件、`contextual-url-input`の引継ぎ理由を確認する。一般のテキスト・email・numberフォームと固定hidden URL・disabled URL項目を含む通常フォームは各1回送信されること。単に全フォームを止めて合格にしない。AI向け指示では適切なURL・用途・影響・復元の確認と、確認できない操作の手動引継ぎを検証する。

```powershell
dotnet test Tests/ScopePilot.Tests.csproj -c Release
```

`AiInteractionTests` は設定の既定OFFと保存・再読込、実行スナップショット、モード別の指示・MCP許可ツールを確認する。
`ai-interaction-integration.cjs` は実Edge・同梱Playwright MCP・MCPツールフィルター・ローカルHTTPサイトとプロキシを使用する。

ブラウザ統合ケースの同時起動で待機タイムアウトが起きた場合、同じ全テストを`dotnet test Tests/ScopePilot.Tests.csproj -c Release -- xUnit.ParallelizeTestCollections=false`で直列実行して切り分ける。対象や合格条件を減らさず、最初の失敗と再実行の結果を両方残す。

合格条件：

- OFFでは従来の閲覧ツールだけを公開し、POSTを送信しない。
- ONではMCPの実スナップショットから入力欄・ボタンを特定して入力・クリックし、POST／PUT／PATCH／DELETEがプロキシを経由して応答200となる。
- 独自メソッドPROPFINDをメソッド制限で拒否しない。ログインPOST、ログアウトGET／POSTを許可する。
- HARに実際の要求本文・応答、観測一覧に各メソッド・URL・応答statusが保存される。
- 案件の禁止ラベル・禁止URLとOrigin外への送信は対象へ到達しない。
- 任意コード実行のMCPツールは公開しない。入力・送信モードのファイルアップロードは生成済み検証用PDFのディレクトリーに限定し、外部パス・シンボリックリンクの範囲外参照を転送しない。実際のmultipart本文で検証用バイト列が到達することを確認する。
- 認証変更後の通信を以前の認証済みロールとして誤って取り込まない。
- 同じメソッド・URLを複数回送信しても、通信原本ジャーナルとHARには毎回の要求を残す。HARの対象Origin内の件数とジャーナルのrequest行を照合する。観測一覧の代表化とは区別する。
- robots.txt・sitemap.xmlの補助取得も原本とHARに残る。別のAPIRequestContextを経由して記録対象から抜けないこと。
- メソッド・URLごとの出現回数まで照合し、件数が同じでも「POSTの欠落とGETの余分な記録」のような置換を成功扱いしない。`capture-audit.json`に欠落IDと余分なHAR行を記録する。
- Workerの初期スクリプトが302で別スクリプトへ移るケースを3回発生させ、独立したHTTP fixture／プロキシの実送信数と、補完後のジャーナル・HARを双方向に比較する。元URLの302と各リダイレクト先の200、最終スクリプトの本文がすべて保存されること。元のPlaywright HAR同士の一致だけでは合格にしない。
- Workerの開始URLと転送先URLに画面内フラグメントを付け、HTTP要求先にはフラグメントが入らないこと、クエリ中の`%23`と重複回数はそのまま保持すること。
- MCPのホバーで現れたボタン、DOM要素のドラッグ、画面座標のドラッグから、実際のPOSTがプロキシへ到達すること。任意コード実行を許可せず、OFF時にはこれらのツールを公開しない。
- 未実行操作の追加点検は入力・送信ONかつ実行時間内だけ行う。追加結果で前回の確認済み操作を失わず、page/operationの同じ項目を更新する。追加回数の上限到達を全件確認完了に置き換えない。
- 特殊操作の`manual_required`は手動一覧へ出力し、自動再試行のキューへ戻さない。データを再作成してIDが変わっても、同じ画面・操作の点検結果で以前の失敗を更新できること。手動対象を実行済みに数えない。
- 初回結果が確認済みだけでも、独立した通常UIの棚卸しを実行する。そこで初めて見つかった操作も追加点検の対象にする。認証切れ・データ不足によるblockedを、実際の403や明示的禁止と混同しない。
- 件数不足で次へが無効なケース等は、AIがunavailableと返しても未確認の通常操作として保持し、終了処理・ログアウトを許可しない。実際の権限拒否でUIがないケースと区別する。
- 通常操作が未確認の間は終了処理へ進まない。実Edge/MCPのクリックとGET取得によるログアウトがfixtureへ届かず、その後の通常更新要求は届くことを確認する。新しい実行でフェーズファイルが失われてもログアウトを許可しない。独立点検後に通常の未確認が解消した場合だけ、終了処理フェーズでログアウトを許可する。
- 結果の結合では絶対URLと相対パス、再作成されたデータID、クエリの違いで同じ画面の操作を重複させない。一括操作と個別画面の操作は別に扱う。
- HARは終了・検証後に確定し、保存途中の`.har.pending`を完成済み原本として取り込まない。終了時の猶予を超えた場合は明示的に保存未完了とする。
- ローカル原本の要求ヘッダー・本文、応答ヘッダー・本文と失敗・本文取得不能を記録する。リダイレクトの応答本文などブラウザが公開しない内容を取得済みとはしない。
- アップロード要求の原本にも生成済みファイルの実バイト列を残す。取得本文長とContent-Lengthが異なる場合は要求情報の取得エラーにする。
- 初期API応答を待ってからメニューが描画されるSPAでも、後から現れたリンクへ巡回する。待機は有界とし、描画待ちを完了・網羅の証明にしない。
- 認証済み画面のユーザー名・パスワード付き設定フォームをログイン画面と誤判定しない。ログアウトがリンクではなくPOSTフォームで表示される管理画面も認証済み表示として認識する。

## AIワークフロー

テスト用画面を実際の探索AIへ渡し、生成したMCP呼び出しと通信記録を照合する。入力・クリック・送信の呼び出し、GET以外の到達と応答・HAR保存を確認する。単にツール一覧に存在するだけでは合格にしない。実対象アプリのデータを変更する検証とは区別する。

入力・送信モードは代表ページだけで終了せず、`operationCoverage`に画面・操作・実通信・未到達理由を記録する。API定義の存在は実行の証拠ではなく、未実行操作を網羅済みと報告しない。

## 画面

案件設定に［AIによる入力・送信を許可する］が表示され、既定OFFであること、有効にして保存し同じ案件を開き直すとONとなること、別のOFF案件に切り替えるとOFFとなることを確認する。入力・送信モードとクローラのフォーム送信が独立していることを確認する。

## Paperless実機検証（2026-11-09）

利用者が合意した合格範囲は、実探索で発生した要求の欠落ゼロと、未実行機能の一覧化。全163API操作の実行とは区別する。
通常UIから属性管理138要求、文書操作561要求を保存し、各原本ジャーナルとHARのメソッド・URL・重複回数が全件一致した。両探索の更新系各29要求は本文もBurpと一致。
補助取得の記録経路を修正した後の実機確認では、robots.txt・sitemap.xmlを含む48要求が原本・HAR・Burpすべてで一致した。認証済みのメール設定画面もログインと誤判定しなかった。

応答本文取得不能・中断・終了イベント未解決は保存完了と混同しない。最終48要求の3リダイレクト応答本文は取得不能として残し、`capture-summary.complete=false`を維持した。
詳細な照合、修正途中の失敗、回避手順を使用した範囲、未実行操作は、このScopePilotフォルダー内の`temp/scopepilot-paperless/verification-final.txt`と`unexecuted-operations.txt`に保存。
最終コードの全テストは183合格・4スキップ（ZAP実機前提）・失敗0。`temp/scopepilot-paperless/tests-final/complete-final.trx`を参照。

## リダイレクト補完・操作点検の再検証（2026-11-09）

Chromium NetLogを独立した記録元として、Worker等でPlaywrightに現れなかった転送先を補完する。実機の主実行はBurpの対象709要求とWire709要求が双方向で一致し、GET以外も74/74件一致。23要求を補完した。送信前中断・未解決ブラウザ試行を実送信へ加算しない。

主実行は旧2分の終了待機に達したため、保存済みNetLogと元HARから別ディレクトリーに復旧して照合した。本文取得エラー2件・終了イベント未解決3件が残り、主実行の`capture-summary.complete=false`は維持する。終了猶予5分・補完一括書込へ修正後の別セッションは通常終了コード0、Wire216件、正規HAR/ジャーナル238件一致、本文エラー0・未解決0で保存完了した。別セッションは通常終了の検証であり、全UI網羅とは扱わない。

利用者の追加方針に従い、特殊操作用の専用自動化は追加しない。OTP・認証情報変更・印刷・標準操作で成立しない並べ替え等は`manual_required`で手順を残す。通常操作の未確認・失敗、権限拒否、利用不可も別途残す。UI全機能100%の実行は未証明。

全体回帰188合格・4スキップ・失敗0（`temp/coverage-fix/test-results/final-regression.trx`）。最終のフラグメント・座標・手動引継ぎ関連9合格・失敗0（同`fragment-regression.trx`）。詳細な照合、復旧条件、未実行一覧、配布ハッシュは`temp/coverage-fix/verification-final.txt`・`verification-final.json`・`manual-operations.txt`・`build-manifest.json`を参照する。

## 通常操作とログアウト順序の再検証（2026-11-09）

フェーズを分けた実機探索で、初回と追加点検2回を同じ認証済みブラウザで継続し、通常終了までログアウト要求0件を確認した。Native Burpのログアウト履歴も今回区間に追加なし。通常操作の個別/一括、詳細内の関連作成、検索、設定保存、削除/復元を点検した。共通158操作は初回80、独立点検135、追加点検142。前回の文書設定保存1項目を補正した99/158と比較し、142/158（89.9%）を確認した。条件付き・手動対象を分母から取り除いていない。

実機は初期のフェーズ分離版で開始し、後続パスへ通常削除と独立点検の補足を渡した。実機結果の件数不足がunavailableとして返ったため、最終判定をさらに修正した。最終配布アセンブリーへ実際の結果全体、および件数不足の6項目だけを渡し、時間上限ガードを省いた条件でも終了/ログアウトを許可しないことを確認した。実機の全工程が同一バイナリーだったとは扱わない。

最終全回帰は198合格・4スキップ・失敗0（`temp/logout-fix/test-results/final-complete-regression.trx`）。日本語の「403を再確認」も権限拒否として認識するケースを含む。初期の並行バッチで発生した認証fixture起動待ちタイムアウトは別記録として保持し、単独実行と対象を変えない全体直列実行の成功で切り分けた。

正規HAR/ジャーナル1224件が一致し、NetLog1205要求から26要求を補完した。403応答本文1件が取得不能で`capture-summary.complete=false`を維持。全通信のBurp双方向照合はこの回では未実施。TasksのDismiss allは検索条件を含まないall=trueを送信し、未確認一覧3777件→0件を確認した。操作の成功と実データへの影響を別に記録する。詳細・操作別証拠・残り16操作・手動一覧・版とハッシュは`temp/logout-fix/verification-final.txt`と`verification-final.json`を参照。

### 明示禁止修正の通常配備確認（2026-11-11）

ユーザーがScopePilotを保存して閉じた後、旧プロセスの終了を確認し、検証済み7ファイルをStart-ScopePilot.cmdが参照するbin/Release-review/net10.0-windowsへバックアップ付きで配備した。全269テストに合格したDLL・JSとのSHA-256一致を配備後と通常起動後に確認。通常ランチャーからプロジェクト選択ウィンドウが開くことを確認した。案件ファイルの内容は変更していない。配備の証拠はtemp/forbidden-actions-fix-20261111/deployment-manifest.json、結果はverification-report.txt参照。先のファイルロックによる配備待ちは解消済み。
