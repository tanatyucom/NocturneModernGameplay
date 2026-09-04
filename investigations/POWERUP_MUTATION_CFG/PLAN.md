# Power-Up / genuine Mutation 振り分けCFG investigation

作業中断時点のsave state: 2026-09-03(bit6矛盾調査は決着。詳細は「RESOLVED」セクション参照)

## 参照優先順位

会話再開時は、この文書を読む前に必ず以下を先に読むこと。

1. `00_PROJECT_RULES.md`
2. `01_CURRENT_STATE.md`(Canonical State。この文書はまだそこへ昇格していない)
3. 本文書(`investigations/POWERUP_MUTATION_CFG/PLAN.md`)

`investigations/MUTATION_CHANCE_GATE/`はテーマ的に本investigationの前段(A-only構成でのMutation成立率調査)。本investigationはそこから派生し、「Power-UpとMutationをnativeがどこで振り分けているか」をゼロベースで再解析したもの。`investigations/PRESENTATION_CONSUMER/`は別テーマ(presentation表示欠落)。

## 目的

`GameAssembly.dll`の native CFGを、旧Patch A/B/CやQueue/Inline実装の意味づけを前提とせず、cpp2il ISIL dump + `global-metadata.dat`経由のVA解決 + 直接disassemble(capstone)で再構築し、「Skill Power-Up」と「genuine Skill Mutation」の分岐点を特定する。

---

## 今回確定したCFG(CONFIRMED、VA/命令レベルで検証済み)

### 主要関数とVA(`global-metadata.dat`のmethodPointersテーブル経由で確定)

- `rstcalc.rstCalcSkillPowerUpCore()` : VA `0x18227E100`(引数なし、`sbyte`戻り値)
- `rstcalc.rstCalcSkillPowerUp()` : VA `0x18227E5F0`(cpp2il ISILは解析失敗。**直接call/jmp xrefが0件**=通常gameplayからの直接呼び出しは未確認)
- `rstcalc.rstCalc(dds3ProcessID_t)` : VA `0x18227E710`(**直接call/jmp xrefが0件**、dds3ProcessIDテーブル経由の間接呼び出しと推定)
- `rstcalc.rstRndGetPowerUpSkill(datUnitWork_t, ref SByte)` : VA `0x1822810B0`。**このVAはthunk**(`jmp 0x1965489A0`)。実体は`0x1965489A0`。Coreからの呼び出しが唯一のxref。
- `rstcalc.rstCreateBeforeSkillList(SByte, datUnitWork_t, Int16, rstSkillInfo_t)` : VA `0x182280460`
- `fclCombineCalcCore.cmbChkSkillOwner(UInt16, datUnitWork_t) : sbyte` : VA `0x182410660`(thunkではなく実体、~75バイトの単純ownership-checkループ)
- `rstCalcCore.cmbGetMutationSkill(UInt16, datUnitWork_t) : ushort` : VA `0x18227B6B0`。**バイナリ全体でCore内の1箇所(`0x18227E577`)のみが呼び出し元**(xref scan確認済み)
- `rstCalcCore.cmbGetPowerUpSkill(UInt16) : ushort` : VA `0x18227BB90`。**thunk**(`jmp 0x196539430`)。実体はrstRndGetPowerUpSkillの実体(`0x1965489A0`)内部から呼ばれている(旧報告で「無関係なInvoker領域」と誤判定していたのを訂正 — 実際は`rstRndGetPowerUpSkill`の実体からの正当なgameplay呼び出し)
- `rstupdate.rstUpdateSeqSkillPowerUp()` : VA `0x18228C770`

### `rstData_t`(= `rstinit.GBWK`)の公式field名(cpp2il_cs `result2_H/rstData_t.cs`で確認、CONFIRMED)

