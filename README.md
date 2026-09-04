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

## プロジェクト範囲と設計方針

NocturneModernGameplayは、難易度・確率・育成効率・リソース管理など、
ゲーム結果に影響するルールを現代化するプロジェクトです。SMT3の世界観や
戦闘の基本設計を維持しつつ、SMT5以降に近い快適性を取り入れます。

入力・カメラ・操作短縮など、ゲーム結果そのものを変更しない改善は
別プロジェクトの`NocturneModernController`が担当します。また、既に
コミュニティMODで成立している機能は原則として重複実装しません。

## 機能の目的

通常のレベルアップで「スキル変化(Mutation)」が発生した際、バニラは元スキルを
上書きしてしまう。これを「元スキルを保持したまま、新スキルを追加習得する」
(Learn-As-New) 挙動に変える。8枠満杯の場合は忘れるスキル選択UIを合成的に
起動し、プレイヤーに選ばせる。

## 現在開発中の機能

### Skill Mutation: Learn As New

スキル変化時に元スキルを残し、変化後スキルを新規スキルとして習得する機能です。

| 項目 | 状態 |
|---|---|
| 空き枠へのLearn-As-New | 動作確認済み |
| 8枠満杯時のnative忘却UI | 開発・実機テスト中 |
| Inline Transaction Architecture | 実験・開発中 |

まだ完成機能としては扱いません。調査の完全な履歴は
[`master-archive.md`](docs/research/skill-mutation/master-archive.md)を参照してください。

## Roadmap

今後の予定、Controllerとの責任分離、コミュニティMODとの棲み分けは
[`docs/ROADMAP.md`](docs/ROADMAP.md)を参照してください。

## ビルドについて

net6ベースの MelonLoader / Il2CppInterop アセンブリを使用しているため、
古い Mono/mcs 環境ではビルドできません。dotnet SDK が入った実機環境で
ビルドしてください。
