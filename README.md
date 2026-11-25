# step_recorder

GUI アプリの操作を記録して、画像・動画つきの操作手順(HTML)を作る Windows ツール。

仕様は [docs/spec.md](docs/spec.md) を参照。

## インストール

[Releases](https://github.com/taaaaaaachu/step_recorder/releases) から `StepRecorder-win-Setup.exe` を実行する(管理者権限は不要)。新しいバージョンがあれば起動時に更新を確認する。
コード署名をしていないので、初回は SmartScreen の警告が出る(「詳細情報」→「実行」)。

インストールせずに使う場合は `StepRecorder-win-Portable.zip` を展開する。

## 使い方

1. 開始画面で記録する対象(モニターかアプリ)を選んで「記録開始」
2. 対象を操作する。画面上部のバーで手動撮影・録画・一時停止・停止ができる
3. 停止すると編集画面が開く。ステップの削除・並べ替え・説明文の修正などをして保存する
4. 渡すときは編集画面の「配布用に書き出す」を使う

## 開発

.NET 10 SDK が必要。

```sh
dotnet build
dotnet test                           # 単体テスト
STEP_RECORDER_DESKTOP=1 dotnet test   # 実デスクトップでの録画・表示(ウィンドウが開く。マウスとキーボードは動かさない)
STEP_RECORDER_E2E=1 dotnet test       # 上に加えて操作の E2E(数分間マウスとキーボードが動く)
```

リリースは `v0.1.0` 形式のタグを push すると GitHub Actions が Velopack でパッケージを作り、Releases に公開する。公開されたら、リリースノート(変更点の Markdown)を `gh release edit v0.1.0 --notes-file notes.md` で本文に書く。

## ライセンス

[MIT License](LICENSE)
