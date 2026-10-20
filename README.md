# Claude Usage Monitor

Claude のサブスクリプション（Pro / Max / Team など）の利用枠を、Windows のデスクトップに常駐するウィジェットで表示するアプリです。
Claude Code の `/usage` と同じ情報（Session (5hr)・Weekly (7 day)・モデル別上限の使用率とリセットまでの時間）を、ターミナルを開かずに確認できます。

```
┌────────────────────────────────────┐
│ ● Claude Usage            ⟳ ⌄ ⚙ ✕ │
│ ACCOUNT                            │
│ Email          user@example.com    │
│ USAGE                              │
│ Session (5hr)                 30%  │
│ ██████░░░░░░░░░░░░░░░░░░░░░░░░░░░  │
│ Resets in 2h                       │
│ Weekly (7 day)                 7%  │
│ █░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░  │
│ Resets in 6d                       │
│ Fable limit                    0%  │
│ ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░  │
│ Resets in 6d                       │
│                         更新 17:53 │
└────────────────────────────────────┘
```

> Anthropic 非公式のツールです。Claude Code の `/usage` の出力を解析しているため、Claude Code の更新で表示形式が変わると、正しく読み取れなくなる場合があります（その場合は取得したテキストをそのまま表示します）。

## 主な機能

- **利用枠の表示**：利用枠ごとに使用率・バー・リセットまでの時間を表示します。しきい値（既定 70% / 90%）を超えるとバーが黄・赤になります。
- **詳細表示**：ウィジェットをクリックすると、ローカルログから集計したトークン量を表示します（5時間ブロック、今日・今週・今月、モデル別、プロジェクト別の上位5件、`/usage` の利用傾向）。
- **タスクトレイ常駐**：アイコンに Session (5hr) の使用率を数字で表示します。左クリックでウィジェットの表示・非表示を切り替えます。
- **通知**：しきい値を超えたときに Windows の通知を出します（同じ周期では1回だけ）。リセット時の通知も設定できます。
- **システム情報（オプション）**：設定で項目ごとに有効にすると、PC と WSL2 の CPU・メモリ・ストレージの使用状況も表示します。WSL2 のメモリは VM 内の使用量 / 上限に加えて、Windows 上で VM が使っている量も表示します。80% で黄、90% で赤になります。WSL が停止中のときは起動しません。
- **Windows と WSL の両対応**：Windows に Claude Code が無い場合は WSL 側の Claude Code から取得します。ログは Windows・WSL の両方を合算して集計します。
- **ウィジェットの操作**：ドラッグで移動、画面端に吸着、常に最前面・通常・最背面の切り替え、不透明度の調整、クリック透過、ライト・ダークテーマ。

## 動作環境

