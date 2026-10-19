# Claude Usage Monitor 仕様書

| 項目 | 内容 |
|---|---|
| 文書バージョン | 0.3（利用枠を Claude Code CLI から取得、WSL 対応、個人利用に確定） |
| 作成日 | 2026-10-19 |
| 対象OS | Windows 10 (22H2) / Windows 11 |
| ステータス | レビュー待ち |

---

## 1. 概要

### 1.1 目的
Claude サブスクリプション（Pro / Max / Team 等）の利用状況（利用枠の使用率・リセット時刻・トークン消費ペース）を、デスクトップ上に常駐する小型ウィジェットでリアルタイムに可視化する Windows 専用アプリケーションを開発する。

### 1.2 背景・課題
- Claude Code の利用量はターミナル上のコマンドでしか確認できず、作業中に常時把握しにくい。
- 5時間ごとの利用枠や週次枠、モデル別上限を意識せずに使うと、想定外に上限へ到達する。

### 1.3 スコープ
| 区分 | 内容 |
|---|---|
| 対象内 | **プラン利用枠（Session 5hr / Weekly 7day / モデル別上限）の使用率とリセット時刻の表示（主機能）**、Claude Code のローカル利用ログの集計・表示、しきい値通知 |
| 対象外 | Claude API（従量課金）の利用量・コスト（Admin API 連携、API 換算の推定コスト表示を含む） |
| 対象外（初期） | macOS/Linux 対応、複数ユーザーでの共有ダッシュボード |

---

## 2. 用語

| 用語 | 説明 |
|---|---|
| ウィジェット | デスクトップ上に常駐する枠なし・半透明の小型ウィンドウ |
| セッションログ | Claude Code が `~/.claude/projects/<プロジェクト>/<sessionId>.jsonl` に出力する会話ログ |
| 5時間ブロック | 最初のメッセージ時刻を起点とする5時間の利用枠単位（Claude のサブスクリプションプランの利用制限単位） |
| バーンレート | 直近の単位時間あたりのトークン消費量（tokens/分） |
| 利用枠 | サブスクリプションの利用制限単位。Session (5hr)・Weekly (7 day)・モデル別上限など |

---

## 3. 機能要件

### 3.1 機能一覧

| ID | 機能 | 優先度 | フェーズ |
|---|---|---|---|
| F-01 | Claude Code ローカルログの収集・集計 | 必須 | 1 |
| F-02 | ウィジェット表示（コンパクトモード） | 必須 | 1 |
| F-03 | ウィジェット表示（詳細モード） | 必須 | 1 |
| F-04 | タスクトレイ常駐 | 必須 | 1 |
| F-05 | 設定画面 | 必須 | 1 |
| F-06 | しきい値通知（トースト通知） | 高 | 2 |
| F-07 | 履歴グラフ（日次・週次・月次） | 高 | 2 |
| F-09 | CSV エクスポート | 低 | 3 |
| F-10 | プラン利用枠（Session 5hr / Weekly 7day / モデル別上限 の使用率%とリセット時刻）の表示 | **必須（主機能）** | 1 |
| F-11 | アカウント情報（メールアドレス・プラン）の表示 | 中 | 1 |

### 3.2 F-01 ローカルログの収集・集計

#### 入力
- 対象ディレクトリ：`%USERPROFILE%\.claude\projects\**\*.jsonl`
  - 環境変数 `CLAUDE_CONFIG_DIR` が設定されている場合はそちらを優先する。
  - Windows に Claude Code が無くこのディレクトリが存在しない場合は、エラーにせず WSL 側のログのみを集計する。
  - 設定画面で追加の監視ディレクトリを指定可能とする。
