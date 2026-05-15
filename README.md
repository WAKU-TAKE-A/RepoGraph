# RepoGraph (v0.9.2.0)

**RepoGraph** は、巨大な C# レガシーコードベースを、AIエージェント（Claude Code, Antigravity, Cursor 等）が自律的に探索・分析・リファクタリング可能にするための「AIエージェント専用ツールチェーン」です。

## 🤖 AIエージェントとの共同作業に特化
このツールは、人間が読むためだけではなく、**生成AIが「コードの実行時コンテキスト」を正しく把握するための「サイバー・アイ（電子の眼）」**として設計されています。

### AIエージェントへの導入手順
AIエージェント（例：Claude Code, Antigravity）を使ってこのリポジトリを解析・改善させる際は、まず以下の手順を踏んでください。

1.  **指示書の読み込み**:
    AIエージェントに対し、最初に `docs/AI_INSTRUCTIONS.md` を読むように指示してください。
    > 「`docs/AI_INSTRUCTIONS.md` を読み、その指示に従って RepoGraph のツール類を使い、このリポジトリを解析してください。」

2.  **ツールの自律実行**:
    `AI_INSTRUCTIONS.md` には、AIが自身で `Probe`（解析）や `Relay`（分析）を実行するための環境変数やコマンド、そして「ホットスポットの見極め方」が定義されています。

3.  **証拠に基づく改善要求**:
    AIは解析結果（`hotspots.md` や `field_access_graph.json`）を元に、「どのクラスのどのフィールドをカプセル化すべきか」といった、**エビデンスに基づいたリファクタリング案**を提示できるようになります。

## 🌟 主な機能
- **実行時コンテキスト抽出 (Probe)**: スレッド境界、フィールドアクセス、イベント購読の自動検知。
- **スパゲッティ分析**: 複数クラスから操作される「共有可変状態」を特定し、AIに警告。
- **AIエージェント専用指示書 (`docs/AI_INSTRUCTIONS.md`)**: エージェントが自らツールを使いこなし、リポジトリを攻略するためのマニュアル。

## 🏗️ 構成
1.  **roslyn-cli (Probe)**: C# 製。ソースコードからセマンティックグラフを抽出。
2.  **python-rag (Relay)**: Python 製。グラフ解析とAI向け分析レポートの生成。

## 🚀 クイックスタート (Human & AI)
1.  **Probe によるスキャン**: `dotnet run --project roslyn-cli/Probe/Probe.csproj -- scan <TARGET_PATH>`
2.  **Relay による分析**: `python python-rag/main.py hotspots`
3.  **AIへの丸投げ**: `AI_INSTRUCTIONS.md` を読ませ、あとはAIに「このリポジトリの改善点を見つけて修正して」と頼む。

## ⚖️ License
[MIT License](LICENSE)
