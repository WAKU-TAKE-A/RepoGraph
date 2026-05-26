# RepoGraph (v0.9.9.1)

RepoGraph は、巨大な C# / .NET リポジトリを生成 AI が探索しやすくするための解析ツールです。

基本原則:
RepoGraph は「答えを出すツール」ではなく、「AI が質問を立てるためのツール」です。
AI は自分でファイルを読めます。RepoGraph の役割は、巨大リポジトリで AI が迷子にならないための地図を提供することに限定します。

## 何をするツールか

- `Probe` が Roslyn / MSBuild で `.sln` / `.csproj` を解析し、確定的な構造データ（Hard graph）と、DSL ルールによる推測データ（Candidate edges）を出力します。
- `python-rag` がその出力を使い、`hotspots`、`isolation`、`related`、`ai-candidates` などの調査入口を作ります。
- `ai_soft_edges.json` を別レイヤーのオーバーレイとして扱い、XAML / reflection / DI のような hard graph で取り切れない難所を AI 補助で扱えます。

RepoGraph は、次のような用途に向いています。

- 巨大なレガシーコードの初期把握
- どこから読むべきかの優先順位付け
- shared mutable state や UI / background thread 混在箇所の抽出
- XAML / reflection / DI のような難所の候補化
- AI に raw `grep` 前の地図を渡すこと

## 通常利用時のファイル構成

通常利用では、次のような配置を想定します。

```text
RepoGraph/
  probe/
    Probe.exe
    Probe.dll
    Probe.runtimeconfig.json
    Probe.deps.json
      rules/
        dsl/
    BuildHost-net472/
    BuildHost-netcore/
    runtimes/
    *.dll

  python-rag/
    main.py
    *.py

  analysis_workspace/
  README.md
  AI_INSTRUCTIONS.md
  LICENSE
```

重要:

- `Probe.exe` 単体では足りません。`probe/` 配下の一式が必要です。
- 通常利用は `probe/Probe.exe` 配布レイアウトを前提にします。開発時だけ `roslyn-cli/Probe` と `dotnet build/run` を使います。
- `analysis_workspace/` は解析結果の出力先です。
- `README.md` は人間向け、`AI_INSTRUCTIONS.md` は通常利用時に AI へ見せるための文書です。
- RepoGraph 本体を改造する場合は [AI_INSTRUCTIONS_dev.md](AI_INSTRUCTIONS_dev.md) を参照してください。

## 必要環境

- Python 実行環境
- `probe/` 配下の配布物一式
- 解析対象プロジェクトを Roslyn / MSBuild で解決するための環境

最後の項目は対象リポジトリ次第です。現実には次のいずれかが必要になることがあります。

- .NET SDK
- Visual Studio Build Tools
- .NET Framework Developer Pack / targeting pack
- 対象ソリューション固有の workload

つまり、RepoGraph 本体は zip 配布しやすいですが、対象リポジトリを解析できるかは対象側のビルド環境にも依存します。

## クイックスタート

1. `Probe.exe` で構造データを作ります。

```powershell
.\probe\Probe.exe scan <TARGET_SLN_OR_CSPROJ> --output .\analysis_workspace\<workspace_name>
```

2. `hotspots` を作ります。

```powershell
python .\python-rag\main.py hotspots --workspace .\analysis_workspace\<workspace_name>
```

3. 必要なら `isolation` を作ります。

```powershell
python .\python-rag\main.py isolation --workspace .\analysis_workspace\<workspace_name>
```

4. 近い既存実装を探したいなら `related` を使います。

```powershell
python .\python-rag\main.py related "<known FQN or symbol>" --workspace .\analysis_workspace\<workspace_name>
```

## まず使う軽量コマンド

AI に最初から raw `grep` をさせるのではなく、まず RepoGraph の軽量コマンドで地図を確認する想定です。

```powershell
python .\python-rag\main.py files --workspace .\analysis_workspace\<workspace_name> --limit 30
python .\python-rag\main.py symbols --workspace .\analysis_workspace\<workspace_name> "<name or FQN>"
python .\python-rag\main.py show-hotspots --workspace .\analysis_workspace\<workspace_name> --limit 20
python .\python-rag\main.py show-isolation --workspace .\analysis_workspace\<workspace_name> --limit 20
python .\python-rag\main.py show-isolation --workspace .\analysis_workspace\<workspace_name> --compare-ai-soft-edges
python .\python-rag\main.py graph-meta --workspace .\analysis_workspace\<workspace_name>
python .\python-rag\main.py xaml-candidates --workspace .\analysis_workspace\<workspace_name> --limit 20
python .\python-rag\main.py ai-candidates --workspace .\analysis_workspace\<workspace_name> --kind all --limit 20
python .\python-rag\main.py show-ai-edges --workspace .\analysis_workspace\<workspace_name> --limit 20 --json
```

## AI soft edge の流れ

1. `xaml-candidates` または `ai-candidates --kind xaml|reflection|di` で候補を絞ります。
2. `ai-candidates --bundle-path ...` で AI に渡す JSON bundle を作ります。
3. AI が `ai_soft_edges.json` を返します。
4. `import-ai-edges` で取り込みます。
5. `show-ai-edges --json` で品質を確認します。
6. 必要に応じて `isolation` / `related` に opt-in で重ねます。

## 通常利用時の注意

- `isolation` は最終判定ではなく、調査入口です。
- `hotspots` や graph の方が主役です。
- `reflection`、`DI`、`framework convention`、`dispatch` はまだ完全ではありません。
- `ai_soft_edges.json` は hard graph に混ぜず、別レイヤーとして扱う前提です。

## AI に使わせる場合

通常利用では、人間はまずこの `README.md` を読み、AI には `README.md` と [AI_INSTRUCTIONS.md](AI_INSTRUCTIONS.md) を見せてください。

`AI_INSTRUCTIONS.md` は通常利用向けです。RepoGraph 本体を改造する AI には [AI_INSTRUCTIONS_dev.md](AI_INSTRUCTIONS_dev.md) を使ってください。

## License

[MIT License](LICENSE)
