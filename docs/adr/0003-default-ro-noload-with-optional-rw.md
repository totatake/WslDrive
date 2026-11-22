# ADR-0003: 既定 `ro,noload` 保護と任意 RW オプション

- **ステータス**: Accepted
- **日付**: 2026-11-22
- **関連文書**: [システムアーキテクチャ](../architecture.md), [要件定義書](../requirements.md)

## 1. 背景と課題 (Context)

1. Linux 側で正常アンマウントされていない Dirty な `ext4` パーティションを通常の `-o ro` でマウントすると、Linux カーネルがジャーナルを再生（ディスクへ書き込み）しようとしてマウント失敗や意図しないブロック更新が発生する場合があります。
2. 一方で、ユーザーが意図して `.vhdx` や別物理ドライブの Linux パーティションへファイルを書き込むユースケース（Read-Write マウント）にも対応したい要望がありました。

## 2. 検討した選択肢 (Alternatives Considered)

### 案 A: 常に Read-Only 専用ツールとする
- **メリット**: 誤書き込みリスクがゼロ。
- **デメリット / リスク**: ユーザーが別ドライブや `.vhdx` へファイルを書き戻したい際に別手順が必要になる。

### 案 B: 既定は `ro,noload`（自動 `ro` フォールバック付）、明示的オプションで `rw` 対応
- **メリット**: デフォルトでは `ro,noload`（`noload` 非対応の btrfs / xfs 等では自動で `ro` へフォールバック）を強制して 100% の Read-Only 安全性を保ちつつ、CLI `--rw` / GUI チェックボックスで明示された場合のみ `--options rw` でマウントできる。また `/proc/mounts` の既存マウント再利用時は要求モード（RO/RW）の一致を検証してモード混同を防ぐ。
- **デメリット / リスク**: UI・ドキュメント上で RO と RW の違いを誤認させない表示設計が必要。

## 3. 決定 (Decision)

**案 B（既定 `ro,noload` + 明示的オプトイン `rw`）** を採用する。日常のデータ保全を最優先としつつ、必要な書き込み用途にも安全に応えられるため。

## 4. 結果と影響 (Consequences)

- **良い影響**: 誤操作による書き込みを既定で防ぎつつ、CLI・GUI の双方で柔軟に RW マウントが可能になった。
- **受け入れるトレードオフ / 今後の制約**: Windows 同居ディスクの NBD ブリッジ経路では安全性優先のため RW を許可しない（[ADR-0005](0005-read-only-nbd-bridge-for-windows-shared-disks.md)）。
- **関連する変更箇所**: [MountRequest.cs](../../src/WslMountDrive.Core/Models/MountRequest.cs), [MountSession.cs](../../src/WslMountDrive.Core/Models/MountSession.cs), [WslMountService.cs](../../src/WslMountDrive.Core/Wsl/WslMountService.cs), [Program.cs](../../src/WslMountDrive.Cli/Program.cs), [MainWindow.xaml.cs](../../src/WslMountDrive.App/MainWindow.xaml.cs)