- **WSL 上の Claude Code のログも集計対象とする。**
  - 起動時に `wsl.exe -l -q`（出力は UTF-16LE）でディストリビューションを列挙し、`docker-desktop` 系を除外する。
  - 各ディストリビューションのホームを `wsl.exe -d <distro> -- printenv HOME` で取得し、`\\wsl.localhost\<distro>\<HOME>\.claude\projects` が存在するものを監視対象とする（例：`\\wsl.localhost\Ubuntu\home\<user>\.claude\projects`）。
  - WSL のパスでは `FileSystemWatcher` の変更通知が届かないことがあるため、60秒ごとのポーリングで更新を検知する。
  - ログ読み込みのためだけに停止中のディストリビューションを起動することはしない（設定で「必要に応じて起動する」を選択可能）。ただし利用枠の取得（§3.11）で起動された場合は、その時点でログも読み込む。
  - 設定画面でディストリビューションごとに監視の有効/無効を切り替えられる。
  - Windows と WSL のログは区別せず、同一の利用履歴として合算する（同一アカウントのため区別する意味がない）。
- 1行1JSONのレコードのうち、`message.usage` を持つもの（assistant 応答）を集計対象とする。

#### 使用するフィールド（実ログで確認済み）

| フィールド | 用途 |
|---|---|
| `timestamp` | 集計の時刻軸（UTC → ローカル時刻に変換） |
| `sessionId` | セッション単位の集計 |
| `cwd` | プロジェクト単位の集計 |
| `requestId` / `message.id` | 重複排除キー |
| `message.model` | モデル別集計・単価の決定 |
| `message.usage.input_tokens` | 入力トークン |
| `message.usage.output_tokens` | 出力トークン |
| `message.usage.cache_creation_input_tokens` | キャッシュ書き込みトークン |
| `message.usage.cache_creation.ephemeral_5m_input_tokens` / `ephemeral_1h_input_tokens` | キャッシュ書き込みの TTL 別内訳（単価が異なる） |
| `message.usage.cache_read_input_tokens` | キャッシュ読み込みトークン |
| `message.usage.server_tool_use.web_search_requests` | Web 検索回数 |
| `isSidechain` | サブエージェント由来かどうかの区別 |

> ログ形式は Claude Code のバージョンにより変化しうるため、パーサは未知フィールドを無視し、必須フィールド欠落時はその行をスキップしてログに警告を残す。

#### 処理仕様
1. 起動時に全ファイルを走査し、ファイルごとの読み込み済みバイトオフセットをローカルDBに保存する。
2. 以降は `FileSystemWatcher` で変更を検知し、オフセット以降の追記分のみを読み込む（差分読み込み）。監視漏れに備え、60秒ごとにポーリングで更新日時を確認する。
3. 同一 `message.id` + `requestId` の組は1件として扱う（ストリーミングで同一応答が複数行に記録されるため）。
4. 集計結果はローカル SQLite に保存し、再起動時の再集計を不要にする。
5. ファイルがロック中・書き込み途中（末尾行が不完全な JSON）の場合は、その行を次回読み込みに回す。

#### 集計単位
- 期間：現在の5時間ブロック／今日／今週／今月／任意期間
- 切り口：モデル別／プロジェクト（`cwd`）別／セッション別

#### 5時間ブロックの算出
- ブロック開始時刻＝ブロック外で最初に記録されたメッセージの `timestamp` を時単位で切り捨てた時刻。
- 終了時刻＝開始 + 5時間。終了後の最初のメッセージで新ブロックを開始する。
- 現在ブロックについて「経過時間」「残り時間」「ブロック内トークン合計」「現在のバーンレートから見た終了時点の予測量」を算出する。

### 3.3 F-02 ウィジェット（コンパクトモード）

デスクトップ上に常駐する小型ウィンドウ。既定サイズ 約 280×120 px（DPI スケーリング対応）。

Claude Code の `/usage` 表示と同等の情報を常時表示することを主目的とする。

```
┌────────────────────────────────────┐
│ ACCOUNT                            │
│ Email          user@example.co.jp  │
│ USAGE                              │
│ Session (5hr)                 22%  │
│ █████░░░░░░░░░░░░░░░░░░░░░░░░░░░░  │
│ Resets in 2h                       │
│ Weekly (7 day)                 5%  │
│ █░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░  │
│ Resets in 6d                       │
│ Fable limit                    0%  │
│ ░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░  │
│ Resets in 6d                       │
│                        更新 10:42 ⟳│
└────────────────────────────────────┘
```

