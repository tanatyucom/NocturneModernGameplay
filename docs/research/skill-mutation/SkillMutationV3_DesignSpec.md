# NocturneModernGameplay Skill Mutation V3 — PoC1 Design Specification

## Scope

PoC1は`cmbGetMutationSkill`のPostfixでnative Mutationの成立を観測するだけの、
完全read-onlyな検出段階です。V3専用stateはlegacyの`_active`、`_inlineAwaiting`、
`_pending`と共有しません。ゲーム進行の補正、忘却UIの起動、スキル配列の変更は行いません。

## CONFIRMED

- real GBWK static slot = `0x182e464b8`
- `pCurrentStock` = real GBWK `+0x60`
- `WorkStock` = real GBWK `+0x68`
- `SeqInfo.Current` = SeqInfo object `+0x11`
- `DefSkillResult` = real GBWK `+0x3C`
- `rstChkAddSkill`の8枠満杯時result = `2`
- `rstInitSkillAct(8)`はnativeの8枠満杯時learn action初期化
- `rstUpdateSeqDefaultSkill`は最終的にnativeの`Current=21`遷移を行う
- 忘却フロー = `21 -> 22 -> 8`
- 忘却から戻った後の通常native flowはMutation／進行処理を継続する
- Queueによる遅延replayはownership mismatchを起こし得る

絶対アドレスは対象build固有です。将来参照が必要な場合はhardcodeせず、
`moduleBase + RVA`で解決します。

## REJECTED / DO NOT PORT INTO V3

- queued historical unit replay model
- `pCurrentStock` rebinding
- Inline transactionに対する`RestoreSkillProgress`
- `SeqInfo.Current=21`の直接書き込み
- `TargetIndex` / `TargetCnt`操作
- broad progression gate
- Queue-era completion arbitrationの一括移植

## UNRESOLVED

- 最小でnative-compatibleなfull-slot Inline transactionの正確な形
- injected Learn-As-New中の`WorkStock` lifecycle
- Two Frost duplicationの原因
- 一時的なHP／Lv表示破損の原因

未解決事項は推測でCONFIRMEDへ昇格させません。各段階でstatic/native解析、
read-only telemetry、最小実機試験の順に検証します。

## PoC1 Behavior

`src/SkillMutationV3.cs`は既存telemetry Hookと共存するV3専用Postfixを持ち、
Mutation resultが非ゼロの場合に次の情報だけを記録します。

```text
V3-MUTATION-DETECTED
unit=
stockPtr=
original=
mutated=
frame=
```

V3専用stateはPoC1で`Idle`と`MutationDetected`のみを使用します。
`AwaitingNativeForgetTransition`、`ForgetFlowActive`、`Completed`は将来段階の候補であり、
PoC1では使用しません。

## PoC1 Non-Intervention Contract

PoC1は以下を行いません。

- `WorkStock`／`pCurrentStock`の書き換え
- `SeqInfo.Current`／`SeqInfo.Change`の書き換え
- `TargetIndex`／`TargetCnt`の変更
- `DefSkillResult`／`PUpSkillID`／`SelectSkillID`の変更
- `rstInitSkillAct`、`rstUpdateSeqDefaultSkill`、`rstAddSkill`、`cmbAddSkill`の呼び出し
- original skill復元／mutated skill追加
- Queue操作、snapshot restore、progression restore
- native replacement suppression

PoC1の合格条件は、実機Mutation時に`V3-MUTATION-DETECTED`が記録され、
V3由来のnative state変更がないことです。ゲーム挙動の改善はPoC1の対象外です。
