# step_recorder

GUI アプリの操作を記録して、企画チームに渡す「操作記録」(プレビュー用 HTML と後加工用の Markdown)を作る Windows ツール。Windows 標準の「ステップ記録ツール」を拡張したもの。

仕様は [docs/spec.md](docs/spec.md) を参照。

## インストール

[Releases](https://github.com/taaaaaaachu/step_recorder/releases) から `StepRecorder-win-Setup.exe` を実行する(管理者権限は不要)。起動時に新しい版があれば更新を確認する(開始画面の「自動更新」のチェックを外すと確認しない)。「今すぐ確認」ボタンでいつでも確認できる。
コード署名をしていないので、初回は SmartScreen の警告が出る(「詳細情報」→「実行」)。

インストールせずに使う場合は `StepRecorder-win-Portable.zip` を展開する。

## 使い方

1. step_recorder の開始画面で「対象」を選んで「記録開始」
   - 画面(モニター): 見えているものを丸ごと記録する。**他のアプリや通知も写り、どのアプリに入力した文字も記録に残る**(伏せるのはパスワード欄だけ)。選ぶとそのモニターに枠が出る
   - アプリ: 起動しておいたアプリのウィンドウだけを記録する。他のアプリは写らない。同じ exe のプロセスは 1 つのアプリとして記録する(タブごとにプロセスが分かれるサクラエディタなどでも、タブの切り替えを追う)
2. 対象を操作する。画面上部の常駐バー(普段は小さなタブに引っ込んでいて、マウスを乗せると出る。ピンで留めれば出たまま)で撮影モードの切替・手動撮影・一時停止・停止ができる
   - 既定のホットキー: 手動撮影 `Ctrl+Shift+F9` / 一時停止 `Ctrl+Shift+F10` / 停止 `Ctrl+Shift+F11` / 録画 `Ctrl+Shift+F12`
   - 「● 録画」を押してから操作し、もう一度押すと、その間の動画が1つのステップになる
   - 一時停止の間は動画も録らない(止めていた時間は動画に入らない)
   - 録画中に押したキーも記録し、動画に重ねて表示できる(編集画面で動画のステップごとに ON/OFF。配布用の書き出しでは焼き込む。パスワード欄の文字は●)
   - クリック(ボタンごとに色の違う輪)とホイール(向きの矢印)も同じように重ねて表示できる(キーとは別に ON/OFF)
   - 開始画面の撮影モードで「録画」を選ぶと全編を動画に録り(静止画はクリックごとに撮る)、編集画面で好きな時刻のフレームや区間をステップとして切り出せる
3. 停止すると編集画面が開く。不要なステップの削除・並べ替え・説明文や画面名の修正・強調枠の調整・トリミング・動画のステップの再生の速さ(HTML では見る人も変えられる)・キーとマウスの表示の指定をして「保存」
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
dotnet test                           # 単体テスト
STEP_RECORDER_DESKTOP=1 dotnet test   # 実デスクトップでの録画・表示(ウィンドウが開く。マウスとキーボードは動かさない)
STEP_RECORDER_E2E=1 dotnet test       # 上に加えて操作の E2E(数分間マウスとキーボードが動く)
```

### 動画の色がおかしい PC の診断

取り込み・RGB→YUV 変換(Windows の部品と GPU ドライバーを個別に)・エンコード・読み戻しを試し、どこで壊れるかを `report.txt` に書く。配布するアプリには含めない。

```sh
dotnet publish tools/ColorDiag -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o artifacts/ColorDiag
```

できた `StepRecorder.ColorDiag.exe` を対象の PC で実行する(インストール不要)。mp4 を exe にドロップすると、その動画が白潰れした状態で作られたものかも判定する。

リリースは `v0.1.0` 形式のタグを push すると GitHub Actions が Velopack でパッケージを作り、Releases に公開する。
