# Skill Mutation V3 Evidence Index

更新日: 2026-09-02

Statusは`CONFIRMED`、`STRONG`、`POSSIBLE`、`WEAK`、`REJECTED`を使用する。
元ログ名またはframeが現Repositoryで確定できないEvidenceは、その事実をNotesに残す。

| Evidence ID | Claim | Status | Source Log | Frame / Time | Unit | Relevant Log Marker | Notes |
|---|---|---|---|---|---:|---|---|
| `EV-HP-VIS-DETECT-001` | `HP-MUT-VIS-001`でMutation Logicが成立した。 | CONFIRMED | user-confirmed paired-case summary (2026-09-02) | `UNRESOLVED` | 59 | `V3-MUTATION-DETECTED`; `nativeResult=28` | original=36、mutated=28。元ログ名はRepository内で未特定。 |
| `EV-HP-VIS-REPLACE-001` | `HP-MUT-VIS-001`でreplacementがskill arrayに現れた。 | CONFIRMED | user-confirmed paired-case summary (2026-09-02) | `UNRESOLVED` | 59 | `V3-REPLACEMENT-VISIBLE` | presentation成功とは独立したEvidence。 |
| `EV-HP-VIS-SCREEN-001` | `HP-MUT-VIS-001`では実機画面に表示があった。 | CONFIRMED | user actual-screen observation (2026-09-02) | `UNRESOLVED` | 59 | user observation | runtime logとは別Evidence種別。 |
| `EV-HP-FAIL-DETECT-001` | `HP-MUT-FAIL-001`でMutation Logicが成立した。 | CONFIRMED | user-confirmed paired-case summary (2026-09-02) | `UNRESOLVED` | 59 | `V3-MUTATION-DETECTED`; `nativeResult=48` | original=36、mutated=48。 |
| `EV-HP-FAIL-REPLACE-001` | `HP-MUT-FAIL-001`でreplacementがskill arrayに現れた。 | CONFIRMED | user-confirmed paired-case summary (2026-09-02) | `UNRESOLVED` | 59 | `V3-REPLACEMENT-VISIBLE` | presentation欠落後もMutation不成立とは分類しない。 |
| `EV-HP-FAIL-SCREEN-001` | `HP-MUT-FAIL-001`では実機画面に表示がなかった。 | CONFIRMED | user actual-screen observation (2026-09-02) | `UNRESOLVED` | 59 | user observation | `EV-HP-FAIL-DETECT-001`と組み合わせてpresentation failureを確定する。 |
| `EV-HP-PAIR-STATE04-001` | visible/invisible双方で`State+04`が`0 -> 1 -> 0`を通った。 | CONFIRMED | user-confirmed paired-case summary (2026-09-02) | `UNRESOLVED` | 59 | `V3-STATE-04-08-TRANSITION`; `V3-PRESENTATION-STATE` | 既知fieldだけでは成否を分離できないEvidence。 |
| `EV-HP-PAIR-STATE08-001` | 観測境界では`State+08`は0のままだった。 | CONFIRMED | user-confirmed paired-case summary (2026-09-02) | `UNRESOLVED` | 59 | `V3-PRESENTATION-STATE` | 境界間の瞬間変化は未解決。 |
| `EV-SRC60-NULL-001` | visible/invisible双方で`source+0x60=null`が観測された。 | CONFIRMED | Canonical Evidence summary (2026-09-02) | `UNRESOLVED` | 59 | `V3-SOURCE-PRESENTATION-STATE`; `V3-MOTIONREQ-BOUNDARY` | null単独をpresentation failure条件にはできない。 |
| `EV-HP-NOMUT-001` | 表示なしにはMutation自体が未検出の別Caseが存在する。 | CONFIRMED | user-confirmed case summary (2026-09-02) | `UNRESOLVED` | 59 | `V3-MUTATION-DETECTED`なし | absence Evidence。対象時間窓の元ログ回収が必要。 |
| `EV-JF-PWR-001` | `PUpSkillResult=1`のordinary Skill Power-Upがスキル変化風に表示される。 | CONFIRMED | user-confirmed case summary (2026-09-02) | `UNRESOLVED` | 60 | `V3-RAW-0x4B-COMPARE`; `pUpSkillResult=1` | 表示ありだけでMutation成功と数えない根拠。 |
| `EV-CONSUMER-BRANCH-001` | presentation failureを決めるconsumer branchは未特定である。 | POSSIBLE | `01_CURRENT_STATE.md`; presentation investigation | N/A | N/A | `V3-MOTIONREQ-BOUNDARY`ほか | 原因候補は未解決であり、特定branchをCONFIRMEDにしない。 |

CONFIRMED Evidence数: **11**