| offset | 名前 | 型 |
|---|---|---|
| +0x4A | TargetIndex | sbyte(Native Write Guardrails対象) |
| +0x4B | PUpSkillResult | sbyte |
| +0x4C | PUpSkillIndex | sbyte |
| +0x4E | PUpSkillID | ushort |
| +0x60 | pCurrentStock | datUnitWork_t |
| +0x68 | WorkStock | datUnitWork_t |
| +0x7E | Flag | sbyte(presentation種別: 1=PowerUp, 2=Mutation, 3=失敗/なし, 4=別ケース) |
| +0x91/+0x92/+0x94/+0x98 | MotionReq系 | PRESENTATION_CONSUMER investigationの「source object」と同一構造であることが判明 |

`datUnitWork_s.flag`(+0x10, uint)・`.id`(+0x14, ushort)・`.skill`(+0x50, Int32[])も同ファイルで確認済み。

### `rstCalcSkillPowerUpCore`の全分岐(raw disassembly、VA精度で確認)

```
entry(0x18227E100)
  │
  rstRndGetPowerUpSkill(pStock, &PUpSkillIndex) → PUpSkillID
  │  PUpSkillID==0 → return 0
  │
  rstCreateBeforeSkillList(...)で除外リスト構築 → PUpSkillIDが一致 → return 0
  │
  RNGロール(pStock.id!=0なら閾値0x80@0x18227E332、==0なら閾値0x70@0x18227E396。
  │         実プレイでは id は常に非0なので閾値0x80側が実質専用)
  │  ロール失敗側(0x18227E344 "mov dil,1; jmp 0x18227e4bc")
  │    → 0x18227E3AD〜0x18227E416(promotion-check・bit6 test・16要素scan)を
  │      完全にバイパスして直接Mutation-attemptマージブロックへ
  │  ロール成功側(dil=0, 0x18227E34C/0x18227E39C)
  │    → promotion-check: cmbChkSkillOwner(PUpSkillID, pStock) [VA 0x18227E3D2]
  │       結果>=0(所持している)なら dil を1へ昇格
  │    → bit6 test(VA 0x18227E40C: test byte[pCurrentStock+0x10],0x40)
  │       SET → 即return 0(0x18227e5a9)
  │       CLEAR → 16要素scanループ(TARGET+0xb8->[0]->+0x58の配列、8バイトstride、
  │               各要素+0x10のbyteが bit0 set & bit2 clear & bit1 set の場合のみ
  │               そのbit6をrsiへOR集約)
  │
  ▼
Mutation-attemptマージブロック(0x18227E4BC〜)
  rsi!=0 または dil==1 のいずれかで到達
    pCurrentStock.skill[PUpSkillIndex] を取得
    cmbGetMutationSkill(skillValue, pStock) [VA 0x18227E577]
      戻り値0 → return 3
      戻り値非0 → PUpSkillID=戻り値、return 2  ★ genuine Mutation
  rsi==0 かつ dil==0 の場合のみ
    or [pCurrentStock+0x10], 0x40(bit6セット, VA 0x18227E4E8)
    return 1  ★ ordinary Skill Power-Up
```

呼び出し元`rstCalc`は戻り値(AL)を無条件で`GBWK.PUpSkillResult`(+0x4B)へ書き込む。`rstCalc`内に**2つの独立したcall site**が存在する(`0x18227E9C9`と`0x18227F0A1`)。どちらがどの条件で選ばれるかは未確定(下記UNRESOLVED参照)。

`rstUpdateSeqSkillPowerUp`(次フレーム側)は`PUpSkillResult`を読み、`==1`と`==2`でほぼ同一の適用処理(`rstOverWriteSkill`→`rstSetMotionReqFromTbl(2)`→`SetMotionGiftFlag`→`rstInitSkillAct`)を行うが、末尾で`GBWK.Flag`に1または2を書き込む点だけが異なる。その後`PUpSkillResult`を0xFF(-1)へリセットする。

### 両方候補時の優先順位(CONFIRMED)

「Power-Up候補とMutation候補を別々に計算して比較する」設計ではない。単一のRNGロール結果(`dil`)と16要素scan結果(`rsi`)が同じ状態変数群を共有し、**Mutation化条件(RNGロール失敗 OR 16-scanヒット OR 既知skill昇格)のいずれかが真ならMutation、全て偽の場合だけPower-Up**という一方向の排他分岐。「どちらが先か」という設計ではなく、Power-Upは「Mutation化しなかった残余」。

