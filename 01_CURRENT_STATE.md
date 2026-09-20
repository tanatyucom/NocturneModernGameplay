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

#### bit6 gateとcandidate selectionの順序(2026-09-05追加、CONFIRMED static)

- Core内部の呼び出し順序は、Core entry → `0x18227E15A`(`rstRndGetPowerUpSkill`呼び出し) → `GBWK.PUpSkillIndex` / `PUpSkillID`確定 → bit6 test(`0x18227E40C`)である。
- bit6 SETによる`return 0`は、candidate selection**前**に落ちる経路ではない。candidate selection**後**、native candidateが既に確定した状態で、bit6 gateにより当該candidateごと破棄される経路である。
- 詳細な設計上の含意(Repeat=Unlimited Option D/Fの評価根拠)は`investigations/REPEAT_UNLIMITED/PLAN.md`参照。

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

### CONFIRMED(実機、2026-09-04追加)

- Power-Up 100% / Mutation 100%(Test E)で、bit6 clear→ordinary Power-Up、bit6 set→genuine Mutationとなることをunit=60の連続2試行(frame160086→197075)で確認した。詳細は下記「実機テストA〜E結果」参照。
- Power-Up 100% / Mutation Native(Test A)で、bit6 clearユニット(unit=60)は5/5全試行でordinary Power-Up成立、Core到達欠落なしを確認した。詳細は下記参照。

### UNRESOLVED(実機未検証)

- Power-Up 0% / Mutation 100%でMutationのみ発生するか(今回のTest A〜Eでは未検証)。
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

### `Mutation.Disabled`のdil==1経路によるbit6バイパス(2026-09-04 static発見、2026-09-04 runtime確認)

- CONFIRMED(static disassembly、`.analysis/disasm_mutation_disabled_bit6.py`): `rstCalcSkillPowerUpCore`のRNG-bypass経路(native `dil=1`、VA `0x18227E344`→`0x18227E347`の`jmp 0x18227E4BC`)は、bit6 test(`0x18227E40C`)を一切経由せずmerge block(`0x18227E4BC`/`0x18227E4BF`)へ到達する。`SkillMutation.Chance=Disabled`の既存パッチ(DEntry2、VA`0x18227E4BF`)がこの分岐をNOP化しているため、bit6の実際の状態(SET/CLEARいずれでも)に関わらず、この経路を通った回はordinary Power-Up成立処理(`0x18227E4C1`: `bit6|=0x40; return 1`)へ到達し得る。
- **CONFIRMED runtime(2026-09-04、実機テストC)**: `bit6WasSetBeforeCore=True`の状態で`originalResult=1`が実際に発生し、`MutationDisabledBit6Guard`が`correctedResult=0 / corrected=True`へ補正したことを直接観測した(unit=59, frame=58062, 16:40:26.190)。
- **STRONGLY SUPPORTED / runtime corroborated**: このruntime事象がstatic CFGで特定したdil=1 RNG-bypass経路に対応すること。確認済みstatic CFG上では、bit6 SET状態から`originalResult=1`へ到達する他経路は確認されていない。ただしdilレジスタ/branch自体はruntime直接観測していない。詳細は下記「実機テストA〜E結果」のTest C参照。
- この現象は`SkillPowerUp.Chance`の設定値に非依存(`Mutation.Chance=Disabled`が有効である限り発生し得る)。

### Shared Outer Gate 実装チェックポイント(2026-09-04実装、2026-09-04実機テストA〜E完了)

**実装済み(static / build-time CONFIRMED、実機テストA〜Eでruntime検証済み)**:

- `src/SkillMutationV3/SharedSkillChangeOuterGateControl.cs`(新規): `0x18227EFD0`の単一owner。`forcePass = SkillMutationChanceControl.Mode==Always || SkillPowerUpChanceControl.Mode==Always`。`SkillMutationChanceControl`から`OuterGate`のSite定義を完全に除去済み(このVAへの二重書き込み経路は物理的に存在しない)。
- `src/SkillMutationV3/MutationDisabledBit6Guard.cs`(新規): 上記`Mutation.Disabled`のbit6バイパス問題に対する是正。`rstCalcSkillPowerUpCore`のPrefixでCore進入前のbit6を捕捉し、Postfixで`SkillMutationChanceControl.Mode==Disabled && bit6WasSetBeforeCore==true && __result==1`のときのみ`__result=0`へ補正する。`__result`以外への書き込みなし。
- 両`ChanceControl`の`SetMode`に、`SharedSkillChangeOuterGateControl.IsResolved==false`のとき`Always`遷移を拒否するfail-safeガードを追加(`Mode`を変更しない、partial Always状態を作らない)。
- `ModMain.cs`の初期化順序を`SharedSkillChangeOuterGateControl.Initialize()` → `SkillMutationChanceControl.Initialize()` → `SkillPowerUpChanceControl.Initialize()`へ変更。
- Clean build(error 0)・deploy・SHA256一致確認済み: `4085523e20d245556d91bce5c40543687077e9d1b0bf759f06a4985f281c88e6`。
- **実機テストA〜E(2026-09-04)完了、全PASS。** 詳細は下記「実機テストA〜E結果」参照。

**残存UNRESOLVED**:

- `0x18227EFD0`がpre-Coreの確率ゲートであることは引き続きSTRONGLY SUPPORTEDのまま(CONFIRMEDへ未昇格)。今回の実機テストでもAL直接runtime観測は行っていない。
- native bit6 CLEAR siteの通常プレイでの発火タイミング(新規、下記参照。旧「bit6 persistence inconsistency」はTest A/Eのreload手順差によるconfoundと判明したため撤回・再定義済み)。
- seqCurrent=9の意味論(新規、下記参照)。

### 実機テストA〜E結果(2026-09-04、Shared Outer Gate / MutationDisabledBit6Guard検証)

ログ出典: MelonLoader `Latest.log`(2026-09-04 16:33:25〜16:59:20)、`PowerUpMutationCfgDiagnostics`対象unit=59/60。モード切替ログを境界に5フェーズへ機械的に切り分けた。

#### CONFIRMED runtime

- **Test A**(Mutation=Native/PowerUp=Always): outer gate `forcePass=True bytes=A8 00`。10/10試行でCore到達(不発ゼロ)。bit6 clearユニット(unit=60)は5/5全試行でordinary Power-Up成立(Always priority conversion経由)。bit6 SETユニット(unit=59)は設計通り結果を尊重(4回不発+1回genuine Mutation、RNG-bypass経路由来)。
- **Test B**(Mutation=Disabled/PowerUp=Always、bit6 clear): unit=60, frame=59017。Core到達、native自身がresult=1(補正不要、corrected=False)、bit6が正しくSET(flagRaw 0x3→0x43)。ordinary Power-Up成立。
- **Test C**(bit6 SET、Mutation=Disabled/PowerUp=Always、Repeat=Native): 2種の経路をruntimeで直接確認。
  - unit=60(Bから継続、frame=113154、約6分後): bit6 SET→native自身が`return 0`(通常のbit6 testルート、dil=0側)、再Power-Up不成立。
  - unit=59(同フェーズ、frame=58062): `MutationDisabledBit6Guard; bit6WasSetBeforeCore=True originalResult=1 correctedResult=0 corrected=True`。RNG-bypass経路がbit6無視で`originalResult=1`を返し、Guardが`0`へ補正したことをruntimeで確認。dilレジスタ自体は非観測。
- **Test D**(Mutation=Always/PowerUp=Native): 6/6試行(unit=59×3, unit=60×3)全てcoreResult=2。bit6状態(unit59=常時SET、unit60=常時CLEAR)に非依存でMutation成立、回帰なし。
- **Test E**(Mutation=Always/PowerUp=Always、reloadなし・同一個体継続): unit=60の連続2試行(frame160086→197075、約4.5分間隔)で「bit6 clear→ordinary Power-Up(Always priority適用、restoredSkillId=299)→bit6 set→genuine Mutation(coreResult=2)」の順序をruntimeで直接確認。unit=59(bit6常時SET)も同フェーズでMutation尊重を確認。
- **Test E由来の確定事実**: reloadなし・同一unit継続の条件下で、ordinary Power-Up成立によりSETされたbit6が、次回level-up(約4.5分後)まで保持されることを観測した(inv21→inv22)。
- Shared Outer Gateのforce論理(`forcePass = mutationMode==Always || powerUpMode==Always`)は全21回のモード遷移ログで一致、二重書き込み経路なし。

#### REJECTED / TEST-PROCEDURE CONFOUND(2026-09-04、ユーザー指摘により訂正)

- **Test AとTest Eのbit6 persistence比較**: Test Aはユーザーが同一saveを試行ごとにreloadして複数回実施しており、Test Eはreloadせず同一個体を連続level-upさせていた。この手順差により、Test Aの各試行間でbit6がFalseに見えたのは「native CLEARが発火した」ためではなく、「Power-Up前のsave状態(bit6 clear)がreloadで復元された」ことで説明可能であり、unit lifetimeを跨いだnative CLEAR挙動の直接比較には使えない。旧記載の「bit6 persistence inconsistency」(Test A/E比較に基づくUNRESOLVED)は撤回する。

#### UNRESOLVED(新規、2026-09-04)

