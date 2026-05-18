# RepoGraph (v0.9.8.0)

RepoGraph は、生成 AI が巨大な C# / .NET リポジトリを扱いやすいように作った解析ツールチェーンです。
コードを AI 向けの構造データへ変換し、AI や人間が「どこが中心か」「どこが危険か」「どこで状態が共有されているか」「既存の近い実装は何か」を掴みやすくすることを主目的にしています。

## 何が分かるか
- `Probe` が Roslyn / MSBuild を使って `symbols`、`method_calls`、`field_accesses`、`type_dependency` などを抽出します。
- `Relay` がその出力を使って `hotspots.md`、`dead_code_candidates.md`、`dead_code_candidates.json`、RAG 用 index を生成します。
- 現時点で比較的強いのは `hotspots`、`thread hazard`、`shared mutable state`、`call/type/field graph` です。
- `hotspots` では raw の `fan-in/fan-out` に加えて、framework 由来の synthetic edge を少し割り引いた `effective fan-in/fan-out` も出します。
- `deadcode` では、除外された候補について `Suppressed Convention Patterns` を rule ID と family 単位で見える化します。
- 取り切れない `XAML` / framework callback については、生成 AI の読解結果を `ai_soft_edges.json` として別レイヤー保存できます。
- `show-deadcode --compare-ai-soft-edges` により、hard graph だけでは孤立に見える候補が AI soft edge でどれだけ抑えられるか比較できます。
- `ai-candidates --bundle-path ...` により、`xaml / reflection / di` の難所を外部 AI に渡しやすい JSON bundle として出力できます。

## 何がまだ弱いか
- `deadcode` は heuristic ベースで、補助的な候補列挙です。
- `related` は近い既存メソッド / クラスを探す補助機能で、project / file family / 構造の近さを見るには有効ですが万能ではありません。
- `reflection`、`DI`、`framework convention`、一部の `dispatch` はまだ完全ではありません。
- そのため、`deadcode` の結果は削除判断に直結させず、AI や人間の二次確認を前提にしてください。

## 構成
1. `roslyn-cli/Probe`
   C# 製の抽出器です。`.sln` / `.csproj` を解析して SQLite と graph JSON を出力します。
2. `python-rag`
   Python 製の分析器です。`hotspots`、`deadcode`、`related`、`query`、`build-index` を提供します。

## クイックスタート
環境パスの前提は [AI_INSTRUCTIONS.md](AI_INSTRUCTIONS.md) を参照してください。

1. 解析を実行します。
```powershell
$env:DOTNET_ROOT = "C:\tools\dotnet-sdk-8.0.421-win-x64"
& "C:\tools\dotnet-sdk-8.0.421-win-x64\dotnet.exe" run --project C:\tmp\RepoGraph\roslyn-cli\Probe\Probe.csproj -- scan <TARGET_SLN_OR_CSPROJ> --output C:\tmp\RepoGraph\analysis_workspace\<workspace_name>
```

2. ホットスポットを生成します。
```powershell
C:\tmp\RepoGraph\.venv\Scripts\python.exe C:\tmp\RepoGraph\python-rag\main.py hotspots --workspace C:\tmp\RepoGraph\analysis_workspace\<workspace_name>
```

3. 必要なら dead code 候補も生成します。
```powershell
C:\tmp\RepoGraph\.venv\Scripts\python.exe C:\tmp\RepoGraph\python-rag\main.py deadcode --workspace C:\tmp\RepoGraph\analysis_workspace\<workspace_name>
```

4. 近い既存実装を探したいなら `related` を使います。
```powershell
C:\tmp\RepoGraph\.venv\Scripts\python.exe C:\tmp\RepoGraph\python-rag\main.py related "<known FQN or symbol>" --workspace C:\tmp\RepoGraph\analysis_workspace\<workspace_name>
```

## まず使う軽量コマンド
`grep` に行く前に、まず RepoGraph の軽量コマンドで地図を確認する想定です。