### 旧Patch B/Cの正体(CONFIRMED)

旧`Patch B`(VA `0x18227E34C`)・`Patch C`(VA `0x18227E39C`)は、上記2つのRNGロールそれぞれの「成功時`dil=0`」代入命令(`xor dil,dil`、vanilla bytes `40 32 FF`)そのもの。RNGの確率自体は変えず、「ロール成功側をPower-Up方向からMutation方向へ倒す」効果だけを持つ。

---

## Runtime telemetry実装状況(build/deploy済み、read-only)

`src/SkillMutationV3/PowerUpMutationCfgDiagnostics.cs`(新規、既存ファイル無変更)に以下3クラスを追加済み:

- `V3CfgCoreBoundaryPatch`(`rstCalcSkillPowerUpCore` Prefix/Postfix): `eventStart`/`seqCurrent`/`pCurrentStockPtr`/`pCurrentStockFlagRaw`+bit6/`pCurrentStockId`/`pUpSkillIndex`/`pUpSkillID`/(postfixのみ)`coreResult`
- `V3CfgRndPowerUpSkillPatch`(`rstRndGetPowerUpSkill` Postfix): `result`/`pIdx`
- `V3CfgSkillOwnerPatch`(`cmbChkSkillOwner` Prefix/Postfix): `skillID`/`result`/`isPromotionCandidate`(=`skillID==PUpSkillID`での相関キー)

ログタグ: `V3-CFG-CORE` / `V3-CFG-RNDPOWERUP` / `V3-CFG-SKILLOWNER`(既存タグと衝突なし)。`SeqInfo.Current==8`のハードフィルタは使用していない(unit=59/60フィルタのみ)。

**build/deploy済み**:
- `dotnet build -c Release` 0エラー
- deploy先: `C:\Program Files (x86)\Steam\steamapps\common\smt3hd\Mods\NocturneModernGameplay.dll`
- SHA256(source/deploy一致): `845c74a92f60267020b85343fbdd880b140e9767c4ea28260b578c27ab095d6c`
- Git commit/pushは未実施

## 実機テスト結果(2026-09-02実施)

`Latest.log`より`V3-CFG-CORE`が**12件(6回のPrefix/Postfix対)**、`seqCurrent=9`で発火(`seqCurrent=8`は0件)。

### 判明した事象(CONFIRMED、2026-09-02): 前回セッションの「Core patch 0件発火」の原因

前回セッション(`MUTATION_CHANCE_GATE`)の`V3SkillPowerUpCoreBoundaryPatch`が発火しなかったのは、Harmony patch未アタッチではなく**`SeqInfo.Current==8`というハードフィルタの値が誤っていた**(実際は`seq=9`)ことが原因と判明。Harmony自体は正常に動作している。

### 実測6件

| frame | unit | bit6(prefix) | 昇格判定 | coreResult | D(cmbGetMutationSkill) |
|---|---|---|---|---|---|
| 4911 | 59 | True | -1 | **2** | 発火 |
| 7199 | 60 | False | 5 | 2 | 発火 |
| 11930 | 59 | True | -1 | **0** | 不発火 |
| 12999 | 60 | False | -1 | 2 | 発火 |
| 17032 | 59 | True | -1 | **2** | 発火 |
| 18605 | 60 | False | 5 | 2 | 発火 |

`isPromotionCandidate=True`フィルタは、frame=4911の生ログを手動検証し正しく機能していることを確認済み(同フレーム中の`rstCreateBeforeSkillList`由来の無関係な8件のcmbChkSkillOwner呼び出しから、真のpromotion-checkを正しく分離できている)。

**この結論は2026-09-03のA/B/C/D hardware breakpoint調査でCONFIRMEDに反証された(下記「RESOLVED」セクション参照)。`isPromotionCandidate=True`は`rstCreateBeforeSkillList`内部呼び出しでもfalse positiveになり得ることがruntimeで直接実証されており、上記「正しく機能している」という当時の手動検証結論は信頼できない。**

---

## RESOLVED(2026-09-03): bit6=True観測なのにMutation成立する「矛盾」の決着

