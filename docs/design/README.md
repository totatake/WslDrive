# サブシステム設計パック一覧 (`docs/design/`)

本ディレクトリには、大規模機能や主要サブシステムに関する複数段階の設計パック（`docs/design/<feature>/`）を配置します。

## 配置ルール

大規模サブシステムを新規設計・リファクタリングする際は、`docs/design/<feature>/` 配下に以下の構成でドキュメントを作成してください：

- `README.md` — 設計パック索引および 3 行要約
- `00-background.md` — 開発背景・ボトルネック・課題分析
- `01-architecture.md` — コンポーネント構成・データフロー図
- `02-detail-design.md` — データ構造・API 契約・状態遷移・エッジケース処理
- `03-roadmap.md` — 実装マイルストーン（CLI/環境変数仕様が中心の場合は `03-cli-and-env.md` + `04-roadmap.md`）
- `04-results.md` — 実装後の計測・検証結果

## 現在の設計パック

- *（現時点では全体アーキテクチャは [architecture.md](../architecture.md) と [adr/README.md](../adr/README.md) に集約されています。大規模サブシステムを追加する際にここへパックを追加します。）*
