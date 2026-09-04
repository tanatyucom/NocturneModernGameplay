# NocturneModernGameplay Current State

## Skill Mutation V3

### CONFIRMED

- Skill Mutation V3はzero-base再実装である。
- Queue / Inline architectureは現行V3ではない。

#### Native static

- real GBWK = `0x182e464b8`
- TARGET = `0x182e4ed30`
- ACTION = `0x182e46930`

#### Runtime ownership

- real GBWK `+0x60` = `pCurrentStock`
- real GBWK `+0x68` = `WorkStock`

#### SeqInfo

- `Current` = `+0x11`

#### Result

- real GBWK `+0x4B` = `PUpSkillResult`

#### Patch A

- VA `0x18227efd0`
- vanilla: `A8 03`
- patched: `A8 00`
- outer skill-change gateを強制成功する。

#### Patch B / Patch C

- Patch B = `0x18227e34c`
- Patch C = `0x18227e39c`
- Patch B/Cは`dil=0` ordinary Power-Up側を`dil=1` Mutation側へ変換する。

#### 現在のA-only診断

- `ExperimentalDisablePatchBAndC = true`
- Patch A ON
- Patch B/C vanilla
- A-onlyでもMutation presentation欠落が発生した。
- V3 original restore OFFでもpresentation欠落が発生した。
- Mutation Logic Success / State Success / Presentation Successは分離可能である。

#### State_182e31630

- static slot = `0x182e31630`

Pointer chain:

```text
slotAddress = moduleBase + RVA
slotValue   = *(slotAddress)
statePtr    = *(slotValue + 0xb8)
```

Fields:

- `+0x04` dword
- `+0x08` dword
- `+0x0c` byte
- `+0x10` float / raw
- `+0x14` float / raw
- `+0x18` byte
- `+0x1c` dword

#### Source object

```text
[real GBWK.Pointer + 0xb8] -> [0]
```

Source fields:

- `+0x91`
- `+0x92`
- `+0x94`
- `+0x98`

以下は同一source objectを参照する。

- `0x182281b70` write先
- `SetMotionGiftFlag` read元
- `0x182280d64`系consumer read元

- source objectと`State_182e31630`は別objectである。

#### `0x182280d64`系

- hot / cold分割された単一論理関数である。
- logical rangeは`0x182280d64 - 0x18228108e`である。

#### `0x182281c30`

- presentation-related path上の長大未解析関数である。
- 約338命令である。
- presentation consumer本体とはまだ断定しない。

### STRONGLY SUPPORTED

- 現時点で追加項目なし。

### HYPOTHESIS

- presentation request / stateは生成されるが、consumer側の条件判定、party scan、またはlifecycleのどこかでpresentation実行まで到達しない場合がある。

### REJECTED

- old `+0x60 = WorkStock`
- 旧Queue / Inlineを現行V3へ戻す方針
- Patch B/Cがpresentation欠落の必要条件
- V3 original restore timingがpresentation欠落の主因
- `State_182e31630`既知field差だけがpresentation成否branch point
- source `+0x91 >= 0`ならpresentationを単純skipする
- dispatcher entry nullがsilent skip原因
- bit6がMutationそのものを直接止める、という強い仮説

### UNRESOLVED

- visible / invisibleでsource `+0x91 / +0x92 / +0x94 / +0x98`に差があるか
- party scan条件の意味
- `0x182281c30`全体の役割
- `0x1814c69b0`の意味
- `0x1821bdbe0`の意味
- `0x18202e1d0`の意味
- presentation requestのgenerate / relay / consume / reset条件
- Patch A自体がunusual ordering / lifecycleを作るか
- ordinary Power-Up等の別presentation activityがshared stateへ干渉するか
- Mutation-vs-Mutation高密度collisionはA-only結果により主因仮説として強く弱まった。ただし、ordinary Power-Up等を含むshared-state competition自体は未解決。

### NEXT

1. `ExperimentalDisableOriginalRestore = true`の条件で次回diagnosticを行う。
2. source telemetry付きbuildを使用する。
3. actual-game testを行う。
4. ChatGPTがvisible / invisible差分を抽出する。
5. 差分のみClaudeへ渡す。
6. Claudeが該当fieldのwriter / reader、`0x182280d64`系consumer、`0x182281c30`関連branchを狙い撃ち解析する。

## Power-Up / genuine Mutation 振り分けCFG