```powershell
C:\tmp\RepoGraph\.venv\Scripts\python.exe C:\tmp\RepoGraph\python-rag\main.py files --workspace C:\tmp\RepoGraph\analysis_workspace\<workspace_name> --limit 30
C:\tmp\RepoGraph\.venv\Scripts\python.exe C:\tmp\RepoGraph\python-rag\main.py symbols --workspace C:\tmp\RepoGraph\analysis_workspace\<workspace_name> "<name or FQN>"
C:\tmp\RepoGraph\.venv\Scripts\python.exe C:\tmp\RepoGraph\python-rag\main.py show-hotspots --workspace C:\tmp\RepoGraph\analysis_workspace\<workspace_name> --limit 20
C:\tmp\RepoGraph\.venv\Scripts\python.exe C:\tmp\RepoGraph\python-rag\main.py show-deadcode --workspace C:\tmp\RepoGraph\analysis_workspace\<workspace_name> --limit 20
C:\tmp\RepoGraph\.venv\Scripts\python.exe C:\tmp\RepoGraph\python-rag\main.py show-deadcode --workspace C:\tmp\RepoGraph\analysis_workspace\<workspace_name> --compare-ai-soft-edges
C:\tmp\RepoGraph\.venv\Scripts\python.exe C:\tmp\RepoGraph\python-rag\main.py graph-meta --workspace C:\tmp\RepoGraph\analysis_workspace\<workspace_name>
C:\tmp\RepoGraph\.venv\Scripts\python.exe C:\tmp\RepoGraph\python-rag\main.py xaml-candidates --workspace C:\tmp\RepoGraph\analysis_workspace\<workspace_name> --limit 20
C:\tmp\RepoGraph\.venv\Scripts\python.exe C:\tmp\RepoGraph\python-rag\main.py ai-candidates --workspace C:\tmp\RepoGraph\analysis_workspace\<workspace_name> --kind all --limit 20
C:\tmp\RepoGraph\.venv\Scripts\python.exe C:\tmp\RepoGraph\python-rag\main.py ai-candidates --workspace C:\tmp\RepoGraph\analysis_workspace\<workspace_name> --kind reflection --bundle-path C:\tmp\candidate_bundle.json
C:\tmp\RepoGraph\.venv\Scripts\python.exe C:\tmp\RepoGraph\python-rag\main.py show-ai-edges --workspace C:\tmp\RepoGraph\analysis_workspace\<workspace_name> --limit 20
```

用途:
- `files`: 解析済みファイル一覧の確認
- `symbols`: 既知の型名・メソッド名から入口を探す
- `show-hotspots`: 重要箇所の上位だけ素早く把握する
- `show-deadcode`: 孤立候補の上位だけ素早く把握する
- `show-deadcode --compare-ai-soft-edges`: hard graph だけだと孤立に見える候補が、AI soft edge でどれだけ抑えられるか比較する
- `graph-meta`: scan mode や出力元の確認
- `xaml-candidates`: XAML / code-behind のうち、生成 AI に追加読解させる価値が高い箇所を絞る
- `ai-candidates`: `xaml / reflection / di` の難所をまとめて絞る
- `ai-candidates --bundle-path ...`: 外部 AI に渡しやすい prompt-ready JSON bundle を保存する
- `show-ai-edges`: 取り込んだ AI 補助 edge を確認する

## AI Soft Edge の流れ
1. `xaml-candidates` または `ai-candidates --kind xaml|reflection|di` で AI に読ませる候補を絞ります。
2. 生成 AI が `source`, `target`, `type`, `confidence`, `evidence` を持つ JSON を作ります。
3. RepoGraph に取り込みます。

```powershell
C:\tmp\RepoGraph\.venv\Scripts\python.exe C:\tmp\RepoGraph\python-rag\main.py import-ai-edges <soft_edges.json> --workspace C:\tmp\RepoGraph\analysis_workspace\<workspace_name>
```

4. 必要に応じて `deadcode` / `related` に opt-in で使います。

```powershell
C:\tmp\RepoGraph\.venv\Scripts\python.exe C:\tmp\RepoGraph\python-rag\main.py deadcode --workspace C:\tmp\RepoGraph\analysis_workspace\<workspace_name> --with-ai-soft-edges
C:\tmp\RepoGraph\.venv\Scripts\python.exe C:\tmp\RepoGraph\python-rag\main.py related "<known FQN or symbol>" --workspace C:\tmp\RepoGraph\analysis_workspace\<workspace_name> --with-ai-soft-edges
```

## 使いどころ
- 巨大なレガシーコードの初期把握
- 構造的に危険なクラスやメソッドの優先順位付け
- 共有可変状態や UI / background thread 混在箇所の抽出
- 既存の近い実装や類似ファミリーの探索
- AI に探索の足場を与えるための前処理

## 運用メモ
- AI にこのリポジトリを触らせるときは、最初に [AI_INSTRUCTIONS.md](AI_INSTRUCTIONS.md) を読ませてください。
- AI には、最初から `grep` で総当たりさせるより、まず `files` / `symbols` / `show-hotspots` を使わせる方が安定します。
- `XAML` や framework 規約で取り切れない箇所は、`xaml-candidates` で候補を絞ってから生成 AI に読ませる運用がしやすいです。
- `deadcode` は便利ですが主役ではありません。まずは `hotspots` と graph の精度を優先して使う想定です。
- `deadcode` は候補を無理に減らすより、「なぜ孤立して見えるか」を説明し、追加調査をしやすくする方向で使います。
- フレームワーク由来の除外は、`.NET host`、`XAML/UI`、`MVVM`、`ASP.NET`、`DI`、`serialization` などの rule ID に分けて管理しています。
- graph JSON の `graph.scan_mode` と `graph.solution_path` で、`full` / `incremental` のどちらで作られた成果物かを確認できます。
- `incremental` は使えますが、まずは `full` を基準に信頼し、差分運用では重要箇所を再確認する前提が安全です。

## License
[MIT License](LICENSE)