表示項目：
- ACCOUNT：メールアドレス（設定で非表示可。画面共有時の配慮）
- USAGE：利用枠ごとに「名称・使用率%・プログレスバー・リセットまでの残り時間」
  - Session (5hr)：5時間枠
  - Weekly (7 day)：週次枠
  - モデル別上限（例：Fable limit）：データソースから返される枠を**動的に**列挙し、固定項目として実装しない（プランやモデル追加で項目が増減するため）
- プログレスバーの色：しきい値に応じて 通常 → 警告（黄）→ 危険（赤）
- 最終更新時刻と手動更新ボタン。データが古い（既定15分以上未更新）場合は灰色表示し「最終取得：xx分前」を明示する
- リセット時刻を過ぎた枠は、次回取得まで「リセット済み（0%見込み）」と表示する

### 3.4 F-03 ウィジェット（詳細モード）

ウィジェットのクリック（または設定でホバー）で展開表示する。既定サイズ 約 360×480 px。

- 現在ブロック：トークン内訳（入力／出力／キャッシュ書込／キャッシュ読込）、バーンレート、終了時予測
- 今日／今週／今月の合計トークン
- モデル別内訳（横棒グラフ）
- プロジェクト別上位5件
- 最終更新時刻

### 3.5 ウィジェットの振る舞い

| 項目 | 仕様 |
|---|---|
| ウィンドウ | 枠なし、角丸、背景の半透明（Windows 11 では Mica/Acrylic を使用、Windows 10 では単色半透明にフォールバック） |
| 最前面 | 「常に最前面」「デスクトップに固定（最背面）」「通常」から選択 |
| 移動 | ドラッグで移動。画面端へのスナップあり |
| 位置記憶 | 終了時の位置・モードを保存し、次回起動時に復元。マルチモニター構成の変化で画面外になった場合はプライマリモニターに戻す |
| 透明度 | 30〜100% で調整可能 |
| クリック透過 | 有効時はマウス操作を背面に透過（トレイメニューから解除可能） |
| タスクバー | タスクバーおよび Alt+Tab には表示しない |
| テーマ | ライト／ダーク／システム設定に追従 |
| 更新 | ログ変更検知時に即時反映。最低でも30秒ごとに表示（残り時間等）を再描画 |

### 3.6 F-04 タスクトレイ常駐
- トレイアイコンに Session (5hr) の使用率を反映（色またはバッジ）。
- ツールチップ：各利用枠の使用率とリセットまでの残り時間。
- 右クリックメニュー：ウィジェット表示/非表示、コンパクト/詳細切替、クリック透過の解除、今すぐ更新（利用枠の取得＋ログ再集計）、設定、終了。
- ウィジェットを閉じてもアプリはトレイに常駐する。

### 3.7 F-05 設定画面

| 設定項目 | 既定値 |
|---|---|
| Windows 起動時に自動起動 | オフ |
| Claude Code 実行ファイルのパス | 自動検出 |
| 利用枠の取得元 | 自動（Windows → WSL の順に試行）／ Windows のみ ／ WSL のみ |
| 利用枠の取得間隔 | 3分 |
| 監視ディレクトリ（複数可） | `%USERPROFILE%\.claude` |
| WSL ディストリビューションごとの監視 | `.claude/projects` があるものを有効 |
| ログ読み込みのために停止中の WSL ディストリビューションを起動する | オフ（利用枠の取得では設定に関わらず起動する） |
| 警告しきい値／危険しきい値 | 70% / 90% |
| メールアドレスの表示 | オン |
| テーマ・透明度・最前面設定 | システム / 90% / 常に最前面 |

設定は `%APPDATA%\ClaudeUsageMonitor\settings.json` に保存する。

### 3.8 F-06 しきい値通知
- いずれかの利用枠の使用率が警告／危険しきい値を超えたとき、Windows トースト通知を出す。
- 同一の枠・同一リセット周期・同一しきい値の通知は1回のみ。
- 利用枠のリセット時の通知はオプション。

### 3.9 F-07 履歴グラフ
- 日次（直近30日）、週次（直近12週）、月次（直近12か月）の棒グラフ。
- モデル別の積み上げ表示切替。
- 別ウィンドウで表示する（ウィジェット本体は小型を維持）。