- **native bit6 CLEAR siteの通常プレイでの発火タイミング**(Test A/E比較とは切り離した別件): `rstUpdateSeqSkillPowerUp`内のbit6 CLEAR機構(Flag∈{3,4}×State+0x1C==1条件、VA `0x18228CCB2`/`0x18228CD89`)が実際のゲームプレイ中いつ発火するかは、reload混在のTest Aデータでは検証できていない。reloadなし条件での複数試行によるnative CLEAR発火の直接観測が必要。Repeat=Unlimited設計に影響し得るため引き続き優先調査。
- **seqCurrent=9の意味論**: 今回のTest A〜E全22 invocationで、`rstCalcSkillPowerUpCore`呼び出し時の`GBWK.SeqInfo.Current`は例外なく`9`だった。既存のseq dispatch table記載(`seq10→rstUpdateSeqSkillPowerUp`)との関係(rstCalc内Core呼び出しとrstUpdate側seq遷移の時系列関係)は未整理。

### 実機診断: reloadなし5 level-up観測(2026-09-04、SkillMutation.Chance=Native / SkillPowerUp.Chance=Always、EXP×10診断helper使用)

ログ出典: 同日のMelonLoader `Latest.log`(18:03:38.989〜18:08:44.533、対象unit=59/60)。reloadを挟まずFrost(unit=60)を連続level-upさせた回。EXP-MULTIPLIERログは戦闘機会の時系列特定にのみ使用し、bit6/State意味論の根拠としては使用していない。

#### CONFIRMED static

- `State_182e31630+0x1C`は、この観測全体を通じて常に同一の固定static slot address(`0x182e31630`)である(unit・invocationを問わず同一アドレス)。

#### CONFIRMED runtime

- **State+0x1C cross-unit overwrite**: unit=60でState+0x1C=1を観測した後(frame=29722)、unit=59のCore呼び出し(frame=33720、coreResult=0)が発生し、既知writer(`rstcalc.rstCalc`内`0x18227F012`)がState+0x1C=0を書き込んだ。次にunit=60を観測した時点(frame=34262)ではState+0x1C=0になっていた。この間、target unit(59/60)のbit6はいずれも一貫してSETのままで、Site2(bit6 clearを伴うリセット、VA`0x18228CD89`/`0x18228CDAA`)は発火していない。
  - **一般化ガード**: これは1 observed pathである。「すべてのunit・すべてのケースで必ずこの意味(=他unitに上書きされる)になる」とは一般化しない。今回確認できたのは、少なくとも1回、unit=59のCore呼び出しがunit=60の残していたState+0x1C値を上書きした、という事実のみである。
- reloadなし・unit=60・6 Core invocations(observed path)でV3-BIT6-CLEARは0件(前回記録と同一区間の再確認)。

#### REJECTED

- 旧仮説「State+0x1Cを0へ戻す未知の別writerが存在する可能性が高い」は撤回する。理由: 今回の1→0遷移は、既知writer(`0x18227F012`)+別unit(59)のCore呼び出し(coreResult=0)だけで完全に説明でき、新規・未知のwriterを仮定する必要がない。
- 「diagnostic上の重複(dedup bug)」「pCurrentStock誤帰属」「単一level-up内での近接重複Core呼び出し」は、6 Core invocationsと5 user-perceived level-upsの差分の説明としていずれも棄却する。6サイクルは27〜59秒間隔で明確に時間分離しており、`V3-CFG-CORE`は全件`pCurrentStockId=60`を一貫して報告し、`RstCalcState1CDiagnostics`のdedupキーは`seq`を含むため取りこぼしによる重複生成は起こり得ない。

#### UNRESOLVED

- **5 user-perceived level-ups vs 6 unit=60 Core invocations**: 原因未確定。以下2つがHYPOTHESISとして残るが、いずれも確定させない。
  - user count漏れ(6サイクルのうち1件をユーザーがカウントし損ねた)。
  - cycle4(frame=43495起点、seq6→8→**21→22**というforget-skill経路。他5サイクルのseq6→8→10→11→13という通常power-up経路と異なる)が、他5サイクルと異なる意味を持つ(=ユーザーが「level up」として認識した事象と一致しない可能性)。
- native bit6 CLEAR siteの通常プレイでの発火タイミング(継続、未観測のまま)。

#### Repeat=Unlimited設計への注意(重要な設計制約)

- **State+0x1C(`State_182e31630+0x1C`)はunit-localなrepeat履歴値として直接利用してはならない。** 別unitのCore呼び出しによって上書きされる共有(グローバル)stateであることがCONFIRMED runtimeで確認されたため、特定unitのPower-Up成立履歴判定にState+0x1C単独では使えない。Repeat=Unlimitedの設計では、unit固有の履歴はpCurrentStock自体の状態(bit6等)から判定し、State+0x1Cをunit識別の代替に使わないこと。

### NEXT

1. 5 user level-ups / 6 Core cyclesの差分整理、特にcycle4(seq21→22のforget-skill経路)の意味整理。**Repeat=Unlimited設計そのものへの直接的な必須条件ではない**(Repeat=Unlimitedが必要とするのはbit6 CLEAR条件とState+0x1Cの意味論であり、user側のカウント精度はそれ自体を左右しない)が、今後のobserved path解釈の信頼性(「何サイクル観測できたか」の正確な把握)に関わるため優先度1とする。
2. native bit6 CLEAR siteの通常プレイでの発火タイミング調査(`rstUpdateSeqSkillPowerUp`のFlag値・State+0x1C挙動を試行単位でログ化する。reloadを挟まない複数試行で行い、Test Aで生じたreload confoundを再発させない)。
3. seqCurrent=9とseq dispatch table(seq8/10/21/22)の時系列関係の追加cross-validation(observed pathを増やす)。
4. `[SkillPowerUp] Repeat = Unlimited`をzero-baseで別investigationとして開始する。目標: Power-Upのみ繰り返し可能にし、Mutation.Chanceへ副作用を出さない。VEH/hardware breakpoint常駐、code caveのいずれも正式機能としては現時点で不採用方針を維持し、安全なnative制御点を再調査する。**State+0x1Cはunit-local判定に使わない(上記設計制約参照)。**
5. Repeat=Unlimited実装後のみ、Power-Up 100%⇔Mutation 100%の自動排他制御(最後に変更した側を優先し反対側を0%へ)を追加する。Repeat=Nativeでは両方100%の共存を許可し続ける。

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

### 再開: Mutation AddNew native lifecycle(2026-09-15、`investigations/ACQUISITION_LEARNASNEW/PLAN.md`)

本investigationはHidden New Skill Entry解決後、User方針により新規investigationを立てずV3 zero-base調査として再開した(旧Queue/Inline/Learn-As-New実装はhistorical evidenceとしてread-only参照のみ、production採用は現行binary/runtimeで再検証してから判断する)。**まだPoC実装には進んでいない。static解析のみ。** 詳細・byte-exact evidenceは`investigations/ACQUISITION_LEARNASNEW/PLAN.md`を正とする。

#### CONFIRMED(`rstUpdateSeqSkillPowerUp`全体逆アセンブル、byte-exact)

- `GBWK.PUpSkillResult`(+0x4B)による分岐は`1`(ordinary)/`2`(mutation)で**ほぼ完全に対称なnativeコード**を通る。両方とも同一managed method`rstupdate.rstOverWriteSkill`(VA `0x182285FF0`)を呼び、`&pCurrentStock.skill[PUpSkillIndex]`へ`GBWK.PUpSkillID`(+0x4E)を書き込む。この`rstOverWriteSkill`呼び出しが上書き不可逆化の唯一の地点(呼び出し前は読み取り専用処理のみ)。
- 呼び出し直後の3つのpresentation call(`0x182281a80`/`0x18227c080`/`0x182280a50`)はordinary/mutation共通、その後`GBWK.Flag`(+0x7E)へ`1`(ordinary)/`2`(mutation)を書き込み、最後に`PUpSkillResult=0xFF`(消費マーク)を書いて共通tailへ収束する。
- **訂正**: `FullCapacityAddNewBridgePoc.cs`の旧コメント「`GBWK.Flag = 1 (ordinary) or 4 (mutation)`」は誤りと判明。正しくは`1`/`2`。`Flag==4`は別の早期bail分岐(`PUpSkillResult`チェックより前のゲート、`0x18216a490`の戻り値が1のケース)で書かれる値であり、Mutationの意味ではない(前述bit6 CLEAR writerのSite1条件`Flag==4`とは別文脈、混同注意)。
- `rstOverWriteSkill`はPower-Up AddNewで既に実戦投入済みの`FullCapacityOverwriteSuppressor`の介入点と同一managed methodであり、Mutation呼び出しサイトも構造的には同じ機構で捕捉可能(現状Trigger側のガードが`PUpSkillResult==1`のみのためMutationには未作用)。

#### CONFIRMED(`rstCalcSkillPowerUpCore`前半逆アセンブル、byte-exact)

