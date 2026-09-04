# Acquisition Mode / LearnAsNew Investigation

更新日: 2026-09-03(PC終了前の保全作業として記録)

## 参照優先順位

会話再開時は、この文書を読む前に必ず以下を先に読むこと。

1. `00_PROJECT_RULES.md`
2. `01_CURRENT_STATE.md`(Canonical State。本文書はそこに未昇格の分類も含む)
3. 本文書(`investigations/ACQUISITION_LEARNASNEW/PLAN.md`)

`investigations/PRESENTATION_CONSUMER/`・`investigations/POWERUP_MUTATION_CFG/`は別テーマ(presentation欠落・CFG振り分け)。本investigationは「上書き(Overwrite)ではなく新規取得(LearnAsNew)として扱えるnative制御点」をzero-baseで調査している。

## 目的

`[SkillMutation]/[SkillPowerUp] Acquisition = Overwrite / LearnAsNew`を実装するにあたり、旧Queue/Inline/Learn-As-New実装を復活させず、native自身の分岐選択に同一lifecycle内で介入するだけで完結する設計を特定する。**今回のセッションではまだ実装しない。解析のみ。**

## CONFIRMED(01_CURRENT_STATE.mdへ既に昇格済み)

- `rstOverWriteSkill(ref Int32 pSkill, UInt16 NewSkillID)`(VA `0x182285FF0`)は実質`*pSkill=NewSkillID`のみ。対象slot選択は呼び出し元責務。呼び出しsiteは`rstUpdateSeqSkillPowerUp`(VA `0x18228C770`)内2箇所(PUpSkillResult==1/2両方で同一パターン)。
- slot pointer計算式: `&pCurrentStock.skill[PUpSkillIndex]`(CLR Int32[]標準レイアウト、境界チェックあり)。
- `GBWK.PUpSkillIndex`(+0x4C)の唯一のwriterは`rstRndGetPowerUpSkill`実体(VA `0x1965489A0`)内`0x196548B15: mov byte ptr [r12], al`。`rstCalcSkillPowerUpCore`(VA `0x18227E100`)は読むだけ。
- PUpSkillIndex生成algorithm: 所持skillを動的count(`pStock+0x48`)までscan → `skillID==0`は空きsentinelとしてskip → `cmbGetPowerUpSkill(skillID)!=0`のみcandidate → RNGで1つ選択 → **既存skillの**slot indexをwrite。**空きslot選択の経路はこの関数に存在しない。**
- `rstAddSkill`(VA `0x182285A40`)は`rstUpdateSeqDefaultSkill`(VA `0x182288790`)内2箇所からのみ呼ばれる。`rstUpdateSeqSkillPowerUp`からは呼ばれない。
- `rstReplaceSkill`(VA `0x1822860E0`)はdirect-call xrefが実行可能セクション全域で見つからない。
- `rstChkSkillAct`(VA `0x182285B50`)がDefaultSkill/SkillPowerUp両方から呼ばれる共通ゲート関数。
- seq dispatch table(`rstUpdate`、VA `0x18228CDE0`、table VA `0x18228D5F4`): seq8→`rstUpdateSeqDefaultSkill`、seq10→`rstUpdateSeqSkillPowerUp`、seq21→`rstUpdateSeqDestroySkill`(VA `0x1822890D0`)、seq22→`rstUpdateSeqDestroyConfirm`(VA `0x182288B20`)。

## STRONGLY SUPPORTED

- `rstUpdateSeqDefaultSkill`内`[source_object+0x3C]==2`で分岐し、成立側で`[cursor+0x14]=8`書き込み直後に`SeqInfo.Current=21`を直接write(forget flow入口)。「8」がslot capacityを意味する解釈。
- `TERMINOLOGY.md`の「seq=21→22 forget flow」記述はjump table実体decodeによりCONFIRMED級の強度に到達(「22→8復帰」の内部write自体は未追跡でSTRONGLY SUPPORTED止まり)。
- DefaultSkillとSkillPowerUpは`rstChkSkillAct`という共通ゲートを持つが、seq8/seq10という別々のstate machineケースとして分離されている。

## HYPOTHESIS

- `rstAddSkill`発火経路(DefaultSkillのseq8・空きslotあり側)への合流、またはseq21 forget UIへの合流が、LearnAsNew実現経路の有力候補。
- Queue/Inlineなしで実現可能(ただし「2つの独立したnative state machineの橋渡し」という、想定より本質的な設計課題)。

## REJECTED

- 「PUpSkillIndexを空きslotへ変えるだけ」という単純LearnAsNew案。空きslot選択という概念自体がPUpSkillIndex生成関数に存在しないため成立しない。
- `rstOverWriteSkill`呼び出しsite自体への介入。既にslot確定済みの箇所であり、書き換えは「上書き結果の偽装」に近くなるリスクが高い。

## UNRESOLVED(次回最優先)

1. **`rstAddSkill`内部ヘルパー関数の解析**: `0x18240DEB0` / `0x1824104E0` / `0x182410C60`。目的: empty slot追加の実体、count更新、skill array mutation、8枠満杯判定、seq21 flowとの接続。
2. seq22→8復帰時の内部write(`rstUpdateSeqDestroyConfirm`側)。
3. `rstReplaceSkill`の実際の呼び出し元(indirect callの可能性)。
4. Power-Up/Mutation結果(`PUpSkillResult`/`PUpSkillID`)をDefaultSkill側state machine(seq8/21)へ合流させる具体的control point。

## 次にやること(再開時の手順)

1. `00_PROJECT_RULES.md` → `01_CURRENT_STATE.md` → 本文書の順に読む。
2. `rstAddSkill`内部ヘルパー(`0x18240DEB0`/`0x1824104E0`/`0x182410C60`)をGameAssembly.dllのオフラインbyte-level disassemblyで解析。
3. 可能ならseq22→8復帰のinternal writeを確認。
4. 上記が揃ってから、LearnAsNewの具体的control point候補(A: rstAddSkill合流、B: seq21合流)を再評価。
5. 設計確定後、ユーザー承認を得てから実装フェーズへ。

## GUI仕様(確定済み、backend成立後のみ表示)

```
☐ 新規取得として扱う / ☐ Learn as new skill
OFF = Overwrite, ON = LearnAsNew
config: "Acquisition": "Overwrite" | "LearnAsNew"
```

backend未実装中はGUIへ表示しない。

## 禁止事項(継続)

- 旧Queue/Inline/Learn-As-New実装の復活。
- `SeqInfo.Current`をMODから直接write(解析のみ許可、実装は禁止)。
- `pCurrentStock`/`WorkStock`の過去unitへのrebind。
- `TargetIndex`/`TargetCnt`の推測write。
- presentation sequenceの強制遷移。
- Queueでの別unit再生、Inlineでのforget flow無理矢理挿入。
- return値だけの偽装。
- source変更・build・deploy・Git操作は、次回明示的な指示があるまで行わない。