### 3.10 F-09 CSV エクスポート
- 指定期間の集計結果（日時／モデル／プロジェクト／各トークン数）を CSV 出力する（UTF-8 BOM付き、Excel で文字化けしないこと）。

### 3.11 F-10 プラン利用枠の表示（主機能）

サブスクリプションの利用枠（Session 5hr / Weekly 7day / モデル別上限）の**使用率%とリセット時刻**はサーバー側で計算される値であり、ローカルログからは正確に再現できない。そのため、**インストール済みの Claude Code CLI を子プロセスとして実行し、`/usage` コマンドの結果を取得する。**

#### 取得コマンド（動作確認済み：Claude Code 2.1.287）

```
claude -p "/usage" --output-format json --no-session-persistence
```

| 確認結果 | 内容 |
|---|---|
| 所要時間 | 約0.9秒 |
| モデル呼び出し | なし（`num_turns: 0`、トークン消費 0。`local_command: "usage"` としてローカル処理される） |
| セッションログ | `--no-session-persistence` 指定時は `~/.claude/projects` にログが残らない（未指定だと1回ごとに jsonl が1つ作られるため必須） |
| 認証 | Claude Code のログイン状態をそのまま使う。本アプリは認証情報に一切触れない |

出力例（JSON の `result` フィールド）：

```
You are currently using your subscription to power your Claude Code usage

Current session: 24% used · resets Oct 19, 8pm (Asia/Tokyo)
Current week (all models): 6% used · resets Oct 26, 8am (Asia/Tokyo)
Current week (Fable): 0% used · resets Oct 26, 8am (Asia/Tokyo)
```

#### 実行仕様
- 実行する Claude Code の決定方法は「取得元の決定（Windows / WSL）」に従う。
- コンソールウィンドウを表示しない（`CreateNoWindow`）。作業ディレクトリは `%LOCALAPPDATA%\ClaudeUsageMonitor\probe`。
- タイムアウト 30秒。同時に2つ以上実行しない。
- 取得タイミング：
  - 起動時
  - 定期取得：既定 3分間隔（設定で 1〜30分）
  - ローカルログに新しい応答が追記されたとき（直前の取得から60秒以上空いている場合のみ）
  - ウィジェットの更新ボタン・トレイメニューからの手動取得
- PC のスリープ復帰時・ネットワーク復帰時にも1回取得する。

#### 解析仕様
- JSON の `is_error` が `true`、または `result` が取得できない場合はエラー状態とする。
- `result` の各行を次の正規表現で解析する：
  `^(?<label>.+?):\s*(?<pct>\d+(?:\.\d+)?)% used\s*·\s*resets (?<reset>.+?) \((?<tz>[^)]+)\)$`
- 正規表現に一致しない行は無視する。`/usage` の出力には利用枠の後に「What's contributing to your limits usage?」以下の利用傾向（直近24時間・7日のリクエスト数、セッション数、コンテキストサイズの傾向、よく使うスキル・サブエージェント・MCP サーバー）が続く場合があるため、この部分は詳細モードの「利用傾向」欄に生テキストのまま表示する（任意表示）。
- 同じアカウントでも取得元によってリセット時刻の丸めが異なることがある（例：`8pm` と `7:59pm`）。表示は分単位とし、この差は許容する。
- リセット日時の文字列（例：`Oct 19, 8pm`、`Oct 19, 7:59pm`）には年が含まれないため、指定タイムゾーンで「現在以降で最も近い日時」として年を補う。
- 表示名の対応（既知のもののみ。未知のラベルは受け取った文字列をそのまま表示）：

| CLI のラベル | ウィジェット表示名 |
|---|---|
| `Current session` | Session (5hr) |
| `Current week (all models)` | Weekly (7 day) |
| `Current week (<モデル名>)` | `<モデル名>` limit |

- 1行も解析できなかった場合は、`result` の生テキストをそのまま詳細モードに表示し、アプリログに警告を残す（CLI の出力形式変更への備え）。
- 「subscription」を含む先頭行が無い、またはサブスクリプション以外で利用中と判断できる場合は「サブスクリプション未使用」と表示する。