frame=4911/17032で観測された「Core Prefix/Postfix両方でbit6=Trueなのにcoreresult=2(Mutation成立)」という矛盾は、2026-09-03のA/B/C/D hardware breakpoint実測により、**矛盾ではなく既知CFG通りの挙動である可能性が最も高い**と判断する。矛盾の前提だった「promotion-check(dil=0側)を必ず通過している」という判定自体が、信頼できない相関ヒューリスティックに基づいていたことが直接実証された。

### 静的解析で確定した消去法(2026-09-02セッション、旧仮説名。下記「追加したdiagnostic」節のA/B/C/D breakpoint地点とは無関係な別命名)

- **旧仮説①(オブジェクト取り違え)は棄却**: `0x18227E40C`直前の`rax`データフローを命令単位で再確認。RIP相対アドレスを実際に計算し(`0x182e464b8`=real GBWK)、Core関数entry直後の最初のGBWK参照と完全一致することを2箇所で確認。オフセットチェーン(`GBWK→+0x60→pCurrentStock→+0x10`)も`rstData_t`公式field名と一致。
- **旧仮説②(Core自身の直接コールグラフ内での書き込み)は棄却**: `rstRndGetPowerUpSkill`実体(thunk追跡後`0x1965489A0`)・その内部の`cmbGetPowerUpSkill`実体(`0x196539430`)・`rstCreateBeforeSkillList`(`0x182280460`)・`cmbChkSkillOwner`実体(`0x182410660`、~75バイト)のいずれにも、pCurrentStock+0x10への書き込みは存在しない。
- **旧仮説③(bit6を通らないbypass経路は今回のケースには非該当)という当時の判定は撤回**: 当時「frame=4911/17032は`isPromotionCandidate=True`が観測されている=dil=0側でしか到達できない`0x18227E3D2`を通過している」と判定していたが、この判定根拠(`isPromotionCandidate=True`)自体が2026-09-03にfalse positiveであることが直接実証された(下記CONFIRMED参照)。したがって「この2件はbit6 testを必ず通過する経路を通っている」という結論はもはや成立せず、RNGロール失敗側のbypass経路(`0x18227E344`/`0x18227E347`)である可能性を排除できない。

### 追加したdiagnosticの目的と結果(2026-09-03、`src/SkillMutationV3/PowerUpMutationBit6RawProbe.cs`)

**目的**: `rstCalcSkillPowerUpCore`内の4地点(A=`0x18227E3D7` promotion-check call直後、B=`0x18227E40C` bit6 test、C=`0x18227E440` 16要素scan入口、D=`0x18227E4BC` Mutation-attemptマージ)にhardware breakpoint(Dr0〜Dr3)+ Vectored Exception Handlerを設置し、どのbasic blockを実際に通過したかをbasic-block単位で観測した。GameAssembly.dllへの書き込みは一切行わず、制御フロー・register・ALU flags・return値も変更していない(RFのみ再開用の技術的例外)。

**経緯**: 当初はA/B/Cが不発火でDのみ発火するcoreResult=2 invocationが多数観測され、「Dr0/Dr1/Dr2/Dr7がMutation実行中に無効化される」「hardware breakpoint instrumentation自体の信頼性問題」を主仮説として、D hit時点で`EXCEPTION_POINTERS.ContextRecord`から直接Dr0〜Dr3/Dr7を読み取る検証を追加した。結果、**Dr状態は全hitで一貫してInstall時のまま(dr7=0x455、4本ともenabled)**であり、hardware breakpoint自体は終始正常だったことが確認された(該当仮説はREJECTEDへ)。

その後、`cmbChkSkillOwner`(VA `0x182410660`)の全call site(4箇所: Core本体`0x18227E3D2`、`rstCreateBeforeSkillList`内2箇所、無関係な遠方関数1箇所)を`.text`全域xref scanで再確認し、Aの発火有無を「Core本体promotion-check通過」のground truthとしてruntimeログと突き合わせた結果、**Aが不発火のinvocationでも`V3-CFG-SKILLOWNER`の`isPromotionCandidate=True`が記録されるケースを直接確認**(2026-09-03実機テストinvocation1、frame=25191)。これにより「A/B/C不発火・Dのみ発火」は既知のRNGロール失敗bypass経路(`0x18227E344`〜`0x18227E347`)と矛盾しないことが判明した。

