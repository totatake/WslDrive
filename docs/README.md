# WslDrive ドキュメント一覧

本ディレクトリは **WslDrive** の開発背景、要件定義、システムアーキテクチャ、設計判断記録（ADR）、利用ガイド、および検証仕様を集約する技術ドキュメントハブです。

## 推奨される読む順序

1. [overview.md](overview.md) — 開発背景・既存アプローチとの比較・技術的な柱・ユースケース
2. [requirements.md](requirements.md) — 機能要件（`REQ-F-*`）および非機能要件（`REQ-NF-*`）
3. [architecture.md](architecture.md) — システム全体構成、コンポーネント責務、データフロー、バイナリ解析仕様
4. [adr/README.md](adr/README.md) — Architecture Decision Records（設計判断と採択理由）
5. [guides/usage.md](guides/usage.md) — ビルド・発行・CLI / GUI の利用手順
6. [guides/troubleshooting.md](guides/troubleshooting.md) — パーミッション・NBD制約・エラー対処
7. [testing.md](testing.md) — 自動単体テスト、トレーサビリティ、Windows 11 実機結合テスト手順
8. [roadmap.md](roadmap.md) — 完了済みマイルストーンと今後の計画

## 基本ドキュメント

| ドキュメント | 内容 |
| :--- | :--- |
| [overview.md](overview.md) | 開発背景・3案比較（DiscUtils+Dokan / WSL2 / libext2fs）・採用理由・ユースケース |
| [requirements.md](requirements.md) | 機能要件・非機能要件（`REQ-F-*` / `REQ-NF-*`） |
| [architecture.md](architecture.md) | ソリューション構成、Mermaid 図、スーパーブロック解析仕様、境界設計 |
| [testing.md](testing.md) | テスト方針、xUnit 単体テスト、要件トレーサビリティ、結合テスト手順 |
| [roadmap.md](roadmap.md) | 完了済みマイルストーン・今後の開発計画 |

## サブディレクトリ

| ディレクトリ | 用途 |
| :--- | :--- |
| [guides/usage.md](guides/usage.md) | ユーザー向け導入・ビルド・CLI / GUI 利用ガイド |
| [guides/troubleshooting.md](guides/troubleshooting.md) | パーミッション制限、NBD ブリッジ制約、WSL2 状態不整合の対処 |
| [adr/README.md](adr/README.md) | Architecture Decision Records（`ADR-0001`〜`ADR-0005`） |
| [design/README.md](design/README.md) | サブシステム・大型機能のライフサイクル設計パック（`00-`〜`04-`） |
| [reports/README.md](reports/README.md) | 定量評価・トレース解析・リリース前検証レポート |
| `assets/` | README や設計書で参照するデモ GIF・図表・スクリーンショット |