出典: `investigations/POWERUP_MUTATION_CFG/PLAN.md`(調査経緯・runtime観測の詳細はそちらを参照。ここには今後の設計判断に必要な確定事項のみを記載する)。

### CONFIRMED

#### `rstCalcSkillPowerUpCore`

- VA `0x18227E100`
- 戻り値(sbyte): `0` = 今回Skill Power-Up / Mutation適用なし、`1` = ordinary Skill Power-Up、`2` = genuine Skill Mutation、`3` = Mutation attempt failure等
- 呼び出し元`rstCalc`が戻り値(AL)を無条件で`GBWK.PUpSkillResult`(+0x4B)へ書き込む
- `rstCalc`内のCore call siteは少なくとも`0x18227E9C9`と`0x18227F0A1`の2箇所(どちらがどの条件で使用されるかはUNRESOLVED)

#### Power-Up / Mutation振り分けCFG(概略)

```text
rstRndGetPowerUpSkill
↓
候補なし / 除外条件 → return 0
↓
RNG
├─ failure側: dil=1(VA 0x18227E344)
│    → 0x18227E347 jmp 0x18227E4BC
│    → promotion-check / bit6 test / 16-scanを全てbypass
│    → genuine Mutation attemptへ直行
│
└─ success側: dil=0
     → cmbChkSkillOwner promotion-check
     → pCurrentStock.flag bit6 test
         SET → return 0
         CLEAR → 16要素scan
     → Mutation化条件成立
         → cmbGetMutationSkill
         → nativeResult != 0 → return 2
         → nativeResult == 0 → return 3
     → Mutation化条件なし
         → pCurrentStock.flag |= 0x40
         → return 1
```

Power-UpとMutationは別々に候補計算して比較する設計ではない。単一の排他分岐(Mutation化条件— RNGロール失敗 OR 16-scanヒット OR promotion-check成立 — のいずれかが真ならMutation、全て偽の場合のみPower-Up)であり、Power-Upは「Mutation化しなかった残余」として成立する。

#### genuine Mutation

- `cmbGetMutationSkill`のVA `0x18227B6B0`。Core内での呼び出しは`0x18227E577`の1箇所のみ。
- nativeResult != 0 → `PUpSkillID`を戻り値へ更新 → `return 2`。
- 今後Mutation成立判定は「`cmbGetMutationSkill`のnativeResult != 0 → coreResult=2 → application」の経路を優先する。presentation表示の有無だけでMutation成立を判断しない。

#### ordinary Skill Power-Up

- Mutation化条件が全て不成立の場合のみ成立する。
- 成立時: `pCurrentStock.flag |= 0x40`(VA `0x18227E4E8`) → `return 1`。

#### bit6

- 対象: `pCurrentStock.flag`(+0x10)の bit `0x40`。
- bit6 testのVA: `0x18227E40C`。
- RNG-success側では、bit6 SET → (`0x18227E410 jne`) → `return 0`。promotion-checkで`dil`が1へ昇格していても、bit6 SETならreturn 0となる(`dil`はbit6 testのSET分岐では参照されない)。
- ordinary Power-Up成立時のみ、Core自身がbit6をSETする。

#### runtime検証

上記static CFGは、hardware breakpoint(4地点同時観測)によるruntime basic-block通過確認で検証済み。

- `A/B/Cなし → D → coreResult=2` = RNG failure bypassと整合
- `A → B(bit6=True) → return0` = bit6 SET gateと整合
- `A → B(bit6=False) → C×16 → D → return1` = ordinary Power-Up経路
- `A → B(bit6=False) → C×16 → D → return2` = RNG-success側からのgenuine Mutation経路

(A=`0x18227E3D7`、B=`0x18227E40C`、C=`0x18227E440`、D=`0x18227E4BC`。詳細は`investigations/POWERUP_MUTATION_CFG/PLAN.md`参照)

#### `isPromotionCandidate=True`の扱い

- `V3-CFG-SKILLOWNER`の`isPromotionCandidate=True`は、Core本体promotion-check(`0x18227E3D2`)通過の一意な証拠ではない。false positiveがruntimeで直接実証済み。
- `cmbChkSkillOwner`直接call siteは少なくとも4箇所: `0x18227E3D2`(Core本体)、`0x1822805C7`/`0x182280735`(`rstCreateBeforeSkillList`内)、`0x18240DCC7`(別関数、未特定)。
- 今後Core本体promotion-check通過のground truthとして`isPromotionCandidate=True`を使用しない。A=`0x18227E3D7`のruntime通過観測をground truthとする。

