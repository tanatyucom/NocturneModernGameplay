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

### ChatGPT

- coordinator
- runtime log analysis
- visible / invisible差分抽出
- Evidence整理
- Claude結果のcross-check

### Claude

- native reverse engineering
- CFG
- writer / reader追跡
- native意味論

### Codex

- implementation
- grep / search
- diff
- build
- deploy
- SHA-256検証
- 機械的抽出

### User

- actual-game testing
- final approval
- Canonical State更新承認

Canonical Stateは、人間承認前に確定版として扱わない。