#### アカウント情報（F-11）
- `claude auth status` の JSON 出力から `email`・`orgName`・`subscriptionType` を取得する（起動時と1時間ごと）。
- `loggedIn` が `false` の場合は「Claude Code にログインしてください」と表示する。

#### 表示ルール
- 枠の一覧（名称・使用率・リセット時刻）は取得結果をそのまま動的に表示する。
- リセット時刻は「Resets in 2h」「Resets in 6d」のように相対表示し、ツールチップで絶対日時を表示する。
- 取得に失敗し続けている間は、直近の成功値を灰色で表示し「最終取得：xx分前」を表示する。

#### 取得元の決定（Windows / WSL）

Windows に Claude Code がインストールされているとは限らない。Windows 側に無い場合は WSL 側の Claude Code から取得する。Windows 側に無いことはエラーではなく、通常の構成として扱う。

- Windows ネイティブと WSL で同一アカウントを使う前提とし、利用枠はアカウント単位のため**どちらか一方から取得できれば十分**とする（両方の値を合算・併記しない）。
- 起動時に次の順で利用可能な Claude Code を検出する：
  1. **Windows 側**：設定値 → `PATH` 上の `claude` → `%USERPROFILE%\.local\bin\claude.exe` の順に探す。
  2. **WSL 側**：§3.2 の方法で列挙したディストリビューションごとに `wsl.exe -d <distro> --cd ~ -- bash -lc 'command -v claude'` を実行し、`claude` があるものを候補とする（既定のディストリビューションを優先）。
- 取得順：
  1. Windows 側に `claude.exe` があれば、それを実行する。
  2. Windows 側に無い、またはログアウト中・エラーの場合、WSL 側の `claude` を次のコマンドで実行する（2026-10-19 に Ubuntu で動作確認済み）：
     `wsl.exe -d <distro> --cd ~ -- bash -lc 'claude -p "/usage" --output-format json --no-session-persistence'`
     - `bash -lc`：ログインシェルの `PATH` で `~/.local/bin/claude` を解決するため
     - `--cd ~`：アプリの作業ディレクトリが Google ドライブ等の WSL から参照できないパスの場合、`wsl: Failed to translate ...` の警告が出るため
     - 標準出力の最終行の JSON のみを解析対象とする
- WSL 側から取得する場合、ディストリビューションが停止中でも `wsl.exe -d` により起動して取得する（WSL が唯一の取得元となりうるため）。
- Windows・WSL のどちらにも Claude Code が無い場合のみ、ウィジェットに「Claude Code が見つかりません」と表示する。
- 検出結果は保持し、1時間ごと（および取得失敗時）に再検出する。Windows 側に後から Claude Code がインストールされた場合も再検出で切り替わる。
- どちらから取得しても同じ値が返るため、取得元は表示・記録しない（内部的には最後に成功した取得元を次回最初に試すだけとする）。
- アカウント情報（`auth status`）も同じ取得元から取得する（WSL の場合は `bash -lc 'claude auth status'`）。

---

## 4. 非機能要件

### 4.1 性能
| 項目 | 目標 |
|---|---|
| 常駐時メモリ使用量 | 100MB 以下 |
| アイドル時 CPU 使用率 | 1% 未満 |
| 起動〜ウィジェット表示 | 2秒以内（初回の全ログ走査はバックグラウンドで実施し、進捗を表示） |
| ログ追記〜表示反映 | 5秒以内 |
| 初回走査 | ログ合計 1GB を 60秒以内 |

### 4.2 信頼性
- ログファイルの破損行・未知形式があってもアプリが停止しないこと。
- 集計DBが破損した場合は自動で再構築する。
- 未処理例外はアプリログに記録し、ウィジェットにエラー状態を表示する。