- `GBWK.PUpSkillIndex`(+0x4C)は関数冒頭、ordinary/mutation分岐(dil)より**前**に1回だけ確定し、両分岐で共有される(mutation分岐側の再write箇所なし)。`GBWK.PUpSkillID`(+0x4E)も同時に「ordinary候補値」で初期化される。
- Mutation成立時(`cmbGetMutationSkill`、VA `0x18227B6B0`のnativeResult!=0)は、VA `0x18227E597`で`GBWK.PUpSkillID`を Mutation targetへ**上書き**する。`PUpSkillIndex`は再writeされない。
- **構造的帰結**: Mutationは独立したslotを選ぶのではなく、常に「既に有効なordinary Power-Up候補が存在するslot」の上で成立する(`PUpSkillID==0`ならmutation分岐へ到達する前にcoreResult=0で即return)。
- `0x1822810b0`(PUpSkillIndex/初期PUpSkillIDを設定する関数)は`rstRndGetPowerUpSkill`(VA `0x1965489A0`)へのwrapper/entryである可能性が高いが**未確定**(アドレス帯が別領域のため)。断定しない。

#### 未着手(次回最優先)

1. `rstAddSkill`内部ヘルパー(`0x18240DEB0`/`0x1824104E0`/`0x182410C60`)のbyte-exact解析。
2. Mutationのoverwrite確定(`rstOverWriteSkill`呼び出し)前に、native `rstAddSkill`パイプラインへ合法的に合流できる制御点があるかの評価。
3. seq21/22で`DefSkillResult`/`EventParam`等をPower-Up AddNewと同じ意味で使えるかの確認。
4. seq22→8復帰時のinternal write確認(旧UNRESOLVED、継続)。
5. `HiddenSlotCandidateInjection.cs`がMutation経由でもそのまま機能するかはUNKNOWN(未検証、backend成立後に確認)。

## Phase C: Option F result conversion — 初回実機成功(2026-09-12)

Phase Bの調査(R0-A/R0-B/R0-Cのnative CFG、`investigations/REPEAT_UNLIMITED/PLAN.md`)を前提に、人間承認を得てOption F result conversionの初回candidate実装(`src/SkillMutationV3/OptionFRepeatUnlimitedControl.cs`)を実施し、`SkillMutation.Chance=Native / SkillPowerUp.Chance=Always / SkillPowerUp.Repeat=Unlimited`の設定で実機投入した。**詳細な生ログ・disassembly根拠は`investigations/REPEAT_UNLIMITED/PLAN.md`「Option F result conversion 初回実機成功(2026-09-12)」を正とする。ここには今後の設計判断に必要な確定事項の要約のみを記載する。**

### CONFIRMED runtime observed(2026-09-12、1セッション分)

- R0-C(`rstCalcSkillPowerUpCore`のbit6ゲートによるrawResult==0)を`__result=1`(ordinary Power-Up成功相当)へ変換するOption Fの初回実機成功を2件観測した:
  - `unit=59`(今回セッション上の対応: ハイピクシー)、`pUpSkillID=39:"メディア"`。
  - `unit=60`(今回セッション上の対応: ジャックフロスト。ユーザー実機確認済み)、`pUpSkillID=16:"マハジオ"`。
  - いずれも変換直前の`V3-OPTIONF-CANDIDATE-POOL`スナップショットで対象skillが未所持だったことを確認しており、既所持skillへの無意味な再変換ではない。
- R0-B(native exclusion一致によるrawResult==0)がOption Fによって誤って変換されない保護を1件観測した: `unit=136`(今回セッション上の対応: モウリョウ)、`pUpSkillID=22:"マハザン"`、`action=NOT_CONVERTED`。
- 上記3件は、`__result`を一切書き換えない独立診断(`OptionFF2Diagnostics`)側でも同一のnative raw classification(`rawResult=0`、対応する`exclusionMatched`値)が一致することを確認した。
- **Option Fとは無関係のnative自身の挙動として**、ジャックフロスト(`unit=60`)で「既に`マハブフ`を所持した状態で`ブフ->マハブフ`のSkill Power-Upが成立し、`マハブフ`が2枠並ぶ」ことをユーザー実機目視で確認した(CONFIRMED runtime observed、この観測ケースについて)。少なくとも今回観測したジャックフロストのブフ→マハブフケースでは、target skillを既に所持していてもnative Skill Power-Upが成立し、同一skill IDの重複所持が発生した(「native Skill Power-Up全般が常に既所持targetを許可する」とは一般化しない)。このため**Option F側に独自のtarget既所持禁止guardは追加しない**(native semanticからの乖離を避けるため)。詳細は`investigations/REPEAT_UNLIMITED/PLAN.md`「Native Skill Power-Upの既所持target重複(2026-09-12)」参照。

### UNRESOLVED / NOT YET CONFIRMED

- production stability / universal correctness(観測件数が少なく、設定組み合わせも未網羅)。
- `SkillMutation.Chance=Disabled`と`SkillPowerUp.Repeat=Unlimited`の共存(`MutationDisabledBit6Guard`との`__result`書き込み競合。Harmony postfix実行順序が未検証のため、現行candidateは`Mode==Disabled`時に防御的skipする設計。技術的に不可能とは断定していない)。
- `unit=59`/`unit=60`/`unit=136`の仲魔対応(ハイピクシー/ジャックフロスト/モウリョウ)は今回のセッション上の観測にすぎず、`datUnitWork_s.id`の普遍的semanticとしては引き続きUNRESOLVED。

### READY FOR PRODUCTION: NO

## Diagnostics: SaveLoadDiagnostics(reload boundary marker)

- 実装: `src/Diagnostics/SaveLoadDiagnostics.cs`(read-only diagnostic、GUI/settings.jsonなし、Gameplay機能未登録)。
- Hook: `slMain.slLoadProc_closeFile`のHarmony Postfixを無条件発火させ、`V3-SAVE-LOAD; type=LoadComplete; frame=...`をログ出力する。

### CONFIRMED runtime

- 1セッション内でLoad-menuからのLoadを3回(タイトル画面から1回、field復帰後に2回)実行し、`type=LoadComplete`マーカーが3件(frame 1332 / 2097 / 3203)、1:1で観測された。
- 同セッション内のSave-only操作1回では追加マーカー0件だった(false positiveなし)。

### 検証範囲(重要、この診断を他investigationで再利用する際の前提)

- **検証済み**: Load-menu Load 3/3一致、Save-only false positive 0/1。
- **未検証**: Continue/resume-from-suspend経由のLoad(静的解析では`slContinueYesNoProc`→`slLoadData`直接callという別経路の可能性が高く、`slLoadProc_closeFile`を通らない可能性がある)、New Game、複数ゲームセッション(プロセス再起動)をまたぐLoad、gameplayを挟まない連続Load。
- 「1 Load = 1 marker」は上記検証範囲内でのCONFIRMED runtimeであり、あらゆるLoad経路への一般化ではない。

## Hidden New Skill Entry(master-archive.md Section 22継続、2026-09-13〜14)

`investigations/HIDDEN_SKILL_ENTRY/PLAN.md`に詳細を移した(調査経緯はそちらを参照。ここには今後の設計判断に必要な確定事項のみ記載)。

### CONFIRMED

- `cmpDrawSkillList`/`cmpSkillNameCostDraw`/`cmpMisc.cmpMakeStrCol`はforget UIの描画経路ではない(Harmony patch適用済み・seqゲート無し無条件カウンタでも0件、実機目視でリスト表示は確認済み)。旧来の「native "cmp" static関数がpresentationを担う」という前提はREJECTED。
- 実描画は Unity側の `statusUI` MonoBehaviour(Assembly-CSharp, global namespace、GameObject `Canvas_UI/campUIBase/statusUI(Clone)`)が担っている。`TextMeshProUGUI[] obtainedText` / `GameObject[] skillCurObj`(16要素)等のフィールドを持つ。
- `skillCurObj[i].activeSelf`は`obtainedText[i]`の`<material="TMC21">`タグと完全に相関する(21サンプル中不一致ゼロ)。選択ハイライトの実体としてSTRONGLY SUPPORTED。
- フロストの forget フロー中、Learn-As-Newで習得したスキルの表示に差し替えられた行(`obtainedText[7]`)だけ、観測された全サンプルで`skillCurObj[7].activeSelf`が一度も`True`にならなかった。ユーザーの実機目視確認(「ハイライト枠は見えない」)と一致。**master-archive.md Section 22の現象がV3 zero-base移行後も再現することが確定した。**
- `statusUI`自身のIL2CPPメタデータ上のmanagedメソッドは`Awake`/`OnDisable`/`.ctor`の3つのみ。`obtainedText[i]`差し替えや`skillCurObj[i]`のON/OFFは別クラスが外部からpublicフィールドを直接操作して行っている。
- **根本原因(2026-09-14、CONFIRMED — static disassembly + 独立した2系統のruntime計測 + 実機症状が一致、User承認済み)**: hidden entry滞在時、`CursorPos.Index=0`/`CursorPos.Shift=8`となり、ハイライト判定用の`target`(`=Shift+Index`)は`8`になる。一方、ハイライト描画側ループ(VA `0x1822D97C0`、`cmpUpdate.cmpSetupObject`(VA `0x182620A80`)の呼び出し元)は`ebx=0..7`(`loopUpper=8`)しか回らないため、`target == ebx`が一度も成立せず、`cmpSetupObject(skillCurObj[ebx], true)`がどの行に対しても呼ばれない。その結果、論理選択・説明文表示・決定操作は機能するが、選択ハイライトだけが表示されない。
  - `cmpSetupObject`が`skillCurObj[i].SetActive`の直接ラッパーであることをhardware breakpointで確認済み(`SkillCurObjNativeCallerProbe.cs`)。
  - ハイライトON判定の実体(`target == ebx`のゲート、VA `0x1822D9AFD`)を別のhardware breakpointで直接計測済み(`HighlightTargetGateTrace.cs`)。hidden entry滞在中、`target=8; loopUpper=8; ebx=0〜7全てmatch=False`を実機ログで確認。
  - 同時刻の`HIDDEN-SLOT-ARRAY-CHECK`ログでも、cursor=8(=hidden entry位置)の瞬間に`selectSkillID`が所持8スキル配列外の値(pending new skillのID)を指すことを確認済み。ただしこの`selectSkillID`解決自体は**native自身ではなく、`AddNewHighlightCorrection.cs`(このMOD自身のコード、legacy実装の再現)がAddNewブリッジ有効時のみ行っているもの**であり、native自身の挙動として確認されたわけではない(2026-09-14訂正)。
  - 「所持8スキルとは別の9番目の特殊スロット」という当初モデルは配列構造としては誤りだった(2026-09-14 PLAN.mdモデル再修正参照)が、カーソルの論理位置としては実質的に「9番目相当(target=8)」を指しているため、現象としては一貫する。
