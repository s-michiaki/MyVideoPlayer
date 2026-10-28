# MyVideoPlayer

自分用の Windows 動画プレイヤーです。フォルダ内の動画をサムネイル付きで一覧表示し、
埋め込みチャプターや説明文中のタイムスタンプからワンクリックでその場面へ飛べます。

- 技術: C# / WPF（.NET 10、Fluent ダークテーマ）+ LibVLC（VLC のエンジン）
- サムネイル・メタデータ取得: ffmpeg / ffprobe

## できること

**一覧画面**
- フォルダを選ぶ（ボタン / Ctrl+O / ドラッグ＆ドロップ）と、動画をサムネイル付きで一覧表示
- サブフォルダを含めるかの切り替え、ファイル名での絞り込み（スペース区切りで AND 検索）
- 並べ替え: 名前順（「第2話」<「第10話」の自然順）/ 新しい順 / 古い順 / サイズ / 長さ
- サムネイルに長さ・解像度・チャプター数・タイムスタンプ数のバッジ、途中まで見た位置のバー
- サムネイルの大きさ変更、最近開いたフォルダ、右クリックで「エクスプローラーで表示」「パスをコピー」
- 前回開いたフォルダを起動時に自動で開く

**再生画面**
- MP4 / MKV / WebM / MOV / AVI / TS など、VLC が再生できる形式はほぼすべて再生可能
- **音声ファイル（m4a / mp3 / wav）にも対応**: 一覧ではカバー画像（なければ音符アイコン）で表示し、再生中もカバー画像を表示。チャプター・説明文のタイムスタンプも同じように使えます（ポッドキャストや音声配信の録音など）
- 右側のパネルに
  - **チャプター**: MP4/MKV に埋め込まれたチャプター（開始時刻・タイトル・長さ）
  - **タイムスタンプ**: 説明文（description / synopsis / comment タグ）に書かれた
    `00:00 オープニング`、`[12:34] 本編`、`1:02:03 - まとめ` のような行
  - **説明文**: 説明文の全文
  - **情報**: 解像度・コーデック・作成日時など
- 一覧をクリックするとその位置へジャンプ。再生中の項目はハイライトされ、自動でスクロール
- シークバー上にチャプターの目盛り。バーにマウスを乗せると時刻とチャプター名を表示
- 続きから再生（途中で閉じた位置を記憶）、前後の動画へ移動、再生速度 0.25〜3 倍
- 全画面（操作バーは 2.5 秒で自動的に隠れる）

### キー操作

| キー | 動作 |
|---|---|
| Space / K | 再生・一時停止（映像のクリックでも可） |
| ← / → | 5 秒戻る / 進む（Shift で 30 秒） |
| J / L | 10 秒戻る / 進む |
| PageUp / PageDown | 前 / 次のチャプター |
| P / N | 前 / 次の動画 |
| ↑ / ↓ ・マウスホイール | 音量 |
| M | ミュート |
| [ / ] ・Backspace | 再生速度を下げる / 上げる ・ 等速に戻す |
| 0〜9 | 動画の 0%〜90% の位置へ |
| Home / End | 先頭 / 末尾へ |
| F / F11 ・映像をダブルクリック | 全画面 |
| C / I | サイドパネルの表示切り替え |
| Esc | 全画面を解除（全画面でなければプレイヤーを閉じる） |

## 動かし方

### すぐ使う（ビルド済み）
`MyVideoPlayer-win-x64.zip` を好きな場所に展開して `MyVideoPlayer.exe` を実行します。
.NET ランタイムと VLC は同梱済みなので、追加のインストールは不要です。

### ffmpeg を用意する（サムネイルとタイムスタンプに必要）
次のどれかで ffmpeg / ffprobe を置いてください。無くても再生とチャプター表示はできます。

1. `winget install Gyan.FFmpeg`（PATH が通るので一番簡単。インストール後にアプリを再起動）
2. https://www.gyan.dev/ffmpeg/builds/ の essentials 版を展開し、
   中の `bin` フォルダごと `MyVideoPlayer.exe` の隣に `ffmpeg\bin\` として置く
3. 任意の場所に置き、アプリ右上の「…」→「ffmpeg のフォルダを指定…」で選ぶ

### ソースからビルド
.NET 10 SDK が必要です。

```powershell
dotnet build -c Release                       # ビルドのみ
dotnet test tests/MyVideoPlayer.Core.Tests    # テスト（ffmpeg が PATH にあれば実ファイルでも確認）
./publish.ps1                                 # 配布用フォルダと zip を dist\ に作成
```

## データの保存場所

`%LOCALAPPDATA%\MyVideoPlayer\`
- `settings.json` … 最後に開いたフォルダ、音量、並べ替え、続きから再生の位置など
- `cache\` … サムネイル（.jpg）と ffprobe の結果（.json）。ファイルのサイズか更新日時が変わると作り直す
- `error.log` … 想定外のエラーが起きたときの記録

## 構成

```
src/MyVideoPlayer.Core   UI に依存しない処理（テスト可能）
  VideoScanner.cs          フォルダ内の動画を列挙
  MediaProbe.cs            ffprobe の JSON からチャプター・説明文・長さを読む
  TimestampParser.cs       説明文から「12:34 タイトル」を抽出
  MediaCache.cs            サムネイル作成と結果のキャッシュ
  AppSettings.cs           設定と再生位置の保存
src/MyVideoPlayer.App    WPF アプリ
  MainWindow               一覧画面
  PlayerWindow             再生画面（LibVLCSharp の VideoView）
  ViewModels/              画面の状態とロジック
tests/MyVideoPlayer.Core.Tests
```

## 補足・既知の制限

- チャプター: ffprobe が使えればそこから、使えない場合は再生開始後に VLC が認識したものを表示します。
- タイムスタンプの抽出は「行の中に `m:ss` / `h:mm:ss` がある行」を拾います。動画の長さを超える時刻は無視します。
- 映像の上に重ねている部分（メッセージ表示・クリック判定）は、VLC の映像ウィンドウの仕様上、別の透明ウィンドウとして描画しています。

## リリースの作り方

GitHub の **Actions** タブ → **build-and-release** → **Run workflow** でバージョン（例: `0.2.0`）を入れて実行すると、
Windows 上でテスト・ビルドし、タグ `v0.2.0` と Release を作って `MyVideoPlayer-win-x64.zip` を添付します。
リリースノートは `.github/release-notes.md` です。タグを push しても同じことができます。

```powershell
git tag v0.2.0
git push origin v0.2.0
```
