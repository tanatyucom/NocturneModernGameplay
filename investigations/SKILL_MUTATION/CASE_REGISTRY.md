# Skill Mutation V3 Case Registry

更新日: 2026-09-02

## 命名規則

`<Demon>-<Event>-<Outcome>-<Serial>`を使用する。

- Demon: `HP` = High Pixie、`JF` = Jack Frost
- Event: `MUT` = Skill Mutation、`PWR` = ordinary Skill Power-Up、`NOMUT` = Mutation未検出
- Outcome: `VIS` = presentation表示あり、`FAIL` = Mutation成立後のpresentation欠落
- Serial: 同分類内の3桁連番

一度割り当てたCase IDは改名しない。未確定値は推測せず`UNRESOLVED`とする。

## Registry

| Case ID | Test Group | Observed Order | Unit | Demon | Original Skill | Native Result | Mutated Skill | Mutation Detected | Replacement Confirmed | PUpSkillResult | Presentation State | Actual Screen Result | Classification | Key Frames | Notes | Confidence |
|---|---|---:|---:|---|---:|---:|---:|---|---|---|---|---|---|---|---|---|
| `HP-MUT-VIS-001` | `HP-PAIR-001` | `UNRESOLVED` | 59 | High Pixie | 36 | 28 | 28 | YES | YES | `2 -> -1` | `State+04 0 -> 1 -> 0`; `State+0c 4 -> 7` | スキル変化表示あり | `MUTATION_VISIBLE` | `UNRESOLVED` | `HP-MUT-FAIL-001`とのpaired case。`V3-MUTATION-DETECTED`と`V3-REPLACEMENT-VISIBLE`あり。 | HIGH |
| `HP-MUT-FAIL-001` | `HP-PAIR-001` | `UNRESOLVED` | 59 | High Pixie | 36 | 48 | 48 | YES | YES | `2 -> -1` | `State+04 0 -> 1 -> 0`; `State+0c 4 -> 7` | スキル変化表示なし | `MUTATION_PRESENTATION_FAILURE` | `UNRESOLVED` | 最重要paired case。Mutation Logicとreplacementは成立し、presentationのみ欠落。 | HIGH |
| `HP-NOMUT-001` | `HP-NOMUT-001` | `UNRESOLVED` | 59 | High Pixie | `UNRESOLVED` | 0 | 0 | NO | NO | `UNRESOLVED` | `UNRESOLVED` | 表示なし | `MUTATION_NOT_DETECTED` | `UNRESOLVED` | `V3-MUTATION-DETECTED`なし。presentation failureとは別分類。 | MEDIUM |
| `JF-PWR-VIS-001` | `JF-PWR-001` | `UNRESOLVED` | 60 | Jack Frost | `UNRESOLVED` | `UNRESOLVED` | `N/A` | NO | `N/A` | 1 | `UNRESOLVED` | 「スキル変化」に見える表示あり | `ORDINARY_POWER_UP_VISIBLE` | `UNRESOLVED` | 画面表示だけをMutation成立Evidenceに使用できない基準Case。 | HIGH |

## 運用上の注意

- `V3-REPLACEMENT-VISIBLE`はskill array上のreplacement確認であり、画面presentation成功を意味しない。
- Actual Screen Resultはユーザー観測、Mutation DetectedとReplacement Confirmedはruntime log Evidenceとして別々に扱う。
- 元ログのファイル名とframeを回収できた場合は、Case IDを変えずに該当欄だけを追記する。