### CONFIRMED

- `V3-CFG-SKILLOWNER`の`isPromotionCandidate=True`は、Core本体promotion-check(`0x18227E3D2`)の一意な証拠ではない。false positiveがruntimeで直接実証された(A不発火 かつ isPromotionCandidate=True が同一invocationで同時に観測された)。
- promotion-check通過の ground truth は A = `0x18227E3D7`(`cmbChkSkillOwner` call直後、迂回不可能なreturn先)である。
- 「A/B/Cなし → D」は既知のRNG failure bypass(`0x18227E344: mov dil,1` / `0x18227E347: jmp 0x18227E4BC`)とruntimeで整合する。
- `A → B(bit6=True) → return0`をruntime確認(coreResult=0のinvocationで実測)。
- `A → B(bit6=False) → C×16 → D → return1`(ordinary Power-Up経路)をruntime確認。
- `A → B(bit6=False) → C×16 → D → return2`(RNG-success側からのgenuine Mutation経路)をruntime確認。
- hardware breakpoint A/B/C/Dは全実機テストを通じて正常に動作していた(Dr0〜Dr3/Dr7はD hit時点で常にInstall直後の値のまま)。

### REJECTED

- Core実行途中で`pCurrentStock.flag`のbit6が一時clearされる、という主仮説(旧「広義のB」)。
- Dr0/Dr1/Dr2/Dr7がMutation実行中に無効化される仮説。
- thread mismatch仮説(A/B/C/Dの全hitは単一OS threadから観測され、D hitでも同一threadだった)。
- hardware breakpoint instrumentation不具合がA/B/C欠落原因という仮説。

### STALE/HYPOTHESIS

- 過去frame 4911/17032の「promotion-check発火済みだからbit6 testを必ず通った」という解釈は、false positive可能な`isPromotionCandidate=True`に依存していたため、現在は証拠として使用しない。RNGロール失敗bypass経路だった可能性が高いが、当時A相当のinstrumentationが存在せず直接の再検証ができないため、CONFIRMEDへは昇格させない。

### 残る未解決事項(本件と独立、優先度は下げる)

- `isPromotionCandidate=True`のfalse positive源のさらに細かい特定(`rstCreateBeforeSkillList`内2 callのどちらか、あるいは`0x18240DCC7`所属関数の特定)は追跡を停止した。現時点の最終目的には不要と判断。
- Priority 5(`rstCalc`内2つのcall site `0x18227E9C9`/`0x18227F0A1`のどちらから来たか)は未解決のまま。frame=4911/11930/17032は`eventStart=0` `seqCurrent=9`で3件とも同一値であり、今回のtelemetryでも区別できていない。

---

## 次にやること(再開時の手順、2026-09-03時点で最新)

1. `00_PROJECT_RULES.md`
2. `01_CURRENT_STATE.md`
3. 本文書(`investigations/POWERUP_MUTATION_CFG/PLAN.md`)
   を順に読む。
4. bit6矛盾調査はRESOLVED(上記「RESOLVED(2026-09-03)」セクション参照)。
5. 本investigationのCONFIRMED事項を`01_CURRENT_STATE.md`(Canonical State)へ昇格するかはユーザー承認待ち。
6. Priority 5(`rstCalc`内2 call site `0x18227E9C9`/`0x18227F0A1`の識別)は低優先度UNRESOLVED。
7. `isPromotionCandidate=True`のfalse positive源の細分化(`rstCreateBeforeSkillList`内どちらのcallか、`0x18240DCC7`所属関数の特定)は追跡停止済み。

## 禁止事項(継続)

- source変更・build・deploy・Git操作・telemetry追加・Patch A/B/C変更・raw patch実装は、次回再開時に明示的な指示があるまで行わない。
- 旧Queue/Inline/Learn-As-New実装の復活。
- 実機Evidenceなしでの確率判定/gateの推測による確定。