#### 旧Patch B/Cの意味(精緻化)

- Patch B(`0x18227E34C`)/ Patch C(`0x18227E39C`)のvanilla bytesは`40 32 FF`(`xor dil,dil`)。
- RNGの確率自体は変更しない。RNG-success側で本来`dil=0`となる箇所をMutation方向へ倒し、genuine Mutation branch(`cmbGetMutationSkill`)へ誘導するpatchである。
- 「Power-Up結果を後からMutationに偽装するpatch」ではない。ただし前段gate等は残るため、B/Cだけでは完全なMutation Alwaysにはならない。

### STRONGLY SUPPORTED

- bit6のsemantic nameは「Skill Power-Up済み / 一回性消費フラグ」の可能性が高い(bit6 SETでreturn0となるgate、ordinary Power-Up成立時にCore自身がSETする、という2つの独立した観測が整合するため)。ただし正式field名は未確定(下記UNRESOLVED参照)。

### HYPOTHESIS

- 過去frame 4911 / 17032(`isPromotionCandidate=True`観測とともに「bit6=True観測なのにcoreResult=2」と報告されていたケース)は、RNGロール失敗bypass経路(`dil=1`、promotion-check/bit6 test自体を通過しない)だった可能性が高い。ただし当時はA相当のinstrumentationが存在せず直接の再検証ができないため、CONFIRMEDへは昇格させない。当時の「promotion-check発火済みだからbit6 testを必ず通った」という判定は、false positive可能な`isPromotionCandidate=True`に依存していたため、現在はEvidenceとして使用しない。

### REJECTED

- Core実行途中で`pCurrentStock.flag`のbit6が一時clearされる、という主仮説。
- Dr0/Dr1/Dr2/Dr7がMutation実行中に無効化される仮説。
- thread mismatch仮説(hardware breakpoint観測が別threadで行われたため一部地点が不発火だった、とする説明)。
- hardware breakpoint instrumentation不具合がA/B/C欠落原因という仮説。
- `isPromotionCandidate=True`をCore本体promotion-check通過の確定証拠として扱うこと。

### UNRESOLVED

- `rstCalc`内2つのCore call site(`0x18227E9C9` / `0x18227F0A1`)のどちらがどの条件で使用されるか(低優先度)。
- bit6の正式semantic name(該当`datUnitWork_s`フィールドの公式名)。
- `isPromotionCandidate=True`のfalse positive源の詳細(`rstCreateBeforeSkillList`内2 callのどちらか、`0x18240DCC7`所属関数の特定)。追跡は停止中で、現時点の設計判断には不要と判断されている。

## Phase A: SkillMutation / SkillPowerUp Chance native control(実装・build・deploy済み、実機テストは次回)

上記「Power-Up / genuine Mutation 振り分けCFG」のCONFIRMED事項を前提に、native制御点を新規zero-base実装した。**この節のCONFIRMEDはbuild/deploy済みという事実、および実装コードの内容についてのみ。挙動そのもの(優先順位ロジックが実機で意図通り動くか)はまだruntime未検証であり、下記UNRESOLVEDへ分離している。**

### CONFIRMED(static / build-time)

#### 実装ファイル

- `src/SkillMutationV3/NativeChancePatchUtility.cs`(共有raw patchヘルパー、zero-base、`SkillMutationAlways.cs`から独立)
- `src/SkillMutationV3/SkillMutationChanceControl.cs`(`SkillMutation.Chance`)
- `src/SkillMutationV3/SkillPowerUpChanceControl.cs`(`SkillPowerUp.Chance`、Always優先順位ロジック含む)
- `src/GameplaySettingsService.cs`(config読み書き、Single Source of Truth)
- `src/GameplayFeatureRegistry.cs` / `src/GuiMetadataBridge.cs`(3値対応)
- `src/ModMain.cs`(初期化順序: ChanceControl.Initialize → GameplaySettingsService.Load → GameplayFeatureRegistry.Initialize)
- (別リポジトリ)`NocturneModernController/src/ModernControllerApi.cs` / `NocturneModernController/settings/Program.cs`(`FeatureMetadata`/`FeatureToggleRequest`への`AllowedValues`/`Value`追加、既存boolean featureとの後方互換維持)

