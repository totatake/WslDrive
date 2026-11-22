# ADR-0001: WSL2 (`wsl --mount`) 方式の採用

- **ステータス**: Accepted
- **日付**: 2026-11-14
- **関連文書**: [システムアーキテクチャ](../architecture.md), [概要・比較](../overview.md)

## 1. 背景と課題 (Context)

Windows 11 環境から別ドライブや `.vhdx` 上の Linux パーティション（`ext4` / `btrfs` / `xfs` 等）にドライブレター経由でアクセスするツールを構築するにあたり、ファイルシステムの解釈・マウント基盤の選定が必要でした。

## 2. 検討した選択肢 (Alternatives Considered)

### 案 A: `LTRData.DiscUtils.Ext` + `DokanNet` (C#)
- **メリット**: NuGet パッケージのみで完結し、Windows 同居ディスクも扱える。
- **デメリット / リスク**: `ext2/3/4` のみ対応で一部拡張機能に制約があり、Dokan カーネルドライバのインストールが必須。

### 案 B: Windows 11 標準 WSL2 (`wsl --mount`) + ドライブ割当
- **メリット**: 本物の Linux カーネルドライバによる 100% の互換性と `ext4` / `btrfs` / `xfs` 等の幅広い対応。サードパーティ製カーネルドライバ導入が不要で BSOD や署名問題がない。
- **デメリット / リスク**: `net use` 制限や UAC 分離、Windows 同居ディスクの `ERROR_SHARING_VIOLATION` など OS 固有仕様への対処が必要。

### 案 C: `libext2fs` + `Dokan` (C/C++)
- **メリット**: e2fsprogs 公式ライブラリによる高い ext4 互換性。
- **デメリット / リスク**: `ext2/3/4` 限定であり、Dokan ドライバ導入と C ライブラリの Windows ビルド保守が必要。

## 3. 決定 (Decision)

**案 B（WSL2 + ドライブ割当）** を採用する。OS 標準機能のみで安全かつ高いファイルシステム互換性を得られ、課題となるドライブ割当・同居ディスク制約も Win32 API と標準カーネル NBD モジュールで解決可能なため。

## 4. 結果と影響 (Consequences)

- **良い影響**: 外部ドライバのインストール不要で `ext4` / `btrfs` / `xfs` に対応可能になった。
- **受け入れるトレードオフ / 今後の制約**: 動作前提として Windows 11 上で WSL2 と 1 つ以上の Linux ディストリビューションが有効化されている必要がある。
- **関連する変更箇所**: [WslMountService.cs](../../src/WslDrive.Core/Wsl/WslMountService.cs), [WslCliRunner.cs](../../src/WslDrive.Core/Wsl/WslCliRunner.cs)