- **`CursorPos.Shift=8`のwriter(2026-09-14、CONFIRMED)**: `CursorPosShiftWriteWatchTrace.cs`(hardware write breakpoint)で特定。native自身のforget flow開始時(seq=8、bridgeActive=False)にVA `0x182288AC1`(`mov byte ptr [rcx+0x14], 8`)で無条件に書き込まれる。AddNewブリッジ経路(seq=21、bridgeActive=True)では、入力方向コードに応じて分岐する別関数(`cmpUpdateSkillSelect`と推定)内のVA `0x182289313`(`mov byte ptr [rax+0x14], 8`)で条件付きに書き込まれる。通常のカーソル移動(0〜7の増減)は別のwriter(VA `0x1822EDE25`ほか)が担当し、こちらは`8`を生成しない。
- **スコープ確定(2026-09-14、CONFIRMED、User訂正済み)**: nativeの通常Power-Upは上書き方式であるため、`Shift=8`の状態を「9番目の選択肢」としてプレイヤーに操作させる状況が発生しない。一方AddNewブリッジでは、その内部状態(`Shift=8`)をユーザー操作可能なUIまで持ち込んでしまうため、通常のハイライト描画ループ(`ebx=0..7`)との不整合が可視化される。**したがって本現象はAddNewブリッジ経路に固有**であり、native自身の`Shift=8`書き込みは(それ自体が「単なる副産物」と断定はできないが)実害のある可視状態には到達しない。
- **`target==8`専用presentation pathの実在(2026-09-14、CONFIRMED)**: `SkillCurObjNativeCallerProbe`の実機ログで、通常行0〜7の`cmpSetupObject(true)`呼び出しは全て`0x1822D9C6B`に収束する一方、**index=8だけは別のcaller(`0x1822DA5FB`)から`value=True`が観測された**。静的解析(VA `0x1822DA3FB`〜)の結果、ゲーム側に`target==8`(`cmp ecx,8`、VA `0x1822DA483`)専用の名前解決・描画・`cmpSetupObject(skillCurObj[8],true)`呼び出し経路が実在することを確認した。
- **High Pixie成功 vs Frost失敗の比較(2026-09-14、CONFIRMED)**: `Hidden9thSlotPathTrace.cs`で比較したところ、High Pixieでは`target==8`専用pathが繰り返し発火し(`skillCurObj[8]`が`active=True`/`inHierarchy=True`になり`await2_01`もハイライト表示)、Frostでは`CursorPos.Shift=8`が維持されているにもかかわらず`BRANCH-ENTRY`が0件だった(`skillCurObj[8]`は`active=False`のまま)。さらに`Hidden9thGateCascadeTrace.cs`による計測では、Frostのhidden UI表示中、`0x1822DA3FC`(専用path内のカスケード開始点と推定)以降のcheckpointがほぼ発火しなかった。**Case A/B切り分け完了(2026-09-14、static disassembly、CONFIRMED)**: IL2CPPメタデータの`methodPointers`全件走査により、`0x1822DA3FC`は独立した別関数ではなく`cmpDrawStatus.cmpDrawSkill`本体(エントリ`0x1822D97C0`)の内部(エントリから`0xC3C`バイト)であることを確認した。同関数の`ret`+int3padding走査でも領域内に関数境界は無し。`cmpDrawSkill`は既知の`0x1822D9AFD`(`target==ebx`ハイライトゲート、Frost hidden entry滞在中もruntimeで発火確認済み)と同一関数のため、**Case A確定**(関数自体は毎フレーム呼ばれている。`0x1822D9AFD`〜`0x1822DA3FC`間のどこかにpre-gateがあり、そこでFrostのみ経路が外れる)。Case B(関数自体が呼ばれていない)は棄却。詳細・新規ゲート候補(`bpl==8`/`sil==1`ゲート、出自未確定)は`investigations/HIDDEN_SKILL_ENTRY/PLAN.md`参照。

- **呼び出し元チェーン全解析・分岐点を`fclChkMessage`まで特定(2026-09-14、static disassembly、CONFIRMED)**: `cmpDrawSkill`呼び出し元を`cmpDrawStatusComEx2`→`cmpDrawStatusComEx`→`rstDrawSeqDestroySkill`→`rstDraw`(switch dispatch)まで遡り、`bpl==8`ゲート(`0x1822DA3D8`)が最終的に`fclMisc.fclChkMessage(0)`(VA`0x182169960`、実名確認済み)の戻り値のみで決まることを確認した。`0x18138BE10`は`return(cl!=0)`のみの単純bool正規化thunkで分岐要因ではない。`seq21`(通常選択中)でも`fclChkMessage(0)!=0`なら実カーソルインデックスが`0xFF`へ強制され、hidden entry専用presentation pathへ到達できない。詳細・runtime観測計画は`investigations/HIDDEN_SKILL_ENTRY/PLAN.md`参照。

- **`fclChkMessage`仮説REJECTED(2026-09-14、実機runtime、CONFIRMED)**: `FclChkMessageResultTrace`による実機観測で、`unit=59`(High Pixie想定)・`unit=60`(Frost想定)とも`bridgeActive=True`中の`fclChkMessageResult`が例外なく`0`(非suppress)だった(計421ヒット)。両者で差が無いため、既存CONFIRMED runtime(Frostのみ`target==8`専用pathに到達しない)と矛盾し、`fclChkMessage`ゲートはFrost失敗の直接原因ではないと結論する。詳細は`investigations/HIDDEN_SKILL_ENTRY/PLAN.md`参照。

- **決定的な分岐点をCONFIRMED(2026-09-14、実機runtime)**: `cmpDrawSkill`内の候補スキャンループ入口`[r13+0x10]`(loop bound)を実機観測した結果、`unit=59`(High Pixie想定、242ヒット)は`1`または`2`、`unit=60`(Frost想定、238ヒット)は例外なく`0`だった。このループは`loopBound<=0`で即座に全体をスキップする構造(static disassembly既確認)のため、Frostは`target==8`専用presentation path(`0x1822DA483`)に到達する候補スキャンループ自体に一度も入れない。**これが「Frostだけhidden entryハイライトが表示されない」の直接原因(native側分岐点)としてCONFIRMEDした。** `r13`の実体・`+0x10`カウントのwriterは未特定。詳細は`investigations/HIDDEN_SKILL_ENTRY/PLAN.md`参照。

### UNRESOLVED

- `r13`が指すオブジェクトの実体と、その`+0x10`カウントのwriter。**注意(User訂正、2026-09-14)**: 今回確定している差はあくまで「High Pixie(AddNewブリッジ、`bridgeActive=True`) = 1/2」対「Frost(AddNewブリッジ、`bridgeActive=True`) = 0」であり、native自身の通常forget flow(`bridgeActive=False`)との比較は行っていない。「native=1以上、ブリッジ=0」という解釈は今回のデータからは言えないため撤回する。次回runtime最優先タスク(`R13FetchAndWriteWatchTrace`、実装・build・deploy済み)。
- `CursorPos.InvisibleListNums=0`だった観測の意味(名称からは「隠しエントリ数」を示唆するが、hidden entry発生時でも0だった)。stale値の可能性を含め未検証。
- await系(`awaitObj`/`awaitText`、息吹の具足のケース)とdirect obtained系(`obtainedText[7]`直接差し替え、会心のケース)が同じ`target==ebx`不一致で説明できるかは未検証。
- 最終的な修正方法(Case A/Bの切り分け結果を踏まえてから検討。「ゲームが本来持つ9番目表示経路をAddNewブリッジから正しく使う」案と、「描画ループ側にtarget==8の特別処理を追加する」案の両方が候補)。

### RESOLVED(2026-09-15) — Power-Up AddNew: READY FOR PRODUCTION = YES

上記UNRESOLVEDの`r13`実体(`rstcalc.rstCreateBeforeSkillList`が返す`rstSkillInfo_t`、cpp2il型定義で確認)・そのwriter(`rstCreateBeforeSkillList`自身)を特定し、根本原因を確定した。詳細・byte-exact evidenceは`investigations/HIDDEN_SKILL_ENTRY/PLAN.md`を正とする。ここには設計判断に必要な要約のみ記載する。

