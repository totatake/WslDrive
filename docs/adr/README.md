# Architecture Decision Records (ADR)

**WslMountDrive** における重要なアーキテクチャ選定、Windows 11 / WSL2 固有課題の解決策、および技術的トレードオフの判断記録です。

## 運用ルール

1. 新しい設計判断を追加する場合は、`docs/adr/NNNN-<short-kebab-title>.md`（4 桁連番）を作成します。
2. 過去の ADR は削除せず、後続の判断で置き換えた場合はステータスを `Superseded by ADR-XXXX` に更新します。
3. 追加・更新時は下の索引テーブルを必ず同期してください。

## ADR 一覧

| ID | タイトル | ステータス | 日付 | 概要 |
| :--- | :--- | :--- | :--- | :--- |
| [ADR-0001](0001-wsl2-mount-over-user-mode-fs-drivers.md) | WSL2 (`wsl --mount`) 方式の採用 | Accepted | 2026-11-14 | サードパーティ製カーネルドライバ不要で 100% Linux FS 互換性を得るための技術選定 |
| [ADR-0002](0002-define-dos-device-and-uac-token-impersonation.md) | `DefineDosDeviceW` と非昇格トークン偽装によるドライブ割当 | Accepted | 2026-11-14 | `p9np.dll` 2階層制限（エラー64/67）と UAC セッション分離の回避設計 |
| [ADR-0003](0003-default-ro-noload-with-optional-rw.md) | 既定 `ro,noload` 保護と任意 RW オプション | Accepted | 2026-11-22 | 未クリーン終了 ext4 の保護、`ro` フォールバック、および `--rw` 切替とモード不整合防止 |
| [ADR-0004](0004-wsl-unmount-ordering-and-multi-partition-management.md) | `wsl --unmount` 実行順序とマルチパーティション管理 | Accepted | 2026-11-17 | `WSL_E_DISK_ALREADY_MOUNTED` 回避、ステートレス復元、兄弟パーティション個別・一括解除 |
| [ADR-0005](0005-read-only-nbd-bridge-for-windows-shared-disks.md) | Windows 同居ディスク向け Read-Only NBD ブリッジ | Accepted | 2026-11-17 | `ERROR_SHARING_VIOLATION` 回避のための 3 重 RO 保護 `/dev/nbdX` ハイブリッドブリッジ |