#### Mutation.Always(4箇所同時patch)

| VA | vanilla | patched |
|---|---|---|
| `0x18227EFD0` | `A8 03` | `A8 00`(outer gate) |
| `0x18227E339` | `7E 11` | `90 90` |
| `0x18227E342` | `7F 5D` | `90 90` |
| `0x18227E39C` | `40 32 FF` | `40 B7 01`(id==0側完全網羅) |

#### Mutation.Disabled(2箇所同時patch)

| VA | vanilla | patched |
|---|---|---|
| `0x18227E4BA` | `75 56` | `90 90` |
| `0x18227E4BF` | `75 47` | `90 90` |

#### PowerUp.Disabled(2箇所同時patch)

| VA | vanilla | patched |
|---|---|---|
| `0x18227E4E8` | `83 48 10 40` | `90 90 90 90` |
| `0x18227E4EC` | `B0 01` | `B0 00` |

#### PowerUp.Always(raw patchではなくHarmony Prefix/Postfix)

- `rstRndGetPowerUpSkill` Postfixで`__result`(original candidate skill ID)をcapture(Core内で唯一の呼び出し元であることをxref scanで確認済み)。
- Core自身のPrefixで`pCurrentStock.flag`のbit6を捕捉。
- Core Postfix: `bit6WasSet`ならびに`__result`に応じ、
  - `bit6WasSet`: 何もしない(Mutation.Chance Native/Always/Disabledの結果をそのまま尊重)。
  - `bit6 CLEAR`かつ`__result∈{2,3}`: `GBWK.PUpSkillID`をcapture済みoriginal candidateへ復元し、`pCurrentStock.flag |= 0x40`、`__result = 1`。
  - `bit6 CLEAR`かつ`__result∈{0,1}`: 無変更。
- `Mutation.Chance`がNative/Alwaysいずれでも同一ロジックが適用される(「bit6 CLEARならPower-Up優先」はMutation設定に依存しない)。

#### config

- `NocturneModernGameplay.settings.json`(DLLと同じディレクトリ、新設ファイル。既存ルート`settings.json`は無関係のまま未使用)。
- `Chance`: `Disabled` / `Native` / `Always`。`Repeat`: `Native`のみ正式対応、`Unlimited`等はfail-safeで`Native`へ。

#### runtime退役状態

- 旧`SkillMutationAlways`(Patch A/B/C): `Initialize()`未呼び出し・`GameplayFeatureRegistry`未登録により実質無効化(source無変更のまま保持)。
- `RepeatableSkillPowerUp`: `Enabled = false`。
- `PowerUpMutationCfgDiagnostics`: `Enabled = false`(default OFF)。
- `PowerUpMutationBit6RawProbe`: 上記default OFFに連動し、通常起動ではhardware breakpoint/VEHをinstallしない。

#### build/deploy(このセッション最終時点)

- NocturneModernGameplay: build成功、error 0、warning 1(`RepeatableSkillPowerUp.cs`既存warning、今回変更と無関係)。
- NocturneModernController(本体) / NocturneModernController.Settings: build成功、error 0、warning 0。
- SHA256(source/deploy一致確認済み):
  - `NocturneModernGameplay.dll` = `6db1198f02b1facc92e11e6add0f79a4008b5d5db36d1391ec9e296c55d1f3f2`
  - `NocturneModernController.dll` = `aae5043a27317aa2843be7cd52828ee64aa5a2f086d421cf2eca96f6df60812a`
  - `NocturneModernController.Settings.exe` = `b6808b38d54190d21d58a9687628546731a8a6195393b348fb3c184cc0440c15`
  - `NocturneModernController.Settings.dll` = `16ff899889dece0bb07e61c91a6633eed6ca71df31331567bdfbeb4380ed6cc8`

### UNRESOLVED(実機未検証、次回最優先)

- Power-Up 100% / Mutation 100%で、未Power-Up→Power-Up、Power-Up済み→Mutation 100%となるか。
- Power-Up 100% / Mutation Nativeで、未Power-UpはPower-Up優先、Power-Up済みはMutation Nativeとなるか。
- Power-Up 0% / Mutation 100%でMutationのみ発生するか。
- ModernController GUIで両Chanceが0%/通常/100%として表示・保存・反映されるか。
- ModernController無しで`NocturneModernGameplay.settings.json`のみで動作するか。