- **根本原因**: `cmpDrawSkill`の9番目(hidden entry)presentation blockは、`rstSkillInfo_t.SkillCnt`(+0x10)が0の場合、その入口で丸ごとスキップされる(単なる配列境界ではなくmaster gate)。`SkillCnt`は`rstCreateBeforeSkillList`が毎フレーム0へリセットした後、pStock(`datUnitWork_t`)のcurriculum配列を24候補scanし、3ゲート(tag一致/レベル閾値/所持済みでない)を通過したものだけ追加する。
- **High Pixieがなぜ成功していたか**: `datUnitWork_t.hensinmae`(変身前種族、+0x88)が非0の変化悪魔だったため、レベル閾値の高い"今後習得予定"側のcurriculumグループ(tag=6)を参照でき、候補が1〜2件残っていた。runtime確認: `stockId=59; stockLevel=13; stockSkillCnt=8; stockHensinmae=61`。
- **Frostがなぜ失敗していたか**: `hensinmae==0`の通常個体のため、レベル閾値が現在レベル以下しかない"消化済み"側のcurriculumグループ(tag=1)しか参照できず、3ゲートのうちレベル閾値ゲートで24候補全滅していた(gate1/gate3は問題なし、runtime確認済み)。runtime確認: `stockId=60; stockLevel=11; stockSkillCnt=8; stockHensinmae=0`。**`hensinmae`はMODから一切改変していない。**
- **`rstSkillInfo_t`構造(cpp2il型定義+byte-exact disassembly、両面確認)**: `SkillCnt`(sbyte,+0x10)/`TargetLevel`(byte[],+0x18、`cmpDrawSkill`からは不読と確認済み)/`SkillID`(ushort[],+0x20、target==8専用pathは常に`SkillID[0]`のみを読む)。両配列ともLength=24で常に非null(Frostの`SkillCnt=0`時でも)。
- **修正方式(2026-09-15当初)**: `src/SkillMutationV3/HiddenSlotCandidateInjection.cs`(Harmony Postfix on `rstcalc.rstCreateBeforeSkillList`)。当初はnative自身が`SkillCnt=0`のまま終わったとき、かつAddNewブリッジがActiveかつseq==21のときのみ、`SkillID[0]`/`TargetLevel[0]`/`SkillCnt=1`を後から供給する設計だった。
- **修正方式(2026-09-15同日、追加修正)**: 上記の`SkillCnt==0`限定ガードは、hensinmae!=0のunit(native自身が既に別候補を見つけている場合、例: High Pixie)でMutation/Power-Up AddNewのtargetがハイライトされないバグを引き起こすことが判明(native自身の候補がブリッジのtargetを覆い隠すため)。**ガードを撤廃し、AddNewブリッジがActiveな間は`SkillID[0]`を常にブリッジのtargetで上書きする方式に変更**(`cmpDrawSkill`はindex 0のみを読むためCONFIRMED済み)。`SkillCnt`はnativeが既に1以上を書いていればそのまま尊重し、0のときのみ1へ引き上げる。`hensinmae`・curriculum構築・native writerはいずれも未改変。
- **実機確認(2026-09-15)**: Frost×target=299「会心」、Frost×target=16「マハジオ」(Power-Up AddNew、初期修正時点)。追加修正後: High Pixie(hensinmae!=0)でMutation AddNew(target=32:ムド等)・Power-Up AddNew(target=39:メディア等)双方でハイライト・名前表示が正常化したことをrun time確認(`HIDDENSLOT-INJECT`ログの`unit=59`出現、および実機目視)。キャンセル→別episode再突入でstate leak無し。既存の成功ケース(Frost、High Pixie旧経路)への回帰無し。warning/errorともセッション通算0件。
- **既知の限定事項(UNKNOWN、production運用上のブロッカーではない)**: `TargetLevel`のプレースホルダ値(`0`固定)は`cmpDrawSkill`からの不読は確認済みだが、他の未特定consumerが存在しないかは完全証明ではない。

### READY FOR PRODUCTION: YES(Power-Up AddNew。2026-09-15 User確認済み。同日の追加ハイライト修正込み)

## Phase D: Mutation AddNew(2026-09-15、PRODUCTION CANDIDATE、runtime validated)

詳細・byte-exact evidence・設計比較は`investigations/ACQUISITION_LEARNASNEW/PLAN.md`を正とする。ここには設計判断・引き継ぎに必要な確定事項の要約のみ記載する。

### 状態: PRODUCTION CANDIDATE(runtime validated、正式production昇格はUser最終承認待ち)

採用設計は候補B(Power-Up AddNew本体ロジックは変更せず、同一native patternをMutation専用bridge/stateとしてzero-base並行実装。相互排他guardのみPower-Up側Triggerへ最小追加)。

### CONFIRMED(byte-exact static + runtime)

- `GBWK.EventParam`(+0x32、`rstData_t`)のwriterは`rstcalc.rstCalcEventInfo`(VA `0x18227C330`、実write`0x18227C5FB`)。`rstCalc`内のretry loopから呼ばれる。第二writerは全`.text`スキャンで見つからず。
- `rstChkAddSkill`の戻り値↔`DefSkillResult`(0=owned/1=空きあり/2=満杯)は`fclCombineCalcCore.cmbChkSkillOwner`ベースで、Power-Up側と共通のprimitiveを使用。
- `rstAddSkill`の3ヘルパー: `0x18240DEB0=cmbAddSkill`、`0x1824104E0=cmbChkKeisyoSkillOwner`、`0x182410C60=cmbDeleteKeisyoSkill`(IL2CPPメタデータVA解決でCONFIRMED)。
- `rstUpdateSeqDestroyConfirm`(seq22)は自身の内部でskill[]圧縮→`skillcnt--`→**`rstAddSkill()`を直接呼ぶ**(VA `0x182288F69`、2つ目のcall site)。Mutation AddNewの満杯側はこの経路に合流する。
- Calc→Updateのtiming: 空きありは同一frame内で完結。満杯側はforget UI滞在中(観測値で最大600フレーム超/約10秒)、`EventParam`/`DefSkillResult`がGBWK上に保持され続ける。
- `DefSkillResult`はMutation bridgeから新規writeしていない(seq21/22の完了経路自体がこのfieldを参照しないため、native tail完走+タイミング遅延redirectという設計であれば不要)。
- `Flag`/`PUpSkillResult`もMutation bridgeから一切触れない(native自身の`rstUpdateSeqSkillPowerUp` tailが確定させ、bridgeのredirectはその後に発生)。
- 実機確認(複数unit・複数target): 満杯8枠AddNew成功(unit=59 High Pixie: target=60/22/115/4/32、unit=60 Frost: target=210/409等)、空きスロットAddNew成功(unit=103: target=400)。source skill保持・target習得・skillcnt整合・`EventParam`のnative本来値への復元、いずれも確認済み。
- Hidden 9th slot presentation(Phase Cの`HiddenSlotCandidateInjection.cs`)はMutation bridge Activeも認識するよう拡張済み(`FullCapacityAddNewBridgeState.Active`とのOR条件)。hensinmae!=0(High Pixie)でも正常表示確認済み。
- Ordinary Power-Up AddNewへの回帰無し(Mutation実装後も`FULLCAP-ADDNEW-COMPLETE`成功を複数回確認)。

### 既知gap(Power-Up本番実装、今回は未修正)

`FullCapacityAddNewBridgePoc.cs`の満杯側bridgeは、forget UIをCANCELした場合に`GBWK.EventParam`をnative本来の値へ復元する処理が無い(全ソースgrepでCONFIRMED)。Mutation側は最初からCOMPLETE/CANCEL両方で明示的にrestoreする設計にしたため、この既知gapは持ち込んでいない。Power-Up側は現状変更していない。

### UNRESOLVED / 未実施

- forget-skill選択UIは通常操作でキャンセルできない(User実機確認、「そのまま忘れる以外出来ない」)。CANCEL経路は通常プレイでは到達不能な可能性が高く、Mutation側の防御的cleanupコードは実質未検証のまま(production blockerにはしない)。
- `dil`(retry loop内比較値)がコンパイル時定数0であることはCONFIRMEDだが、`rstCalcEventInfo`自身のcurriculum選択ロジック内部・`rstCalc`と`rstUpdate`の呼び出しタイミング関係(indirect dispatchのため直接xref不能、static analysisの限界)は未解明。
- Mutation AddNewの正式production昇格(候補Aへの統合含む)はUser最終承認待ち。

## Phase E: SkillPowerUp.Chance=Always × Repeat=Unlimited regression(2026-09-15、FIXED、runtime confirmed)

### 発見経緯

Mutation AddNewのruntime検証中、`SkillPowerUp.Chance=Always / Repeat=Unlimited / SkillMutation.Chance=Native`という設定下で、`unit=59`(High Pixie)がMutation(native `PUpSkillResult=2`)ばかり表面化し、Ordinary Power-Up(`=1`)が一度も表面化しない事象が見つかった。当初「Mutation AddNew実装自体の間接regression」の疑いも検討したが、native自身の生`PUpSkillResult`遷移ログ(Mutation AddNewのguardより前段)を確認した結果、Mutation AddNewのguardが正当な`result=1`を握り潰している形跡は無いことが判明し(`unit=59`はnative自身が`PUpSkillResult=1`を一度も生成していなかった)、既存コードの読解により真因を特定した。

### CONFIRMED(根本原因)

