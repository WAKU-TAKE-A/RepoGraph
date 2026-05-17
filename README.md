# RepoGraph (v0.9.6.0)

RepoGraph は、生成 AI が巨大な C# / .NET リポジトリを扱いやすいように作った解析ツールチェーンです。
コードを AI 向けの構造データへ変換し、AI や人間が「どこが中心か」「どこが危険か」「どこで状態が共有されているか」「既存の近い実装は何か」を掴みやすくすることを主目的にしています。

## 何が分かるか
- `Probe` が Roslyn / MSBuild を使って `symbols`、`method_calls`、`field_accesses`、`type_dependency` などを抽出します。
- `Relay` がその出力を使って `hotspots.md`、`dead_code_candidates.md`、`dead_code_candidates.json`、RAG 用 index を生成します。
- 現時点で比較的強いのは `hotspots`、`thread hazard`、`shared mutable state`、`call/type/field graph` です。

## 何がまだ弱いか
- `deadcode` は heuristic ベースで、補助的な候補列挙です。
- `related` は近い既存メソッド / クラスを探す補助機能で、探索や横断修正には有効ですが万能ではありません。
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

## 使いどころ
- 巨大なレガシーコードの初期把握
- 構造的に危険なクラスやメソッドの優先順位付け
- 共有可変状態や UI / background thread 混在箇所の抽出
- 既存の近い実装や類似ファミリーの探索
- AI に探索の足場を与えるための前処理

## 運用メモ
- AI にこのリポジトリを触らせるときは、最初に [AI_INSTRUCTIONS.md](AI_INSTRUCTIONS.md) を読ませてください。
- `deadcode` は便利ですが主役ではありません。まずは `hotspots` と graph の精度を優先して使う想定です。
- `deadcode` は候補を無理に減らすより、「なぜ孤立して見えるか」を説明し、追加調査をしやすくする方向で使います。

## License
[MIT License](LICENSE)