### 実機診断で確定した事項(2026-09-04、Frost unit=60、SkillMutation.Chance=Native / SkillPowerUp.Chance=Always)

同一save・同一Frost(unit=60)・同一level-up条件・同一設定の下、Power-Up成立試行と不発試行の両方を**同一セッション内**で観測した(1回目成立/2回目不発)。以下はこの1個体・この2試行というobserved pathに基づく記録であり、他個体・他条件への一般化は反例探索前提のSTRONGLY SUPPORTEDまでに留める(下記参照)。

#### CONFIRMED(実機)

- 両試行とも`SeqInfo.Current: 6→7→8→21→22→8`まで完全に同一に進行した(`22→8`遷移は本節と`REPEAT_UNLIMITED/PLAN.md`で独立に複数回再現済み)。
- 成立試行: `seq 8→10`へ進行。`rstRndGetPowerUpSkill`到達(`result=4 pIdx=4`)、`rstCalcSkillPowerUpCore`到達(`coreResult=2`)、直後に`SkillPowerUpChance Always priority applied`発火(`restoredSkillId=4`)、`__result`が`1`へ変換されユーザー実機でordinary Power-Up成立を確認。
- 不発試行: `seq 8→19`へ直行。`rstRndGetPowerUpSkill`・`rstCalcSkillPowerUpCore`とも未到達。`SkillPowerUpChance Always priority applied`不発、ユーザー実機でPower-Up不成立を確認。
- 両試行ともGATE1(`TARGET(0x182e4ed30)+0xb8->[0]->+0x58->array->count(+0x18)->array[0]+0x24`)= `value24=15`、`gate1Pass=True`で完全に同一。
- 両試行とも`pCurrentStock.flag`のbit6 = CLEAR(`False`)で完全に同一。
- **現行`SkillPowerUp.Always`の実際の保証範囲はここまでの実機観測で以下の通り確定する**:
  - **Core-entry guarantee = NO**(Coreへ到達するかどうか自体は保証しない)
  - **Core到達後のpriority conversion = YES**(到達さえすれば、bit6 CLEARかつ`__result∈{2,3}`のとき正しくordinary Power-Upへ変換される。今回の成立試行が直接の裏付け)
- `rstcalc.rstCalc`内`0x18227F012`(`mov dword ptr [rcx+0x1c], edx`、直前に`PUpSkillResult`を読み直した値がedx)が`State_182e31630+0x1C`の書き込み元であること。CONFIRMED(static disassembly、byte-exact)。`REPEAT_UNLIMITED/PLAN.md`のUNRESOLVED#1(writer未発見)は、この書き込みが**リテラル`1`ではなくPUpSkillResultのレジスタ値**であったため、旧セッションの「リテラル1書き込みパターン」検索でヒットしなかったと考えられる。runtime上でState+0x1Cの値自体を直接観測してはいない(下記UNRESOLVED参照、static disassemblyのみのCONFIRMED)。

#### STRONGLY SUPPORTED(反例未探索、CONFIRMEDへは未昇格)

- 成立/不発を分けるpre-Core確率制御点は、`rstcalc.rstCalc`内`0x18227EFD0`(`test al, 3`、Patch Aと同一VA)のroll分岐であると考えられる。
  - 根拠: (1) static CFG上、GATE1通過後・Core呼び出し前に位置する条件分岐であること、(2) `SkillMutationChanceControl`の`Mutation.Always`が同一VAを`A8 03→A8 00`に書き換えて強制成功させる設計であること、(3) `SkillPowerUpChanceControl`はこのVAを一切書き換えていないこと、(4) 今回の実機結果(GATE1・bit6が両試行で同一)が、このrollの成否のみで説明可能であること。
  - **CONFIRMEDへ昇格しない理由**: roll自体の戻り値(AL)をruntimeで直接読み取っていない(生命令へのraw/inline hookは今回のinvestigation方針で禁止)。GATE1・GATE2B以外に未発見の分岐が関与している可能性も理論上残る。

#### REJECTED