`src/SkillMutationV3/SkillPowerUpChanceControl.cs`の`SkillPowerUpChanceAlwaysPatch.Postfix`に

```csharp
if (_bit6WasSet) return;
```

というコードがあり、コード自身のコメントには「Repeat=Native only path in Phase A」と明記されていたにもかかわらず、実際のコードは`GameplaySettingsService.Repeat`を一切チェックしていなかった。Phase C(`OptionFRepeatUnlimitedControl.cs`、Repeat=Unlimited追加)実装時に、Phase Aのこの条件がRepeatモードを考慮するよう更新されていなかったことが原因。`OptionFRepeatUnlimitedControl`は`rawResult==0`(R0-A/B/C)のみを変換対象とし`rawResult==2/3`には関与しないため、bit6が既にSET状態で native CFGの「RNG失敗(dil=1)→bit6を経由せず直接Mutation attempt」経路が`rawResult=2/3`を生成した場合、どちらの変換層にもカバーされずMutationがそのまま表面化していた。

### 修正

```csharp
if (_bit6WasSet && GameplaySettingsService.Repeat != "Unlimited") return;
```

Repeat=Nativeでは従来通りbit6 SET後はAlways変換をskip。Repeat=Unlimitedではbit6 SET後も2/3→1変換を継続する。`OptionFRepeatUnlimitedControl`(rawResult==0専用)とは役割が重ならないため競合しない。

### CONFIRMED runtime(2026-09-15)

`unit=59`(High Pixie)で`FORGET-PUPSKILL-CHANGE`を確認したところ、3/3イベント全てが`pUpResultAfter=1`となり、Mutationは一度も表面化しなかった。`unit=60`/`unit=103`では`SkillPowerUpChance Always priority applied`(2/3→1変換)が複数回正しく発火していることも確認。`[NocturneModernGameplay]`由来の`failed safely`ログは0件。

### READY FOR PRODUCTION: YES(runtime confirmed。2026-09-15)

## Phase F: Option F / Repeat=Unlimitedの独立した残課題(2026-09-15、UNRESOLVED、保留)

Phase Eの修正とは別レイヤーの問題。`OptionFRepeatUnlimitedControl.cs`(raw `rawResult==0`→1変換)で、`OPTION-F-DECISION`ログの`exclusionObserved`フィールドが実機セッション全体を通じて常に`False`になっており、`bit6WasSet=True`のケースが一貫して`action=ABORTED_INCONSISTENT_OBSERVER`となる事象を観測した(`unit=59`/`unit=60`両方、Phase Eの修正とは独立に発生)。

一時診断ログ(`OPTIONF-DIAG-EXCLUSION-HIT`/`OPTIONF-DIAG-TOTAL-CALLS`、`OptionFRepeatUnlimitedControl.cs`に追加済み)を仕込んだが、Phase Eの修正が本命症状を解決したため、この診断ログ自体はまだ活用(実機データ収集)していない。

**次回優先調査**: `investigations/REPEAT_UNLIMITED/PLAN.md`側で独立調査する。`rstCreateBeforeSkillList`が`rstCalcSkillPowerUpCore`のCore-active windowの中で本当に呼ばれているか(2026-09-12時点では呼ばれていた、CONFIRMED runtime observed実績あり)を、上記診断ログで再確認するのが起点。

### READY FOR PRODUCTION: N/A(production blockerではない、保留中の別機能)

## Hardware breakpoint禁止事項(2026-09-15、重要)

**`GBWK.EventParam`へのhardware write-breakpoint(VEH/Dr0-Dr7)による直接観測は、2回クラッシュした(`EventParamWriterCaptureProbe`、`EventParamActualWriterTrace`)。以後、`investigations/ACQUISITION_LEARNASNEW/PLAN.md`のinvestigationではこの手法を再使用しない。再開にはUserの明示承認が必要。** 両クラスとも`Enabled=false`のまま保持(ソース削除はしない)。

## 既知の別issue(記録のみ、本investigation対象外)

### Settings GUI経由のSkillPowerUp/SkillMutation Chance設定が意図せずNativeへ戻る事象(2026-09-12観測)

Skill Mutation V3 / Repeat=Unlimited investigation(R0-B調査)とは別issueとして、事実のみを記録する。今回は調査・修正を行わない。

**観測事実(CONFIRMED runtime observed)**:
- `NocturneModernGameplay.settings.json`で`SkillPowerUp.Chance=Always`を設定していた状態から、NocturneModernController側のSettings GUI(`NocturneModernController.Settings.exe`、ゲーム内Select長押しで起動)を開いて閉じた直後、ログに以下が出力された。
  ```
  Settings GUI closed; settings reloaded.
  (約9秒後)
  SkillMutationChanceControl mode set; mode=Native.
  SkillPowerUpChanceControl mode set; mode=Native.
  ```
- この時点でディスク上の`NocturneModernGameplay.settings.json`は`SkillPowerUp.Chance="Always"`のまま変化していなかった(ファイル内容とruntime上のモードが乖離した)。

**原因**: `HYPOTHESIS`(GUI load/save処理のbug等)。今回は原因追跡・修正のいずれも行っていない。

**再現条件・影響範囲**: 未調査。

## Phase G: 1 LvUp内2回Skill Power-Up統合バグ / episode latch PoC(2026-09-17、UNRESOLVED、READY FOR PRODUCTION: NO)

### CONFIRMED(症状)

実機ログ(`smt3hd\MelonLoader\Latest.log`、17:45:57-17:46:20、`unit=92`)で、**1回のレベルアップ内でSkill Power-Upが2回成立**することを確認した。

1回目: ordinary Power-Up成立、target=メディア(39)。skillcnt=8 full capacityのためnative forget UIへ移行、プレイヤーが1スキルを忘れ、targetを新規取得(`FULLCAP-ADDNEW-COMPLETE`)。

その直後、**同一level-up episode内・新規EXP event/新規level-up eventなし**で、`rstCalcSkillPowerUpCore`が再実行され、新しい候補で2回目のordinary Power-Upが成立(target=マハザン(22))。再度forget UIへ移行した(2回目)。

`levelUpCnt`は1回目終了時点で`1->0`(正常消費)、2回目の不正ロール時点では既に`-1`/`-2`(負値)だった。

### REJECTED(訂正した誤解釈)

「`ADDNEW-POC-SKIP reason=no-capacity`が出ている = `FullCapacityAddNewBridge`が機能不全」という初期解釈は誤り。empty-slot AddNew helperがskillcnt=8 full capacity時にskipするのは正常であり、その後Power-Up成立→native forget UI→1スキル忘れる→target新規取得→skillcnt=8維持、がFullCapacity AddNewの正式仕様。今回の二重発動はFullCapacity AddNew自体の機能不全ではない。

同様に「1回のLvUpでPower-Upが2回成立することが意図された仕様」も**記録しない**。`ALLOW-FIRST`という局所ガードの設計意図(349-zombie対策として、handled直後の最初のCore再entryを1回だけ許可する)と、二重Skill Changeを意図したことは別。

### CONFIRMED(根本原因)

`CoreReentryHandledCheck.cs`/`HandledCandidatesObserver.cs`(349-zombie対策、commit `08a06fe`)の`ALLOW-FIRST`は`(stock, EventParam)`単位のcandidate識別で「最初のCore再entryを1回許可」する設計。FullCapacity AddNew完了(forget-confirm解決)後、native が**別のcandidate識別(EventParam)**でCoreへ再entryした場合、`HandledCandidates`/`CoreConsumed`はこれを新規candidateとして認識し、ALLOW-FIRSTが素通しする。これが同一level-up episode内での2回目Power-Up成立の直接原因。**ALLOW-FIRSTの既存設計が、FullCapacity AddNew完了後の同一level-up episode再entryを1回許可してしまう統合バグ**、と分類する。

### Episode-level success latch PoC(実装済み、UNRESOLVED)

`stockPtr`(unit)単位の広域ガードを追加した。既存の`ALLOW-FIRST`/`HandledCandidates`/`CoreConsumed`/`DefaultSkillHandledClassificationBlock`は無改変。

- **SET**: `rstCalcSkillPowerUpCore`のcoreResultが`1`(ordinary Power-Up)または`2`(genuine Mutation)の場合、`HandledCandidatesObserver.MarkEpisodeSkillChangeApplied(stockPtr)`を呼びlatchをSET(`CoreReentryHandledCheck.Postfix`)。
- **SUPPRESS**: 同一unitでlatchがSET済みの場合、candidate IDに関係なく次回以降のCore再entryを既存`ALLOW-FIRST`/`HandledCandidates`判定より前で無条件抑止(`CoreReentryHandledCheck.Prefix`)。新規ログ: `CORE-EPISODE-LATCH; action=SUPPRESS-EPISODE-LATCH`。
- **CLEAR**: `GBWK.LevelUpCnt`が「0以下→新たな正の値」に遷移した時点を新規level-up episode開始とみなしlatchをclear(`DefaultSkillIteratorTrace`の既存sample pointから`HandledCandidatesObserver.OnLevelUpCntObserved`を呼ぶ)。新規ログ: `EPISODE-LATCH-CLEAR`。`ResultLifecycleBoundaryObserver`(`rstCreateTargetList`境界)でも粗いフォールバッククリアを行う。

