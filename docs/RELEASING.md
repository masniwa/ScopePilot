# Forkの公開手順

公開先は利用者がgsgwr/ScopePilotから作成したGitHub Forkを指定します。既存originへ誤ってpushしないよう、公開先を`fork`という別remoteで登録してください。URLや所有者名はこのソースには固定していません。

## ソースを登録する

既存のGit履歴を引き継ぐ場合は、この作業コピーの変更と未追跡の新規ソースを確認し、コミットしてください。`temp/`、案件、通信証拠、認証情報、`.cache/`、`bin/`、`obj/`、`artifacts/`をコミットしないでください。必要な新規Services・Tests・toolsも含めます。ソースZIPには公開用の現時点のソースを収録しますが、Git履歴は含みません。

以下の`YOUR_ACCOUNT`は実際のFork所有者へ置き換えます。この手順は公開操作です。

```powershell
git remote add fork https://github.com/YOUR_ACCOUNT/ScopePilot.git
git remote -v
git diff --check
git status --short
# 変更内容・新規ソース・秘密情報の除外を確認してコミットする
git add .
git commit -m "Release ScopePilot Fork 0.13.0"
git push fork HEAD:master
git tag v0.13.0
git push fork v0.13.0
```

この版は原版の3fe8748b3c9225c61b0d60c5ec9ad640ecf2fbacをベースとし、確認時のgsgwr/masterより後の上流8コミットを含みます。古いgsgwrのコミットへresetして変更を失わないようにしてください。公開先に独自変更がある場合は差分を確認して統合し、force pushしません。

## ビルドとRelease

.NET 10 SDK、Node.js 24、Inno Setup 6.7.3以降を用意します。`tools/playwright-runtime`で`npm ci`を実行します。`scripts/Build-Installer.ps1`でwin-x64 / win-arm64をそれぞれ生成できます。バージョンはScopePilot.csprojの0.13.0を使用します。操作の詳細はDISTRIBUTION.mdを参照してください。

GitHub ActionsのBuild ScopePilot Forkはmasterへのpush / PRと手動実行に対応し、両CPUのZIP・Setup.exe・SHA-256を成果物へ保存します。Releaseの自動公開は行いません。

Fork先でタグ`v0.13.0`のReleaseを作成し、表示名を`ScopePilot Fork 0.13.0`にします。初回はPre-releaseとして公開し、`docs/RELEASE-NOTES-0.13.0.md`を説明に使用することを推奨します。配布ファイルは次の組です。

- ScopePilot-Fork-0.13.0-win-x64.zip / .zip.sha256
- ScopePilot-Fork-0.13.0-win-x64-Setup.exe / .exe.sha256
- ScopePilot-Fork-0.13.0-win-arm64.zip / .zip.sha256
- ScopePilot-Fork-0.13.0-win-arm64-Setup.exe / .exe.sha256

案件・認証情報・通信履歴の混入がないこと、ライセンス同梱、版・CPUの一致を確認します。ARM64をx64環境で生成した場合、ビルドと実機起動の確認を区別してください。