### 4.3 セキュリティ・プライバシー
- セッションログには会話本文が含まれるが、**本アプリは `usage` 等の集計に必要なメタデータのみを読み取り、本文は保存・送信しない。**
- Claude の認証情報ファイル（`.credentials.json` 等）には一切アクセスしない。利用枠・アカウント情報はすべて Claude Code CLI 経由で取得する。
- 本アプリ自身は外部通信を行わない（通信は Claude Code CLI が行う）。テレメトリは送信しない。
- `claude` の実行時は `-p "/usage"` と `auth status` 以外のプロンプトを渡さない（意図せずモデル呼び出しや利用枠の消費が起きないようにするため）。

### 4.4 互換性
- Windows 10 22H2 以降 / Windows 11（x64、ARM64）。
- Claude Code CLI：2.1.287 で動作確認。`/usage` の出力形式が変わった場合は §3.11 の解析仕様に従い生テキスト表示で継続動作する。
- WSL 2（WSL のログ集計を使う場合）。
- 高DPI（100〜200%）・マルチモニター・異なる DPI のモニター間移動に対応。

### 4.5 配布・更新
- 個人利用のみ（ソースは GitHub の公開リポジトリで管理）。配布形式はポータブル版（self-contained 単一ファイル exe を zip 化、§6.1）とし、インストーラー・コード署名・自動更新は行わない。
- 管理者権限不要で動作すること。
- 自動起動はスタートアップ用レジストリ（`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`）への登録で実現する。

---

## 5. システム構成

### 5.1 技術スタック（案）

| 区分 | 採用技術 | 理由 |
|---|---|---|
| 言語/ランタイム | C# / .NET 10（LTS） | Windows ネイティブ API との親和性、単一ファイル発行が可能 |
| UI | WPF | 枠なし・透過ウィンドウ、クリック透過、トレイ常駐の実装実績が豊富 |
| トレイアイコン | System.Windows.Forms.NotifyIcon | 追加パッケージ不要。アイコンは使用率を描画して動的に生成 |
| ローカルDB | SQLite（Microsoft.Data.Sqlite） | 軽量・単一ファイル |
| グラフ（F-07） | 未定（LiveCharts2 または ScottPlot） | WPF 対応 |
| 通知 | NotifyIcon のバルーン通知 | Windows 10/11 ではトースト通知として表示される。追加パッケージ不要 |
| MVVM | 自前の最小実装（INotifyPropertyChanged） | 画面が少ないためライブラリを使わない |
| テスト | xUnit | — |
| CI / リリース | GitHub Actions | §6.1 参照 |

> UI は WPF で確定。Windows 11 の「ウィジェットボード」への登録は Widget Provider の実装と MSIX 配布が必須となるため対象外とする。

### 5.2 コンポーネント構成

```
┌──────────────────────── ClaudeUsageMonitor.exe ────────────────────────┐
│                                                                          │
│  [UI層]  WidgetWindow / DetailView / HistoryWindow / SettingsWindow      │
│          TrayIcon / ToastNotifier                                        │
│               ▲  (ViewModel / データバインディング)                      │
│  [アプリ層] RateLimitService ── UsageService ── BlockCalculator          │
│               ▲                    ▲                                     │
│  [データ層] ClaudeCliClient        LogCollector ── LogParser             │
│             ├ /usage 取得・解析    ├ Windows: FileSystemWatcher+差分読込 │
│             └ auth status 取得     └ WSL: WslLocator+ポーリング          │
│                  │                 UsageRepository (SQLite)              │
│                  ▼                 SettingsRepository                    │
│           claude.exe（子プロセス）                                       │
└──────────────────────────────────────────────────────────────────────────┘
```

### 5.3 データ保存場所

| データ | パス |
|---|---|
| 設定 | `%APPDATA%\ClaudeUsageMonitor\settings.json` |
| 利用枠の最終取得結果（キャッシュ） | `%LOCALAPPDATA%\ClaudeUsageMonitor\ratelimit.json` |
| `claude` 実行時の作業ディレクトリ | `%LOCALAPPDATA%\ClaudeUsageMonitor\probe\` |
| 集計DB | `%LOCALAPPDATA%\ClaudeUsageMonitor\usage.db` |
| アプリログ | `%LOCALAPPDATA%\ClaudeUsageMonitor\logs\` （7日ローテーション） |

### 5.4 DB スキーマ（案）

```sql
-- 読み込み位置管理
CREATE TABLE source_files (
  path          TEXT PRIMARY KEY,
  offset        INTEGER NOT NULL,
  last_modified TEXT NOT NULL
);

