# step_recorder

GUI アプリの操作を記録して、企画チームに渡す「画面一覧」(プレビュー用 HTML と後加工用の Markdown)を作る Windows ツール。Windows 標準の「ステップ記録ツール」を拡張したもの。

仕様は [docs/spec.md](docs/spec.md) を参照。

## インストール

[Releases](https://github.com/taaaaaaachu/step_recorder/releases) から `StepRecorder-win-Setup.exe` を実行する(管理者権限は不要)。起動時に新しい版があれば更新を確認する(開始画面の「自動更新」のチェックを外すと確認しない)。
コード署名をしていないので、初回は SmartScreen の警告が出る(「詳細情報」→「実行」)。

インストールせずに使う場合は `StepRecorder-win-Portable.zip` を展開する。

## 使い方

1. 対象アプリを起動しておき、step_recorder の開始画面でそのアプリを選んで「記録開始」
2. 対象アプリを操作する。画面上部の常駐バーで撮影モードの切替・手動撮影・一時停止・停止ができる
   - 既定のホットキー: 手動撮影 `Ctrl+Shift+F9` / 一時停止 `Ctrl+Shift+F10` / 停止 `Ctrl+Shift+F11` / 録画 `Ctrl+Shift+F12`
   - 「● 録画」を押してから操作し、もう一度押すと、その間の動画が1つのステップになる
   - 開始画面で「録画モード」をオンにすると全編を動画に録り、編集画面で好きな時刻のフレームや区間をステップとして切り出せる
3. 停止すると編集画面が開く。不要なステップの削除・並べ替え・説明文や画面名の修正・強調枠の調整・トリミングをして「保存」
4. 渡すときは編集画面の「配布用に書き出す...」で、使っている画像と動画(mp4 か GIF)だけを別のフォルダに書き出す(あとから編集はできない)。出力フォルダごと渡してもよい

```
20261121-174100_App/
  session.json   正となるデータ(「記録を開く」でこれを開くと再編集できる)
  steps.md       後加工用の Markdown
  index.html     プレビュー用 HTML
  images/        元画像(加工しない。強調枠とトリミングは HTML 上で重ねて表示)
  videos/        録画ボタンで録った動画
  video.mp4      録画モードの全編の動画
```

`StepRecorder.exe <記録フォルダ>` で編集画面を直接開くこともできる。

## 開発

.NET 10 SDK が必要。

```sh
dotnet build
dotnet test                         # 単体テスト
STEP_RECORDER_E2E=1 dotnet test     # 実デスクトップでの E2E(数秒間マウスとキーボードが動く)
```

リリースは `v0.1.0` 形式のタグを push すると GitHub Actions が Velopack でパッケージを作り、Releases に公開する。