- 「Default Skill習得level(seq8→21→22経由)ではSkill Power-Up/Mutation(seq9/10)が排他的に発生しない」という仮説(前回セッションで提起、Canonical化前に撤回済み)。**理由**: 同一save・同一level・同一seq経路(`6→7→8→21→22→8`)から、1回目は`seq8→10`(Core到達・成立)、2回目は`seq8→19`(Core未到達・不発)という異なる分岐が実際に観測された。デフォルトスキル習得の有無がPower-Up/Mutationを固定的に排他するという一般化はこの反例で成立しない。

#### UNRESOLVED

- GATE2B(`source+0x60`が指すobjectの+0x14)のruntime値。今回2試行とも`source+0x60`自体がnullで、GATE2B分岐そのものを実測できなかった(nullになる理由は未解明)。
- `0x1821690d0`(roll関数)の戻り値そのもののruntime直接観測。
- `State_182e31630+0x1C`の実際の値がPUpSkillResultと連動して変化する様子のruntime直接観測(staticなwriter特定のみで、runtime cross-validationはまだ)。
- Core call site 1(`0x18227E9C9`、dil==1側)とcall site 2(`0x18227F0A1`、GATE1/roll側)がそれぞれどのような条件で使い分けられるかの全体像。

**一般化ガード**: 本項のCONFIRMED/STRONGLY SUPPORTEDは、unit=60(Frost)の同一save上の2試行というobserved pathに基づく。「`SkillPowerUp.Always`は常にCore-entryを保証しない」という一般則自体は、他個体・他レベル・他save条件での反例探索を経てから確定表現へ格上げする。

### 追加static finding: `Mutation.Disabled`のdil==1経路によるbit6バイパス(2026-09-04)

- CONFIRMED(static disassembly、`.analysis/disasm_mutation_disabled_bit6.py`)、**runtime未確認**: `rstCalcSkillPowerUpCore`のRNG-bypass経路(native `dil=1`、VA `0x18227E344`→`0x18227E347`の`jmp 0x18227E4BC`)は、bit6 test(`0x18227E40C`)を一切経由せずmerge block(`0x18227E4BC`/`0x18227E4BF`)へ到達する。`SkillMutation.Chance=Disabled`の既存パッチ(DEntry2、VA`0x18227E4BF`)がこの分岐をNOP化しているため、bit6の実際の状態(SET/CLEARいずれでも)に関わらず、この経路を通った回はordinary Power-Up成立処理(`0x18227E4C1`: `bit6|=0x40; return 1`)へ到達し得る。
- この現象は`SkillPowerUp.Chance`の設定値に非依存(`Mutation.Chance=Disabled`が有効である限り発生し得る)。
- **Repeat=Nativeが実際に破られるか(bit6 SET済みユニットでこの経路経由の再Power-Upがruntimeで実際に観測されるか)は、guard発火または再Power-Upのruntime観測までCONFIRMEDへ昇格しない。**

### Shared Outer Gate 実装チェックポイント(2026-09-04、PC shutdown保全のためのcommit。runtime検証は未実施)

**実装済み(static / build-time CONFIRMED、runtime未検証)**:

- `src/SkillMutationV3/SharedSkillChangeOuterGateControl.cs`(新規): `0x18227EFD0`の単一owner。`forcePass = SkillMutationChanceControl.Mode==Always || SkillPowerUpChanceControl.Mode==Always`。`SkillMutationChanceControl`から`OuterGate`のSite定義を完全に除去済み(このVAへの二重書き込み経路は物理的に存在しない)。
- `src/SkillMutationV3/MutationDisabledBit6Guard.cs`(新規): 上記`Mutation.Disabled`のbit6バイパス問題に対する是正。`rstCalcSkillPowerUpCore`のPrefixでCore進入前のbit6を捕捉し、Postfixで`SkillMutationChanceControl.Mode==Disabled && bit6WasSetBeforeCore==true && __result==1`のときのみ`__result=0`へ補正する。`__result`以外への書き込みなし。
- 両`ChanceControl`の`SetMode`に、`SharedSkillChangeOuterGateControl.IsResolved==false`のとき`Always`遷移を拒否するfail-safeガードを追加(`Mode`を変更しない、partial Always状態を作らない)。
- `ModMain.cs`の初期化順序を`SharedSkillChangeOuterGateControl.Initialize()` → `SkillMutationChanceControl.Initialize()` → `SkillPowerUpChanceControl.Initialize()`へ変更。
- Clean build(error 0)・deploy・SHA256一致確認済み: `4085523e20d245556d91bce5c40543687077e9d1b0bf759f06a4985f281c88e6`。