変更ファイル: `src/SkillMutationV3/HandledCandidatesObserver.cs`、`src/SkillMutationV3/CoreReentryHandledCheck.cs`、`src/SkillMutationV3/DefaultSkillIteratorTrace.cs`。

### UNRESOLVED(production blocker)

`LevelUpCnt <= 0 → > 0`をnew episode boundaryとみなす設計は、**同一戦闘で同一unitが2レベル以上上がるケース**で未検証。`DefaultSkillHandledClassificationBlock`の既存コメントにある通り、native分類ループはcandidateをskipする度に`LevelUpCnt`をデクリメントするため、「残りLvUp回数」的semanticだった場合、1回目のlvupでlatchがSETされた後、2レベル目の正当なPower-Upまで誤って抑止する可能性がある。

- Episode latchという設計思想: 妥当、strong candidate。
- `LevelUpCnt`によるclear boundary: UNRESOLVED、要runtime確認。
- **READY FOR PRODUCTION: NO**

### 次回最優先runtime test

1. **Test A(二重発動再現ケース)**: 1回目Power-Up成立→episode latch SET→同一LvUp内Core再entry→`CORE-EPISODE-LATCH; action=SUPPRESS-EPISODE-LATCH`→2回目forget UIが出ないこと、source preserved、target acquired、skillcnt=8、warning/errorなしを確認。
2. **Test B(同一unitの2レベル以上同時LvUp、最重要)**: 意図的に大量EXPを与え、同一仲魔が1戦闘で2レベル以上上がるケースを作る。LvUp #1でSkill Change可能、LvUp #2で`EPISODE-LATCH-CLEAR`が正しい境界で出てSkill Changeが可能であることを確認。
3. Test B失敗時のみ`LevelUpCnt`以外のnative episode boundaryを再調査する(ALLOW-FIRST全面削除には戻らない)。
4. Test A/B成功後、episode latchをproduction candidateへ昇格判断する。

Option F(`exclusionObserved=False`、Phase F参照)の調査は、この検証より後でよい。

### Diagnostic状態

`DefaultSkillIteratorTrace`は`Enabled=true`のまま(Test A/Bに必要)。**Hardware breakpoint系(`EventParamWriterCaptureProbe`/`EventParamActualWriterTrace`/VEH/DR0-DR7)は過去2回クラッシュ済みにつき、User明示承認なしで再使用禁止**(既存禁止事項、継続)。

## Phase H: SkillPowerUp.Chance=Always 正式仕様確定 / early exclusion根本原因 / per-episode single-roll確認(2026-09-19、FIXED、runtime confirmed)

### 発見経緯

「カハク(unit=91)がSkillPowerUp.Chance=Always設定下でも、同一save・同一戦闘条件でSkill Changeが起きたり起きなかったりする」という実プレイ由来の違和感から調査を開始した。当初はepisode latch(Phase G)やGBWK.Flag(+0x7E)周辺を疑ったが、最終的に全く別の、より根本的な native仕様に到達した。

### CONFIRMED: Multi-Level-Up は per-episode single-roll(Phase Gの残課題への回答)

実機ログ(10倍EXPブースト`ExperienceMultiplierDiagnostics.cs`を用いた意図的multi-level-upテスト)で、同一戦闘内で1〜6Lv上がった4ユニットを観測した結果、**delta(1〜6)に関係なく、`rstCalcSkillPowerUpCore`の呼出は常に1回だけ**だった(4/4で一貫)。呼出後、残っている`GBWK.LevelUpCnt`の値(0〜5相当)は消費されずそのままepisodeが終了する。

これはPhase Gの「`LevelUpCnt<=0→>0`をclear boundaryとする設計が、同一戦闘で2レベル以上上がるケースで正当な2回目のSkill Changeを誤って抑止しないか」というUNRESOLVED懸念に対する部分的な回答になる: **native自身がそもそも1 level-up episodeにつき1回しかCoreを呼ばないため、「2回目の正当なSkill Change」という事象自体が(今回観測した範囲では)発生しない**。ただし`CORE-EPISODE-LATCH`(SUPPRESS発火)はこのセッション群を通じて一度も観測されておらず、Phase Gの Test A(SUPPRESSが実際に正しく機能する場面)は依然未実証のまま。episode latchの設計自体を否定する材料ではないが、「二重発動を積極的に抑止した実例」はまだ無い。

### CONFIRMED: `GBWK.Flag`(+0x7E)はSkillPowerUp専用ではない、Hearts-event等と共有される別系統

静的解析(xref scan、14関数を特定)により、`GBWK.Flag`は`rstUpdateSeqSkillPowerUp`の「classification result(0-4)」としてだけでなく、`rstUpdateSeqHeartsMaster`(VA `0x18228BC20`、進入時Flag∉{0,1}なら`inc`する経路が存在)や`rstUpdateSeqDevilParam`(VA `0x182289770`、Flagを開始indexとして`GBWK+0x80`が指す6要素配列を探索し、該当があれば`index+1`をFlagへ書く)など、複数の戦闘後イベントSeq関数から共有されるバイトであることが判明した。

`GBWK+0x80`配列への唯一のwriterは`rstSetHeartsEvent`(`rstUpdateSeqHeartsEvent`が`GBWK.Flag==3`の時のみ呼ぶ)。そのEventType引数(`GBWK+0x39`)のwriterの一つ(VA `0x18227E9A0`)を逆アセンブルした結果、**確認済みRNG関数(`0x1821690d0`)を2回呼び出して値を決定していることをCONFIRMED**(2回目呼出はGBWK経由の重み付きテーブルからのindex抽選)。

既存Canonicalが言う「seq=11、~1/3 RNGロール、`GBWK+0x7a`(event-type reservation)」は、`rstCalc`側の**別の独立したSeqInfo dispatchテーブル**(`rstUpdate`側とは別物、両方が同じ`SeqInfo.Current`を読む)に実在することを直接逆アセンブルで再確認したが、**`GBWK+0x7a`と`GBWK+0x39`は接続しない別系統**(前者はGiftイベント`rstUpdateSeqGift`、後者はHeartsイベント`rstUpdateSeqHeartsEvent`)であることも確認した。

**結論**: このHearts/DevilParam/GBWK.Flag系統はSkill Power-Up/Mutationの成否には直接関与しない別のゲームメカニクスだった。調査自体は「同じ`GBWK.Flag`バイトを複数のnative Seq関数が共有し、RNGで駆動される」という設計の正しい切り分けとして価値があるが、カハクの本題(下記)とは別問題である。

### CONFIRMED(根本原因): early exclusion が dil roll より手前で rawResult=0 を確定させる

`rstCalcSkillPowerUpCore`(VA `0x18227E100`)の実際の処理順序を完全逆アセンブルした結果:

```
1. rstRndGetPowerUpSkill(pStock, &PUpSkillIndex) -> PUpSkillID
   (VA 0x18227E15A/0x18227E15F。無条件・最速部、dilより完全に手前)
   所持スキルをscanし、各skillIDをcmbGetPowerUpSkill(skillID)へ渡し、
   戻り値(強化先ID)の候補プールからRNGで1つを選ぶ。
   PUpSkillID==0 -> return 0 (R0-A)

2. rstCreateBeforeSkillList(...) (VA 0x18227E261。同じく無条件・dilより手前)
   「現在レベルで本来もう習得済みであるべき」curriculum候補群("before list")を構築。

3. PUpSkillIDがbefore listに一致 (VA 0x18227E293) -> return 0 (R0-B)
   *** ここがdil rollより手前 ***

4. dil roll (VA 0x18227E305、RNG 0x1821690d0、約50/50)
   dil=0 -> ordinary Power-Up(bit6 test含む) / dil=1 -> Mutation(cmbGetMutationSkill)
```

**`SkillMutationChanceControl`のAlwaysパッチ(dilを1へ強制する3箇所のNOP/書換)は、手順3の時点で既にCoreがreturnしているケースには一切効かない。** dil roll自体に到達しないため。

カハクの場合、所持スキルのうち1つの強化先が「マハラギ」(skill4)であり、これは同時にカハクの現在レベルで既習得済み扱いのbefore-listにも該当する。**候補選定RNGがこの所持スキルを引いた回だけ、dilに到達する前にrawResult=0で終了する。** 別の所持スキルが選ばれた回は正常にdil rollへ進み成功する。同一save・同一unitでも試行毎に結果が変わっていたのは、この候補選定RNGの偶然の一致によるもので、native側の2つの独立した仕組み(Power-Up強化先抽選 / curriculum既習得チェック)が衝突しているだけであり、バグではない。

**この発見過程自体が静的REで到達し、実機診断(`ALWAYSDIL-PROBE`: 3パッチのライブbytesが常に正しく適用されていたこと、`PUpSkillID`がCore呼出ごとに新規に書き換わること)で直接裏付けられている。**

### 正式仕様(修正後)

```
SkillPowerUp.Chance=Always:
「有効なSkill Change候補が1つでも存在する限り、
 1 level-up episodeにつき必ず1回Skill Changeを成立させる」

SkillPowerUp.Repeat=Unlimited:
次のlevel-up episodeでも再びSkill Change可能にする(この判定とは独立)。

SkillMutation.Chance=Always:
native Coreがdil roll自体に到達した場合、その内部判定をMutation側へ強制する
(early exclusionで到達しなかった場合には無関係)。
```

