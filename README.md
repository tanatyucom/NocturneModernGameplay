# NocturneModernGameplay

SMT3 Nocturne HD Remaster (Steam版, IL2CPP) 向け MelonLoader MOD。

このリポジトリの `docs/` には、`SkillMutation` / `Learn-As-New` 機能について
これまで行ったネイティブ解析(GameAssembly.dll / global-metadata.dat の静的
解析)と設計検討の記録をまとめています。新しく作業を始める前に、まず
[`docs/investigation-log.md`](docs/investigation-log.md) を読んでください。

## プロジェクト概要

- 対象: SMT3 Nocturne HD Remaster (Steam版) の IL2CPP ビルド
- ツール: MelonLoader + HarmonyLib
- 主要ソース: `SkillMutationLearnAsNew.cs`, `SkillMutationTelemetry.cs`,
  `SkillMutationAlways.cs`, `GameplayFeatureRegistry.cs`,
  `GuiMetadataBridge.cs`, `ModMain.cs`

## 機能の目的

通常のレベルアップで「スキル変化(Mutation)」が発生した際、バニラは元スキルを
上書きしてしまう。これを「元スキルを保持したまま、新スキルを追加習得する」
(Learn-As-New) 挙動に変える。8枠満杯の場合は忘れるスキル選択UIを合成的に
起動し、プレイヤーに選ばせる。

## 現在のステータス(概要)

| 項目 | 状態 |
|---|---|
| `_mutationHandled`単一bool問題 | 修正済み(`HandledSlots`へ移行) |
| HandledSlots → native overwrite混在 | 原因確定、対策未実装 |
| Candidate stateの複数unit非対応 | 設計確定、未実装 |
| Global Pending Queue 滞留 | 原因確定、対策未実装 |
| Cross-unit UI表示混線 | 大部分は正常仕様としてクローズ |
| Design A(Global Queue) vs Design B(Per-unit Inline Gate) | 検討中(黄信号) |

詳細は [`docs/investigation-log.md`](docs/investigation-log.md) を参照してください。

## ビルドについて

net6ベースの MelonLoader / Il2CppInterop アセンブリを使用しているため、
古い Mono/mcs 環境ではビルドできません。dotnet SDK が入った実機環境で
ビルドしてください。