**UNRESOLVED(実機未検証、次回再開時の最優先)**:

- 実機テストA〜E(下記NEXT参照)は**未実施**。この実装が意図通り動作することは、いずれのケースもまだruntimeで確認されていない。
- `0x18227EFD0`がpre-Coreの確率ゲートであることは引き続きSTRONGLY SUPPORTEDのまま(CONFIRMEDへ未昇格)。今回の実装はこの前提に基づくが、実装自体の正しさとは独立に、この前提自体もまだruntime AL直接観測では裏付けられていない。
- `MutationDisabledBit6Guard`が実際にnative `dil=1`を引いた試行で発火・補正することは、上記「追加static finding」の通りruntime未確認。

### NEXT

1. **実機テストA〜E(最優先、未実施)**:
   - A: `Mutation=Native / PowerUp=Always`、bit6 clear、Frost同一saveで複数回 → 毎回Core到達・毎回ordinary Power-Up成立を期待。
   - B: `Mutation=Disabled / PowerUp=Always`、bit6 clear → ordinary Power-Up成立を期待。
   - C: 同一個体をbit6 SET後にもう一度level-up、`Mutation=Disabled / PowerUp=Always / Repeat=Native` → ordinary Power-Up再成立なしを期待。`MutationDisabledBit6Guard`の`bit6WasSetBeforeCore=true, originalResult=1, correctedResult=0`が観測できればBlocking Issue #1のruntime裏付けとなる。
   - D: `Mutation=Always / PowerUp=Native` → 既存Mutation.Always回帰確認。
   - E: `Mutation=Always / PowerUp=Always`、Repeat=Nativeで bit6 clear→Power-Up優先・bit6 set→Mutation を確認。
2. `[SkillPowerUp] Repeat = Unlimited`をzero-baseで別investigationとして開始する。目標: Power-Upのみ繰り返し可能にし、Mutation.Chanceへ副作用を出さない。VEH/hardware breakpoint常駐、code caveのいずれも正式機能としては現時点で不採用方針を維持し、安全なnative制御点を再調査する。
3. Repeat=Unlimited実装後のみ、Power-Up 100%⇔Mutation 100%の自動排他制御(最後に変更した側を優先し反対側を0%へ)を追加する。Repeat=Nativeでは両方100%の共存を許可し続ける。

## Phase B: Repeat=Unlimited / Acquisition Mode 解析(実装前、zero-base)

`investigations/`配下の新規investigationとして継続中。本節はこのフェーズで新たにCONFIRMEDとなった事項のみを記録する。STRONGLY SUPPORTED / HYPOTHESIS / UNRESOLVEDの詳細分類は各investigation文書側で管理し、ここへは昇格させない。

### CONFIRMED(static、ISIL解析)

#### `rstUpdateSeqSkillPowerUp`内のbit6 CLEAR writer

- `rstUpdateSeqSkillPowerUp`(VA `0x18228C770`)内に、`pCurrentStock+0x10`のbit6(`0x40`)をclearする命令列が**2箇所**存在する(byte-level disassembly確認済み、`GameAssembly.dll`をpefile+capstoneでオフライン読み取り)。
- 2箇所とも、`GBWK.Flag`(+0x7E)が`3`または`4`であり、かつ`State_182e31630+0x1C == 1`のときにのみ到達する分岐上にある。
- Power-Up/Mutation成立側(`GBWK.Flag`が`1`または`2`)の分岐には、このCLEAR命令は存在しない。
- **Site1(Flag==4側)**: VA `0x18228CCB2`、bytes `83 60 10 BF`、命令`and dword ptr [rax+0x10], 0xFFFFFFBF`(32bit)。State+0x1C testは`0x18228CC9F`。State+0x1Cのリセットなし。
- **Site2(Flag==3側)**: VA `0x18228CD89`、bytes `83 60 10 BF`、命令は同上。State+0x1C testは`0x18228CD60`。直後`0x18228CDAA`で`State+0x1C = 0`へリセット。
- `State_182e31630+0x1C`のreaderは上記2箇所のみ。writer(`=0`)は`0x18228CDAA`のみ。writer(`=1`)は`rstcalc`/`rstCalcCore`/`rstupdate`/`rstinit`の悉皆確認では未発見(UNRESOLVED)。

#### `rstMotionReq`(managed identity)