native RNG経路の成功率100%化ではなく、「native Core自体の直接成功を優先し、rawResult=0(early exclusion等)になった場合のみfallbackする」という per-episode success guarantee である。

### 実装(`src/SkillMutationV3/SkillPowerUpAlwaysEpisodeGuarantee.cs`、新規1ファイル)

`rstCalcSkillPowerUpCore`のPostfix(`[HarmonyPriority(Priority.Last)]`、他の全Always変換の後に実行)。`SkillPowerUp.Chance==Always`かつ最終`__result==0`の場合のみ発火。

- **Phase A(Power-Up retry)**: 所持スキルをscanし`cmbGetPowerUpSkill`で強化先候補を列挙、既所持ターゲットを除外した distinct候補集合を事前計算。`rstCalcSkillPowerUpCore()`を直接再呼出し(完全に独立したHarmony-wrapped呼出、手動ロジック代替ではない)、**候補集合を全て観測し終えるまで**継続(blind N回retryではない)。
- **Phase B(Mutation fallback)**: Phase Aの候補集合が空/使い切った場合のみ、所持スキル各々に`cmbGetMutationSkill(ownedSkillId, stock)`を直接呼出し(native自身が内部で乱数探索するためmod側の候補テーブル再現は不要)。成功時は`PUpSkillIndex`/`PUpSkillID`を手動設定、`__result=2`。bit6は native自身のMutation成功tailと同じく触らない。
- **Phase C**: Mutation候補も無ければ`__result=0`のまま、不発を許可。
- Priority.Lastで実行するため、`CoreReentryHandledCheck`のepisode latch SETがPhase B成功に間に合わない — Phase B成功パスでのみ`HandledCandidatesObserver.MarkEpisodeSkillChangeApplied`を明示的に呼び直して整合性を維持。
- ログ: `ALWAYS-EPISODE-GUARANTEE`(candidate数、fallbackType、finalResult、episode latch前後を記録)。

### Harmony再帰安全監査(CONFIRMED)

`rstCalcSkillPowerUpCore`への既存パッチ9件を監査。有効なもの(`CoreReentryHandledCheck`/`OptionFRepeatUnlimitedControl`/`SkillPowerUpChanceControl`/`MutationDisabledBit6Guard`)は全てPrefix毎に状態リセットする設計でネスト呼出に対して安全。`SkillMutationAlways`/`OptionFF2Diagnostics`/`PowerUpMutationCfgDiagnostics`/`SkillWriterBoundaryTrace`は無効化済み(Enabled=false or未Initialize)で無関係。本実装自身は`_retryInProgress`ガードで自己再帰を防止。

### CONFIRMED runtime(2026-09-19、同一save・同一unit=91、3エピソード)

| Episode | Core呼出回数 | 結果 | ALWAYS-EPISODE-GUARANTEE |
|---|---|---|---|
| 1 | 1回(native直成功) | `coreResultNative=2` | 発火なし |
| 2 | 3回(失敗→失敗→Phase A retry成功) | `finalResult=1`(Mutation成功→既存Always変換でordinary化) | 発火(`fallbackType=powerup-alternate`) |
| 3 | 1回(native直成功) | `coreResultNative=2` | 発火なし |

3/3エピソードで`SKILLPOWERUP-REACHED`到達・`ADDNEW-POC-COMMIT`成立(いずれも「パトラ→メパトラ」、skillCnt 3→4)。`CORE-EPISODE-LATCH`(1episode内二重成立の抑止)は0件発火 = 二重成立自体が起きなかった。`NocturneModernGameplay`由来のWarning/Exceptionは0件(ログ中の59件は全て他Mod由来の無関係ノイズ)。retry暴走(上限到達)なし。

### Phase B: Mutation成功のラベル整合性(2026-09-19、FIXED、runtime confirmed)

正式仕様をさらに明確化した:

```
Phase A: 有効な通常Power-Up候補あり → Power-Up成立 → result=1
Phase B: 通常Power-Up候補が成立不能 → Mutation fallback → result=2のまま維持
Phase C: Power-UpもMutationも不可 → result=0、不発を許可
```

Phase Bで`cmbGetMutationSkill`が系統無関係な変化(例: 原スキル「ジオ」→Mutation先「ディア」)を返すのはnative自身の正当な挙動(Mutationは元々系統無関係な変化を起こしうる機能)。問題は、既存の`SkillPowerUpChanceAlwaysPatch`(Phase E、"PowerUp.Chance=Alwaysならgenuine Mutationも通常Power-Upへ再ラベルする"という既存設計)がPhase Bの成立にも無差別に適用され、「ジオがPower-Upしてディアになった」という意味不明な表示を生んだ(2026-09-19、user実機報告)。

**修正**: `SkillPowerUpChanceAlwaysPatch`に`SuppressNextMutationConversion`フラグを追加。Phase B成功時にこのフラグをセットし、後続(実行順不定)の2→1変換を1回だけ消費・スキップする。Phase A(通常Power-Up)・native自身のdil=1成功は従来通り変換対象のまま。

### 2つの回帰と、そこから得られた設計原則(2026-09-19、両方FIXED)

**回帰1: HarmonyLibのpostfix実行順は、この2パッチ間で`[HarmonyPriority]`を変更しても一切変化しなかった。**
`Priority.Last`→`Priority.First`と2回試したが、両方とも「`SkillPowerUpAlwaysEpisodeGuarantee`のPostfixが先、`SkillPowerUpChanceAlwaysPatch`が後」という同一順序になり、後者が前者の設定した`PUpSkillID`(Phase B fallback target)を、stale(古い)候補値(しばしば0="リザーブ")で上書きする実害バグを2回発生させた。**教訓**: この環境ではHarmonyの複数postfix間の実行順序をpriority属性だけで確実に制御できると仮定しない。**修正方針**: 実行順に賭けるのではなく、上書き元となる共有state(`SkillPowerUpChanceCandidateCapture.LastOriginalCandidateSkillId`)自体をfallback成立時に同期する(`SyncFallbackTarget`)、または明示的なフラグで対象パッチの変換をスキップさせる(`SuppressNextMutationConversion`)、という**実行順序に依存しない設計**へ倒すこと。

**回帰2: 新規追加したpostfixが、同一native関数上の既存の重複防止機構(episode latch)を考慮しなかったことによる349-zombie型の再発。**
`CoreReentryHandledCheck`はepisode latch SET後の再entryを`SUPPRESS-EPISODE-LATCH`で正しく抑止し`__result=0`を自らセットするが、`SkillPowerUpAlwaysEpisodeGuarantee`はこの`__result=0`を「genuine失敗」と誤認し、抑止されるべき再entryのたびに独立してfallbackを再構築していた(1エピソード内で最大5回の余分な成立を観測)。**教訓**: 同一native関数に新規postfixを追加する際は、「自分がstateをどう変更するか」だけでなく「既存の他patchが持つ重複防止/抑止stateを、自分も読んで尊重すべきか」を明示的に監査項目に含めること。**修正**: `SkillPowerUpAlwaysEpisodeGuarantee`の冒頭で`HandledCandidatesObserver.IsEpisodeLatched(stockPtr)`を確認し、既にlatch済みなら即returnする契約を追加。

**Canonical契約として明記**: Always Episode Guaranteeは、既にepisode latch済みのCore result=0をfallback対象として扱ってはならない。

### CONFIRMED runtime(2026-09-20、最終確認、複数unit・複数episode)

10倍EXPデバッグ装置(`ExperienceMultiplierDiagnostics.Multiplier=10`、テスト後`1`へ復元)を用いた実機セッションで、`ALWAYS-EPISODE-GUARANTEE`が12回発火(unit=92×4、unit=97×3、unit=59×2、unit=91×1、unit=137×1、unit=126×1、複数の独立したepisodeにまたがる):

| fallbackType | 件数 | 検証結果 |
|---|---|---|
| powerup-alternate | 6 | 全6件、後続`SkillPowerUpChance Always priority applied`が同一frame・同一target IDで発火(`SyncFallbackTarget`により無害化を確認) |
| mutation | 6 | 全6件、後続変換ログが一切発火せず(`SuppressNextMutationConversion`により正しく抑止) |

- skill ID 0("リザーブ")の実コミットは0件
- 同一episode内の多重成立(ゾンビ)は0件(`episodeLatchBefore`は12件全てFalse = 毎回正当な新規episode)
- 同一unitが複数回(最大4回)、別々のepisodeで再度Guaranteeを利用できており、latchのCLEARが次episodeへ正しく引き継がれていることを実証
- `NocturneModernGameplay`由来のWarning/Exception/failed safelyは0件
- `ADDNEW-POC-COMMIT`等で実際のskill付与も確認済み

### READY FOR PRODUCTION: YES(2026-09-20、runtime confirmed、複数unit・複数episode・2件のregression修正込み)

### 次のステップ

1. 診断ログのうちproductionに残すもの/落とすものを整理(`ALWAYSDIL-PROBE`等、本調査専用の一時診断は削除候補) — 2026-09-19実施済み(`GbwkFlagTransitionTrace`/`AlwaysModeDilContradictionProbe`/`MultiLevelEpisodeBoundaryTrace`は`Enabled=false`でソース保持、`ALWAYS-EPISODE-GUARANTEE`はproductionログとして維持)
2. Git保全(2026-09-20、User承認済み)
