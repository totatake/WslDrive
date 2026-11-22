# ロードマップ (`WslDrive`)

本ドキュメントでは **WslDrive** の達成済みマイルストーンと今後の機能拡張・改善計画を管理します。

## 1. 完了済みマイルストーン

| バージョン / 状況 | マイルストーン | 主な実装内容 | 関連資料 |
| :--- | :--- | :--- | :--- |
| `v0.1.0` (Completed) | 初期基盤構築 | `WslDrive.Core` / `WslDrive.Cli` / `WslDrive.App` / `WslDrive.Tests` の実装、スーパーブロック解析、`DefineDosDeviceW` + トークン偽装マッピング | [architecture.md](architecture.md), [ADR-0002](adr/0002-define-dos-device-and-uac-token-impersonation.md) |
| `v0.1.0` (Completed) | マルチパーティション ＆ WSL2 アンマウント制御強化 | 同一ディスク上複数パーティションの同時マウント・個別/一括アンマウント、ステートレス `/proc/mounts` 復元、stale マウント補完 | [ADR-0004](adr/0004-wsl-unmount-ordering-and-multi-partition-management.md) |
| `v0.1.0` (Completed) | Windows 同居ディスク向け Read-Only NBD ブリッジ | `AlignedDiskStream` + `ReadOnlyNbdServer` + WSL2 `/dev/nbdX` ハイブリッド自動切替、CLI 常駐 `__nbd-daemon` | [ADR-0005](adr/0005-read-only-nbd-bridge-for-windows-shared-disks.md) |
| `v0.1.0` (Completed) | `WslDrive` への改称・任意 RW・日英 UI・標準 OSS 化 | 改称、CLI `--rw` / GUI RW 切替、日英自動切替（`Auto` / `日本語` / `English`）、`v0.1.0` リリース整備 | [ADR-0003](adr/0003-default-ro-noload-with-optional-rw.md), [docs/README.md](README.md) |

## 2. 今後の検討項目 (Future Horizons)

- **LUKS 暗号化パーティションの復号マウント検討**: 現在は `SuperblockInspector` による検出・表示のみ対応。WSL 内 `cryptsetup luksOpen` 連携の要否・セキュリティ設計を検討する。
- **プロファイル / お気に入りマウント設定の保存**: 頻繁に利用する VHDX や物理ディスク＋ドライブレターのプリセット保存機能。
- **パッケージ配布（winget / Chocolatey / GitHub Releases）**: タグ駆動リリースワークフロー（`.github/workflows/release.yml`）の運用定着とインストーラーまたはパッケージマニフェスト整備。