-- 1応答 = 1行
CREATE TABLE usage_records (
  message_id            TEXT NOT NULL,
  request_id            TEXT NOT NULL,
  timestamp_utc         TEXT NOT NULL,
  session_id            TEXT,
  project_path          TEXT,
  model                 TEXT,
  is_sidechain          INTEGER,
  input_tokens          INTEGER,
  output_tokens         INTEGER,
  cache_write_5m_tokens INTEGER,
  cache_write_1h_tokens INTEGER,
  cache_read_tokens     INTEGER,
  web_search_requests   INTEGER,
  PRIMARY KEY (message_id, request_id)
);
CREATE INDEX idx_usage_ts ON usage_records(timestamp_utc);
```

### 5.5 ratelimit.json 形式（案）

起動直後に前回値を即表示するためのキャッシュ。CLI の解析結果を以下の形式で保存する。

```json
{
  "version": 1,
  "fetched_at": "2026-10-19T01:42:00Z",
  "cli_version": "2.1.287",
  "account": { "email": "user@example.co.jp", "org_name": "Example Inc", "subscription_type": "team" },
  "limits": [
    { "raw_label": "Current session",           "label": "Session (5hr)",  "used_percent": 24, "resets_at": "2026-10-19T11:00:00Z" },
    { "raw_label": "Current week (all models)", "label": "Weekly (7 day)", "used_percent": 6,  "resets_at": "2026-10-25T23:00:00Z" },
    { "raw_label": "Current week (Fable)",      "label": "Fable limit",    "used_percent": 0,  "resets_at": "2026-10-25T23:00:00Z" }
  ],
  "raw_result": "You are currently using your subscription ..."
}
```

---

## 6. 開発フェーズ

| フェーズ | 内容 | 成果物 |
|---|---|---|
| 1（MVP） | F-10、F-11、F-02〜F-05：CLI 経由の利用枠表示、ウィジェット（コンパクト/詳細）、トレイ、設定 | 動作するポータブル版 |
| 2 | F-01、F-06：ローカルログ集計（Windows＋WSL）、しきい値通知 | 同上 |
| 3 | F-07、F-09：履歴グラフ、CSV エクスポート | 同上 |

※ 利用枠の取得方法（§3.11）は 2026-10-19 に Windows・WSL（Ubuntu）の両方で実機検証済みのため、検証フェーズは不要となった。

### 6.1 ビルドとリリース

| 契機 | 処理 | 定義 |
|---|---|---|
| `main` への push、Pull Request | ビルドと単体テスト | `.github/workflows/ci.yml` |
| `vX.Y.Z` 形式のタグの push | 単体テスト → win-x64 / win-arm64 の単一 exe を発行 → zip と `SHA256SUMS.txt` を添付して GitHub Release を作成（リリースノートは自動生成） | `.github/workflows/release.yml` |

- アプリのバージョンはタグから設定する（`-p:Version=X.Y.Z`）。
- `-` を含むタグ（例：`v0.2.0-beta.1`）はプレリリースとする。
- 結合テスト（実際の Claude Code を実行するもの）は CI では実行しない（環境変数 `CUM_INTEGRATION=1` のときのみ実行）。

---

## 7. 未決事項・確認事項

| No | 内容 | 決定 |
|---|---|---|
| Q-01 | 対象とする利用形態 | サブスクリプションのみ（API は対象外） |
| Q-02 | Admin API 連携 | 不要 |
| Q-03 | UI フレームワーク | WPF |
| Q-04 | 表示通貨 | コスト表示を行わないため不要 |
| Q-05 | WSL 上の Claude Code の利用 | 集計対象とする |
| Q-06 | 配布範囲 | 個人利用のみ |
| Q-07 | 利用枠の取得方法 | インストール済み Claude Code CLI の `/usage` を実行して取得 |

現時点で未決事項はない。