- 01_CURRENT_STATE.md旧記載の「presentation-related path上の長大未解析関数」の正体は`rstMotionReq`であることを確認した(本体VA範囲`0x182280d64`-`0x18228108e`、cpp2il ISIL cross-reference + static disassembly)。この範囲内から`0x182281C30`を2箇所(call site VA `0x182280FE3` / `0x18228102B`)呼び出し、source object `+0x91`(byte)・`+0x94`(float)を引数として渡している。

#### `0x182281C30` = `rstSmoothMotion`(CONFIRMED、訂正)

- `0x182281C30`の正体は`rstMotionReq`本体ではなく、`rstMotionReq`が上記2箇所から呼び出す`Il2Cpp.rstcalc.rstSmoothMotion(ref dds3ModelHandle_t aHandle, int aGroup, int aNumber, float aBeforeLeng, float aSmoothLeng) : int`である。
- 識別根拠: `global-metadata.dat` + `GameAssembly.dll`の`CodeGenModule.methodPointers`テーブル解決、`cpp2il`のISILによるcross-check、および`rstMotionReq`からのcall-site(`0x182280FE3`/`0x18228102B`)対応確認。
- signature引数対応・呼び出し文脈の詳細は`investigations/PRESENTATION_CONSUMER/PLAN.md`参照。

**REJECTED / corrected**: 旧記載「`0x182281C30` = `rstMotionReq`」は誤りと判明したため訂正する。`0x182281C30`は`rstMotionReq`本体ではなく、その呼び出し先`rstSmoothMotion`である。`rstMotionReq`自身の識別は上記の本体VA範囲(`0x182280d64`-`0x18228108e`)を正とする。

#### `rstOverWriteSkill`

- `rstupdate.rstOverWriteSkill(ref Int32 pSkill, UInt16 NewSkillID)`(managed signature、ISIL確認)の内部処理は実質`*pSkill = NewSkillID`という単純な参照書き込みのみである。
- 対象slot(`pSkill`が指すアドレス)の選択は`rstOverWriteSkill`内部では行われない。呼び出し元側の責務である。

#### PUpSkillIndex writer

- `GBWK.PUpSkillIndex`(+0x4C)の唯一のwriterは`rstRndGetPowerUpSkill`実体(VA `0x1965489A0`)内の`0x196548B15: mov byte ptr [r12], al`である(byte-exact確認)。
- `rstCalcSkillPowerUpCore`(VA `0x18227E100`)自身はPUpSkillIndexを読むだけで、writeしない。
- 生成algorithm: 所持skillを動的count(`pStock+0x48`、固定8ではない)までscan → `skillID==0`は空きslotのsentinelとしてskip → `cmbGetPowerUpSkill(skillID)`が非0を返すもののみcandidate化 → RNGでcandidateを1つ選択 → 選ばれた**既存skillのslot index**をPUpSkillIndexへwrite。**空きslot選択の経路はこの関数に存在しない。**

#### `rstAddSkill` / `rstReplaceSkill` 実VAとcaller

- `rstAddSkill`(VA `0x182285A40`)は`rstUpdateSeqDefaultSkill`(VA `0x182288790`)内の2箇所からのみ呼ばれる。`rstUpdateSeqSkillPowerUp`(Power-Up/Mutation側)からは呼ばれない。
- `rstReplaceSkill`(VA `0x1822860E0`)は、実行可能セクション全域のdirect-call xrefスキャンで呼び出し元が見つからなかった(indirect call経由の可能性は残る、UNRESOLVED)。
- `rstOverWriteSkill`実VA: `0x182285FF0`。`rstChkSkillAct`実VA: `0x182285B50`(DefaultSkill/SkillPowerUp両方が呼ぶ共通ゲート)。

#### seq dispatch table

- `rstupdate.rstUpdate(dds3ProcessID_t PID)`(VA `0x18228CDE0`)内に、`SeqInfo.Current`値で分岐する25エントリのjump table(table VA `0x18228D5F4`)を確認した。byte-level decodeによる対応関係:
  - seq `8` → `rstUpdateSeqDefaultSkill`
  - seq `10` → `rstUpdateSeqSkillPowerUp`
  - seq `21` → `rstUpdateSeqDestroySkill`(VA `0x1822890D0`)
  - seq `22` → `rstUpdateSeqDestroyConfirm`(VA `0x182288B20`)
