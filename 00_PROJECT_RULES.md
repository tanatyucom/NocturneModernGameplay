# NocturneModernGameplay Project Rules

## 基本方針

- Skill Mutation V3はzero-base再実装とする。
- 旧Queue / Inline architectureを復活させない。
- legacyコードをactive `src`へ戻さない。
- speculative implementationを行わない。
- 不確実な場合は停止して報告する。

## Evidence Discipline

Evidenceは以下に分類する。

- `CONFIRMED`
- `STRONGLY SUPPORTED`
- `HYPOTHESIS`
- `REJECTED`
- `UNRESOLVED`

運用ルール:

- `HYPOTHESIS`を自動的に`CONFIRMED`へ昇格しない。
- static / native / runtime evidenceを混同しない。
- 過去資料より[01_CURRENT_STATE.md](01_CURRENT_STATE.md)を優先する。
- `REJECTED`事項を勝手に復活させない。

## Native Write Guardrails

以下への直接writeを禁止する。

- `SeqInfo.Current`
- `TargetIndex`
- `TargetCnt`
- `pCurrentStock`
- `WorkStock`

現在のpresentation調査中は、以下へのwriteも禁止する。

- source object `+0x91 / +0x92 / +0x94 / +0x98`
- `State_182e31630`

fail closedとし、推測したアドレスへアクセスしない。

## Git Guardrails

以下を禁止する。

- `git add`
- `git commit`
- `git push`
- `git reset`
- `git clean`
- その他のdestructive Git操作

特に`git clean`は使用しない。

## Build Guardrails

- clean buildは`bin` / `obj`の削除で行う。
- build failure時はdeployしない。
- build failureをspeculative fixで回避しない。

## AI Role Separation

(2026-09-13更新、User承認済み)

### ChatGPT

- coordinator
- 全体のEvidence整理、矛盾チェック
- runtime log analysis
- visible / invisible差分抽出
- 次の調査方針提案
- Canonical判断のとりまとめ

### Claude

- native reverse engineering / CFG / writer・reader追跡 / native意味論
- read-only trace等の実装
- clean build(`bin`/`obj`削除 → `dotnet build`)
- Modsフォルダへのdeploy、SHA-256検証
- production候補が完成するまで、上記を1セッション内で通しで実施してよい(実機テスト直前まで)
- git add / commit / push等の破壊的操作は行わない(Git Guardrails継続適用)

### Codex

- release / publication / final packaging主担当
- 配布物作成、GitHub/Nexus/GameBanana向け作業
- release/tag
- 実装のfinal整理(Claude実装分の重複作業ではなく、公開前の最終整理)

### User

- actual-game testing
- final approval
- Canonical State更新承認

Canonical Stateは、人間承認前に確定版として扱わない。
