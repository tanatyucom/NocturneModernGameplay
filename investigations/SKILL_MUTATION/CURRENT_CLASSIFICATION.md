# Skill Mutation V3 Current Classification

更新日: 2026-09-02

この文書はCase Registryの短い索引であり、承認済みCanonical Stateである`01_CURRENT_STATE.md`を置き換えない。

## CONFIRMED

- ordinary Skill Power-UpとMutationは画面表示だけでは区別できない。
- `PUpSkillResult=1`はordinary Power-Up系として観測されている。
- Mutation成立ケースには`V3-MUTATION-DETECTED`が存在する。
- `nativeResult != 0`を伴うMutation成立例がある。
- replacement成立は`V3-REPLACEMENT-VISIBLE`で確認できる。ただし、このmarkerは画面表示成功を意味しない。
- `unit=59`にはMutation Logicとreplacement成立後に表示だけ欠落した`HP-MUT-FAIL-001`が存在する。
- visible / invisible双方で`source+0x60=null`の観測がある。
- `State+04`が`0 -> 1 -> 0`を通る例がある。
- `State+08`は現在の観測境界では0のままである。
- 表示なしには、少なくとも`HP-NOMUT-001`のMutation未検出と`HP-MUT-FAIL-001`のpresentation failureという2種類がある。

## UNRESOLVED

- presentation failureを決めるconsumer branch。
- `State+08`の観測境界間における瞬間変化。
- `pCurrentStock` ownership切替とpresentation成否の因果関係。
- `0x182281c30`(= `rstSmoothMotion`、identityはCONFIRMED済み、`01_CURRENT_STATE.md`参照)が呼ばれた際の実際のpresentation成否条件。
- `0x18202e300`の意味。
- presentation requestの最終発行地点。
- visible / invisibleの呼び出し順序差。
- 過去Caseの固定Case IDへの再分類。
- `HP-MUT-VIS-001`、`HP-MUT-FAIL-001`等の元ログファイル名と正確なframe。

## 現在の基準Case

- visible Mutation比較基準: `HP-MUT-VIS-001`
- presentation failure比較基準: `HP-MUT-FAIL-001`
- Mutation未検出比較基準: `HP-NOMUT-001`
- ordinary Power-Up比較基準: `JF-PWR-VIS-001`