- Windows 10 22H2 以降 / Windows 11（x64・ARM64）
- [Claude Code](https://docs.claude.com/en/docs/claude-code/overview) がインストールされ、サブスクリプションでログイン済みであること
  - Windows 側・WSL 側のどちらか一方にあれば動作します
- Claude API（従量課金）の利用量・コストの表示には対応していません

## インストール

[Releases](../../releases) から、インストーラー版かポータブル版（zip）をダウンロードします。どちらも .NET ランタイムを同梱しているため、別途インストールは不要です。

| 形式 | 通常の PC（x64） | ARM 版 Windows |
|---|---|---|
| インストーラー | `ClaudeUsageMonitor-<バージョン>-win-x64-setup.exe` | `ClaudeUsageMonitor-<バージョン>-win-arm64-setup.exe` |
| ポータブル（zip） | `ClaudeUsageMonitor-<バージョン>-win-x64.zip` | `ClaudeUsageMonitor-<バージョン>-win-arm64.zip` |

### インストーラー版

1. `...-setup.exe` を実行します。管理者権限は不要で、`%LOCALAPPDATA%\Programs\ClaudeUsageMonitor` にインストールされます。
2. 途中で「デスクトップにアイコンを作成する」「Windows の起動時に自動起動する」を選べます。
3. スタートメニューに「Claude Usage Monitor」が追加されます。

新しいバージョンのインストーラーを実行すると、そのまま上書きで更新されます（起動中の場合は終了を促されます）。アンインストールは Windows の「設定」→「アプリ」から行えます。

### ポータブル版

zip を好きな場所に展開し、`ClaudeUsageMonitor.exe` を実行します。Windows の起動時に自動で起動したい場合は、設定画面で「Windows の起動時に自動起動する」をオンにしてください。

### 共通

起動すると画面右下にウィジェットが表示され、タスクトレイにアイコンが出ます。

コード署名をしていないため、初回の実行時に「Windows によって PC が保護されました」と表示されることがあります。その場合は「詳細情報」→「実行」を選んでください。

## 使い方

| 操作 | 動作 |
|---|---|
| ウィジェットをクリック／ `⌄` ボタン | 詳細表示の切り替え |
| ウィジェットをドラッグ | 移動（画面端に近づけると吸着） |
| `⟳` ボタン | 今すぐ更新 |
| `⚙` ボタン | 設定 |
| `✕` ボタン | ウィジェットを隠す（トレイアイコンから再表示） |
| トレイアイコンを左クリック | ウィジェットの表示・非表示 |
| トレイアイコンを右クリック | メニュー（詳細表示、クリック透過、更新、設定、ログフォルダ、終了） |

クリック透過を有効にするとウィジェットを操作できなくなります。解除はトレイアイコンのメニューから行ってください。

## 仕組み

### 利用枠の取得

インストール済みの Claude Code を、画面を表示せずに次のコマンドで実行し、結果を解析します（既定は3分ごと。Claude Code の利用を検知したときにも更新します）。

```
claude -p "/usage" --output-format json --no-session-persistence
```

- `/usage` は Claude Code 内部で処理されるコマンドのため、モデルは呼び出されず、利用枠も消費しません。
- `--no-session-persistence` を付けているため、取得のたびにセッションログが増えることはありません。
- アカウント情報は `claude auth status` から取得します。
- 取得元は Windows → WSL の順に試します。WSL では `wsl.exe -d <ディストリビューション> --cd ~ --exec bash -lc '...'` の形で実行します。

### ローカルログの集計（詳細表示）

Claude Code のセッションログ（`~/.claude/projects/**/*.jsonl`）を差分で読み込み、トークン量を集計します。

- Windows：`%USERPROFILE%\.claude\projects`（`CLAUDE_CONFIG_DIR` があればそちら）
- WSL：`\\wsl.localhost\<ディストリビューション>\<HOME>\.claude\projects`（起動中のディストリビューションのみ。設定で停止中のものも起動して読み込めます）

### プライバシー

- ログからは集計に必要なメタデータ（日時、モデル、トークン数、作業ディレクトリなど）だけを読み取り、会話の本文は保存しません。
- Claude の認証情報ファイルには一切アクセスしません。
- アプリ自身は外部と通信しません（通信するのは Claude Code CLI だけです）。

### データの保存場所

| データ | 場所 |
|---|---|
| 設定 | `%APPDATA%\ClaudeUsageMonitor\settings.json` |
| 集計 DB | `%LOCALAPPDATA%\ClaudeUsageMonitor\usage.db` |
| 最後に取得した利用枠 | `%LOCALAPPDATA%\ClaudeUsageMonitor\ratelimit.json` |
| アプリのログ（7日で削除） | `%LOCALAPPDATA%\ClaudeUsageMonitor\logs\` |

アンインストールしても、上の設定・データは残ります。不要な場合は手動で削除してください。ポータブル版で自動起動をオンにしていた場合は、exe を削除する前に設定画面でオフにしてください。

## 開発

### 必要なもの

- .NET 10 SDK
- Windows（WPF アプリのため）

### ビルドと実行

```powershell
dotnet build ClaudeUsageMonitor.sln
dotnet run --project src/ClaudeUsageMonitor
dotnet test ClaudeUsageMonitor.sln
```

ビルド結果（bin / obj）は `Directory.Build.props` の設定で `%LOCALAPPDATA%\ClaudeUsageMonitor.build` に出力されます。リポジトリを Google ドライブなどの同期フォルダに置いた場合に、ビルド中の生成ファイルが見えなくなる問題を避けるためです。

インストール済みの Claude Code を実際に実行するテストは、環境変数を設定したときだけ動きます。

```powershell
$env:CUM_INTEGRATION = "1"; dotnet test ClaudeUsageMonitor.sln --filter "FullyQualifiedName~Integration"
```

### 配布用の exe を作る

```powershell
dotnet publish src/ClaudeUsageMonitor/ClaudeUsageMonitor.csproj -c Release -r win-x64 -o publish/win-x64
```

.NET ランタイムを同梱した単一の exe が出力されます。

インストーラーは [Inno Setup 6](https://jrsoftware.org/isinfo.php) で作成します（定義は `installer/ClaudeUsageMonitor.iss`）。上の publish のあとに実行すると、`dist/` に出力されます。

```powershell
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" /DAppVersion=0.2.0 /DArch=x64 installer\ClaudeUsageMonitor.iss
```

アプリのアイコン（`src/ClaudeUsageMonitor/Assets/app.ico`）は `tools/make-icon.ps1` で生成しています。

### 構成

```
src/ClaudeUsageMonitor.Core     CLI の実行と解析、WSL 連携、ログ集計（SQLite）、メモリ取得、設定
src/ClaudeUsageMonitor          WPF のウィジェット、トレイアイコン、通知、設定画面
tests/ClaudeUsageMonitor.Tests  単体テストと結合テスト（xUnit）
installer/                      インストーラー定義（Inno Setup）
tools/                          アイコン生成スクリプト
docs/specification.md           仕様書
```

## リリース

`main` への push と Pull Request では、GitHub Actions でビルドとテストを実行します（`.github/workflows/ci.yml`）。

`v` で始まるタグを push すると、テスト・ビルド・GitHub Release の作成までを自動で行います（`.github/workflows/release.yml`）。

```powershell
git tag v0.2.0
git push origin v0.2.0
```

- x64 と ARM64 それぞれのインストーラー（`-setup.exe`）と zip、および `SHA256SUMS.txt` が Release に添付されます。
- Actions の画面から Release ワークフローを手動で実行すると、Release を作らずに成果物だけを作れます（Artifacts からダウンロード）。
- リリースノートは前回のタグからの変更内容で自動生成されます。
- `v0.2.0-beta.1` のように `-` を含むタグはプレリリースになります。
- アプリのバージョンはタグから設定されます。

## ライセンス

[MIT License](LICENSE)
