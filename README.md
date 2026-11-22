# WslDrive

Windows 11上で、別ドライブにあるLinuxパーティション（`ext4` / `btrfs` / `xfs` 等）を安全にマウントし、エクスプローラーから通常のドライブレター（`L:\` 等）として透過的にアクセスできるC#ユーティリティです。デフォルトは安全な **Read-Only（読み取り専用）** でマウントし、オプション指定（CLI `--rw` / GUI チェックボックス）により **Read-Write（読み書き可能）** でのマウントにも対応します。

サードパーティ製カーネルドライバ（Dokan / WinFsp等）のインストールは不要で、Windows 11標準の **WSL2 (`wsl --mount`)** と **非昇格ユーザートークンによるドライブマッピング** を組み合わせて動作します。

## 主な特徴

- **ドライバ導入不要 ＆ 100% カーネル互換性**: WSL2のLinuxカーネル純正ドライバを用いるため、`ext4` だけでなく `btrfs` / `xfs` などもネイティブ互換性でアクセス可能。
- **デフォルト Read-Only 保護 (`ro,noload`) ＆ オプション RW 対応**: 標準では ext4 のジャーナル再生すら行わない `ro,noload` オプションを適用し、接続したLinuxディスクへの意図しない書き込みを防止します。必要に応じて `--rw`（GUIではチェックボックス）で Read-Write (`rw`) マウントに切り替えられます（Windows 使用中ディスク向けの NBD ブリッジは安全のため Read-Only 専用）。
- **マウント前のスーパーブロック事前解析**: 物理ドライブ（`\\.\PhysicalDriveX`）やイメージファイルのGPT/MBRおよび各パーティションのスーパーブロック（Magic・ラベル・UUID）を直接読み取り、マウント前からどのパーティションがLinuxデータか分かりやすく一覧表示します。
- **Plan 9 制限および UAC セッション分離を自動回避**: `net use`（`p9np.dll`）では拒否されるサブディレクトリ UNC パス（`\\wsl.localhost\<Distro>\mnt\wsl\...`）に対し、NT オブジェクトマネージャの DOS デバイスシンボリックリンク（`DefineDosDeviceW`）と `explorer.exe` 非昇格トークン偽装（`ImpersonateLoggedOnUser`）を組み合わせて割り当てるため、**特別なレジストリ変更なしに通常ユーザーのエクスプローラーと管理者ターミナルの双方へ `L:` ドライブが即座に表示**されます。
- **CLI ＆ WPF GUI（タスクトレイ常駐対応）** の両方を提供。

## クイックスタート

### ビルドとテスト（Windows 11 / .NET 8 SDK）
```powershell
dotnet build WslDrive.sln -c Release
dotnet test WslDrive.sln -c Release
```

### CLIでの利用例（管理者ターミナルで実行）
```powershell
# 物理ディスクのLinuxパーティション一覧を表示
WslDrive.Cli.exe list

# PhysicalDrive1 の Partition 2 を L: ドライブとして RO マウント（デフォルト）
WslDrive.Cli.exe mount --disk 1 --partition 2 --letter L:

# PhysicalDrive1 の Partition 2 を L: ドライブとして RW（読み書き可能）マウント
WslDrive.Cli.exe mount --disk 1 --partition 2 --letter L: --rw

# マウント状態確認
WslDrive.Cli.exe status

# アンマウント ＆ ドライブ解除
WslDrive.Cli.exe unmount --letter L:
```

## ドキュメント

詳細な設計背景、4つのWindows/WSL2固有課題と設計判断記録（ADR）、ビルド・単体テスト・結合テスト手順については `docs/` 配下をご参照ください。

- [アーキテクチャ設計・開発計画書 (`docs/architecture_and_plan.md`)](docs/architecture_and_plan.md)
- [ビルド・利用・テストガイド (`docs/testing_and_usage_guide.md`)](docs/testing_and_usage_guide.md)
