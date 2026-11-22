# WslDrive

> **Windows 11上で Linux パーティション（`ext4` / `btrfs` / `xfs`）を WSL2 経由で通常ドライブとしてマウント — デフォルトは安全な Read-Only（`ro,noload`）、オプションで Read-Write（`--rw`）にも対応**

**Version:** `v0.1.0` | **Language:** [English](README.md) | 日本語

---

## 概要

`WslDrive` は、Windows 11 上で別ドライブや `.vhdx` 仮想ディスク、さらに Windows と同居する物理ディスク上の Linux パーティションを WSL2 経由でマウントし、エクスプローラーや管理者ターミナルから通常のドライブレター（`L:\` 等）として透過的にアクセスできる C# ユーティリティ（CLI およびタスクトレイ常駐対応 WPF GUI）です。

サードパーティ製カーネルドライバ（Dokan / WinFsp 等）のインストールは不要で、Windows 11 標準の WSL2 カーネルドライバと NT DOS デバイスリンク（`DefineDosDeviceW`）および非昇格トークン偽装（`explorer.exe`）を組み合わせて動作します。

## 主な特徴

- **ドライバ導入不要 ＆ Linux カーネル互換性**: WSL2 の Linux カーネル純正ドライバを用いるため、`ext4` / `btrfs` / `xfs` などを追加ドライバなしで安全にアクセス可能。
- **既定 Read-Only 保護 (`ro,noload`) ＆ 任意 RW 対応 (`--rw`)**: 標準では ext4 のジャーナル再生すら行わない `ro,noload` を適用し、意図しない書き込みを防止します。必要に応じて CLI の `--rw` または GUI のチェックボックスで Read-Write (`rw`) へ切り替えられます。
- **マウント前のスーパーブロック事前解析**: 物理ディスクやイメージファイルの GPT/MBR/EBR およびスーパーブロック（`ext2/3/4`, `btrfs`, `xfs`, `swap`, `LUKS`）を直接 Read-Only 走査し、マウント前から FS 種別・ラベル・UUID を一覧表示します。
- **Plan 9 制限および UAC セッション分離を自動回避**: `net use`（`p9np.dll`）で拒否されるサブディレクトリ UNC パス（`\\wsl.localhost\<Distro>\mnt\wsl\...`）に対し、`DefineDosDeviceW` と `explorer.exe` 非昇格トークン偽装を組み合わせることで、レジストリ変更なしに通常エクスプローラーと管理者ターミナルの双方へ即座にドライブを表示します。
- **Windows 同居ディスク向けハイブリッド Read-Only NBD ブリッジ**: Windows が `C:` 等を使用中の物理ディスクでも、`ERROR_SHARING_VIOLATION` を検出・回避して WSL2 `/dev/nbdX` 経由で読み取り専用マウントできます（安全性のため NBD ブリッジ経路は Read-Only 専用）。
- **マルチパーティション個別・一括アンマウント制御**: 同一物理ディスク・VHDX 上の複数パーティション同時マウントと、他パーティションを維持したままの個別解除に対応しています。

## クイックスタート

### ビルドとテスト

ビルド済みバイナリの実行には Windows 11、WSL2、および [.NET 8.0 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0)（`winget install Microsoft.DotNet.DesktopRuntime.8`）が必要です。ソースからビルドする場合は .NET 8.0 SDK（`net8.0-windows`）を使用します：

```powershell
dotnet build WslDrive.sln -c Release
dotnet test WslDrive.sln -c Release
```

ヘルパースクリプトを使う場合：

```powershell
.\scripts\build-and-test.ps1
```

### CLI での利用例

管理者として実行したターミナルから利用します：

```powershell
# 物理ディスクおよび検出された Linux パーティション一覧を表示
WslDrive.Cli.exe list

# PhysicalDrive1 の Partition 2 を L: ドライブとして RO マウント（既定: ro,noload）
WslDrive.Cli.exe mount --disk 1 --partition 2 --letter L:

# Read-Write (読み書き可能) でマウントする場合
WslDrive.Cli.exe mount --disk 1 --partition 2 --letter L: --rw

# マウント状態とドライブ割当の確認
WslDrive.Cli.exe status

# アンマウント ＆ ドライブ解除
WslDrive.Cli.exe unmount --letter L:
```

### GUI アプリの利用方法

1. `WslDrive.App.exe` を起動します（`app.manifest` により自動で UAC 昇格を要求します。非昇格時は右上の「管理者として再起動」から昇格できます）。
2. 検出された Linux パーティション行を選択し（`.vhdx` / `.vhd` / `.img` のドラッグ＆ドロップも可能）、ドライブレターを選んで **「▶ 選択行をマウント」** をクリックします。
3. 書き込みを行いたい場合は、マウント前に **「読み書き可能 (RW) でマウント」** をオンにします（Windows 同居ディスクの NBD ブリッジ経路では RO 専用となります）。
4. 画面下部のマウント一覧やタスクトレイアイコンから開く・アンマウント操作を行えます。

## ドキュメント

設計背景、技術判断記録（ADR）、詳細な利用手順、テスト仕様は [`docs/`](docs/README.md) にまとめています：

- [ドキュメント一覧 (`docs/README.md`)](docs/README.md)
- [概要・開発背景・既存手法との比較 (`docs/overview.md`)](docs/overview.md)
- [要件定義書 (`docs/requirements.md`)](docs/requirements.md)
- [システムアーキテクチャ設計書 (`docs/architecture.md`)](docs/architecture.md)
- [利用・CLI/GUI ガイド (`docs/guides/usage.md`)](docs/guides/usage.md)
- [トラブルシューティング (`docs/guides/troubleshooting.md`)](docs/guides/troubleshooting.md)
- [設計判断記録 ADR (`docs/adr/README.md`)](docs/adr/README.md)
- [テスト方針・トレーサビリティ (`docs/testing.md`)](docs/testing.md)
- [ロードマップ (`docs/roadmap.md`)](docs/roadmap.md)

## コントリビューション・セキュリティ

- 開発環境構築・ビルド手順・PR チェックリスト: [CONTRIBUTING.md](CONTRIBUTING.md)
- 脆弱性報告ポリシーおよび権限・セキュリティ境界: [SECURITY.md](SECURITY.md)
- 変更履歴: [CHANGELOG.md](CHANGELOG.md)

## ライセンス

本プロジェクトは **MIT License** の下で公開されています。詳細は [LICENSE](LICENSE) を参照してください。
