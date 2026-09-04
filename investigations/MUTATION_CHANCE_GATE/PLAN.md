# genuine Mutation 100%化 Gate Investigation

更新日: 2026-09-02

## 参照優先順位

会話再開時は、この文書を読む前に必ず以下を先に読むこと。

1. `00_PROJECT_RULES.md`
2. `01_CURRENT_STATE.md`(Canonical State。この文書はそれを上書きしない)
3. 本文書(`investigations/MUTATION_CHANCE_GATE/PLAN.md`)

`investigations/PRESENTATION_CONSUMER/`と`investigations/SKILL_MUTATION/`は**別テーマ**(presentation表示欠落)であり、本investigationとは目的が異なる。混同しないこと。

**2026-09-02追記**: 本investigationの「Power-Up/Mutation振り分けbranch」部分は、`investigations/POWERUP_MUTATION_CFG/PLAN.md`へゼロベース再解析として派生・発展した(旧Patch A/B/C・旧Queue/Inline実装を前提にしない独立解析)。native CFG・pUpSkillResult write map・bit6ゲートの矛盾未解決課題はそちらを参照。本文書はA-only構成でのMutation成立率の実測データとして引き続き有効。

## 目的

現在の`SkillMutationAlways`は`A_ONLY`構成(`Patch A = A8 00`、`Patch B/C = vanilla`)。この状態でHigh Pixie `unit=59`のgenuine Mutationは**100%成立していない**。

**最終目標**: ordinary Skill Power-UpをMutationへ変換するのではなく、genuine Mutation pathそのものを100%成立させる。今回の調査フェーズは実装ではなく、**成功回と失敗回を分けるnative gateの特定**に集中している。

---

## 実機結果(2026-09-02, `Latest.log` 34909行、10:33-10:40セッション)

High Pixie `unit=59`、6試行:

| # | frame | original | mutated/nativeResult | 結果 | `cmbGetMutationSkill`呼び出し |
|---|---|---|---|---|---|
| 1 | 4989 | 36 | 61 | 成立 | あり(`MUTATION-VALID-CANDIDATES` frame=4989) |
| 2 | 12909 | 36 | 392 | 成立 | あり(frame=12909) |
| 3 | 23351 | 36 | 392 | 成立 | あり(frame=23351) |
| 4 | 31759 | 36 | 22 | 成立 | あり(frame=31759) |
| 5 | ~37115-38285 | - | - | **不成立** | **なし** |
| 6 | ~45460-46699 | - | - | **不成立** | **なし** |

- `MUTATION-VALID-CANDIDATES`(`cmbGetMutationSkill`のPostfix、**呼ばれた回だけ**出る無条件ログ)は`unit=59`について全セッションでちょうど4回のみ。5・6回目に対応する行はログに一切存在しない。
- 失敗回の`seqCurrent`遷移: `6 → 8 → 21 → 22 → 11 → 13`(成功4回は`8 → 9 → 10 → 11`)。**`seq=9`に一度も入らず、`8`から直接`21`へ飛ぶ**のが失敗回の一貫した特徴。
- 失敗回は`seq=8`区間中、`pUpSkillResult`が常に**0**(3にも1にもならない)。

---

## GATE構造(静的解析でCONFIRMED、`GameAssembly.dll`直接disassembly)

`rstcalc.rstCalc`(VA `0x18227E710`)のseq=8処理で、`cmbGetMutationSkill`へ到達するまでに直列した3段のgateが存在する。

```text
rstCalc, seq=8
    │
    ▼
GATE 1 (rstCalc内, VA 0x18227ef72)
TARGET静的(0x182e4ed30) -> +0xb8 -> [0] -> +0x58(配列) -> array[0] -> +0x24(word)
    < 7 なら即 al=0 で終了(RNGも呼ばれない)
    │passes(>=7)
    ▼
GATE 2 = Patch A (rstCalc内, VA 0x18227efd0)
call RNG(0x1821690d0); test al,3
    vanilla: 1/4成功。Patch A適用後(A8 00): 常に成功。
    成功時のみ rstCalcSkillPowerUpCore(VA 0x18227e100) を呼ぶ。
    │
    ▼
rstCalcSkillPowerUpCore() 内部
    │
    ▼
GATE 3a (VA 0x18227e385-0x18227e39a)
RNG() % 254 <= 0x70(112) ? (推定 約44%、未検証)
    │no(超過) → dil=2 → 即 al=3 で return
    │yes(dil=0)
    ▼
GATE 3b (VA 0x18227e40c)
WorkStock+0x10 の bit6(0x40) テスト
    │set済み → 即 al=0 で return(既に処理済み)
    │clear
    ▼
成功: WorkStock+0x10 の bit6 を自分でセット → al=1 で return
    │
    ▼
GBWK+0x4b(PUpSkillResult相当) = al
al=1のときのみ下流でPUpSkillResult=2へ発展 → rstUpdateSeqSkillPowerUp → cmbGetMutationSkill到達
```

補足:

- `MutationHelperNativeVa = 0x18227E100`(既存`SkillMutationAlways.cs`が使用)は`rstCalcSkillPowerUpCore`の入口そのものであることを`global-metadata.dat`のmethodPointersテーブル経由でCONFIRMED。既存Patch B/C(`+0x24C`/`+0x29C`)はこの関数内部の`dil`分岐(GATE 3aの直後、`xor dil,dil`/`mov dil,2`)を書き換える。
- `MutationEligibilityFlagPatch`(`stock.flag |= 0x40`を全unitに強制する既存コード)は`Prepare() => false`により**現在無効化されており実行されていない**ことをコード上で確認済み。現状bit6は完全にnative任せ。

---

## Evidence分類

### CONFIRMED

- Priority 1の答え: **失敗回では`cmbGetMutationSkill`自体が呼ばれていない**(ログ上の無条件Postfix `MUTATION-VALID-CANDIDATES`が皆無であることによる直接証拠)。
- `rstCalc`のseq=8処理には`cmbGetMutationSkill`到達前に直列した3段のgate(GATE1/GATE2=Patch A/GATE3=rstCalcSkillPowerUpCore内部のGATE3a・GATE3b)が存在する(バイト列を直接読んだ結果)。
- `rstCalcSkillPowerUpCore`のVA(`0x18227E100`)は既存`MutationHelperNativeVa`定数と一致し、Patch B/Cの対象はこの関数内部の分岐である。
- `MutationEligibilityFlagPatch`は`Prepare() => false`で無効化されており現在実行されていない。
- Patch Aは「GATE 2」のみを100%化しており、GATE 1・GATE 3a・GATE 3bには一切手を付けていない。
- `rstcalc.rstCalcSkillPowerUpCore()`の正式managed signatureは`public static sbyte rstCalcSkillPowerUpCore()`(引数なし、sbyte戻り値)。実assembly(`MelonLoader\Il2CppAssemblies\Assembly-CSharp.dll`)をilspycmdで確認済み。
- `datUnitWork_s.flag`は`public uint flag`のmanaged property(実assembly確認済み)であり、`MutationEligibilityFlagPatch`が既に使用している。

### STRONG

- 失敗回のPUpSkillResult=0(3ではない)という実機観測は、**GATE 3aの内部確率ロール失敗(al=3)ではなく、GATE 1かGATE 3bのどちらかであることを強く示唆する**。
- GATE 1・GATE 3bはどちらも`al=0`という同じ観測結果を生むため、現在のログだけでは区別できない。

### POSSIBLE

- GATE 1(`TARGET+0x58`→`array[0]+0x24`<7)がPixie自身の何らかのカウンタ/レベルキーで、直近の連続成功によって条件を満たさなくなった可能性。ただしこのオブジェクトは`GBWK`ではなく`TARGET`(戦闘/対象コンテキスト用の別静的)経由であるため、**Pixie自身のstateかどうか自体が未確認**。

### REJECTED / STALE

- 「候補生成後の確率判定で0にされる」(cmbGetMutationSkill内部のRNG search miss説): `cmbGetMutationSkill`自体が呼ばれていない以上、今回のFAILURE CASEには当てはまらない。
- 「Patch Aが唯一の残存ゲートである」という前提: GATE 1・GATE 3という別の独立したgateの存在を直接確認したためSTALE。
- 「`MutationEligibilityFlagPatch`が現在効いている」という前提: `Prepare() => false`で無効化済みと確認したためSTALE。
- GATE 3b(`WorkStock+0x10`のbit6)が「前回成功時にセットされたまま残留し、次のレベルアップまでにクリアされなかった」可能性: 2026-09-02 2回目セッションの実機ログにより**REJECTED**(下記「実機結果(2026-09-02, 2回目セッション)」参照)。bit6はunitごとに終始固定値であり、その固定値のままgenuine Mutationが複数回成立しているため、bit6残留がMutation失敗の原因という仮説は棄却する。

---

## 実機結果(2026-09-02, 2回目セッション, `Latest.log` 16605行, 11:22-11:27セッション)

`V3-GATE1-VALUE` / `V3-GATE3B-BIT6` / `V3-SKILLPOWERUP-CORE`の3種テレメトリを使った初回の実機テスト。High Pixie `unit=59`とunit=60を対象。

### CONFIRMED(このセッションで追加)

- **GATE1**: unit=59(GATE1イベント506件)・unit=60(412件)とも`value24=15`固定、`passesGate=True`固定。このセッション中、GATE1が`False`になった読み取りは1件も存在しない。
- **GATE3B bit6**: `WorkStock.flag`経由の読み取りが、unit=59は全506件で`flagsRaw=0x1443`(`bit6=True`)固定、unit=60は全412件で`flagsRaw=0x3`(`bit6=False`)固定。セッション中一度も値が変化していない。
- **bit6固定のままgenuine Mutation成立**: unit=59はbit6=True固定のまま`MUTATION-VALID-CANDIDATES`(frame=20123)が1回成立。unit=60はbit6=False固定のまま同ログが4回(frame=4228, 22358, 29012, 35018)成立。→ bit6の値とMutation成立/不成立に相関が見られない。
- **`V3-SKILLPOWERUP-CORE`は本セッションで0件**(prefix/postfixとも皆無、例外catchによる`failed safely`警告も皆無)。`.analysis/disasm_patchA_region.txt`の静的disassemblyでは、GATE1・GATE2(Patch A)通過後の経路が実際に`call 0x18227e100`(通常のcall命令、inlineではない)へ収束することを確認済みであり、GATE1が全読み取りでTrueかつPatch AがA_ONLY(強制成功)である以上、この関数は実プレイ中に到達しているはずである。それにも関わらずHarmony patch(`V3SkillPowerUpCoreBoundaryPatch`)が一度も発火していない。
- **よってreturnValueによるCase A/B/C/D判定は、本セッションのデータでは評価不能**(Core telemetry未発火のため入力データが存在しない)。

### 保留事項(今回のテーマ外、実装/telemetry側の問題としてCodexへ引き継ぐ)

- `V3-SKILLPOWERUP-CORE`のHarmony patchが実機で一度も発火しない原因は未調査。source変更・実装調査はCodex側に引き継ぐ。事実の記録のみここに残す。

---

## 実装済みRuntime Telemetry(read-only、2026-09-02にbuild・deploy済み)

`src/SkillMutationV3/SkillMutationV3.cs`に以下を追加済み。**Mutationロジック・Patch A/B/C・RNG・return値・bit6の書き換えは一切行っていない。**

1. **`V3-GATE3B-BIT6`**(`SkillMutationV3.LogGate3BBit6`): `rstinit.GBWK.WorkStock.flag`(managed property、raw Marshal不使用)から`bit6 = (flag & 0x40) != 0`を算出。
2. **`V3-GATE1-VALUE`**(`SkillMutationV3.LogGate1Value`): `TARGET(0x182e4ed30) -> +0xb8 -> [0] -> +0x58 -> array[0] -> +0x24`をfail-closedで読み取り、`passesGate = value24 >= 7`。null/countはすべて安全にUNRESOLVED/NULL/INVALIDとしてログ化。
3. **`V3-SKILLPOWERUP-CORE`**(`V3SkillPowerUpCoreBoundaryPatch`): `rstcalc.rstCalcSkillPowerUpCore()`のPrefix/Postfix。`returnValue`(sbyte)を記録。

Harmony対象: `rstcalc.rstCalc`(新規`V3Gate1Gate3BBoundaryPatch`、既存patchとは別クラスとして共存)、`rstcalc.rstCalcSkillPowerUpCore`(新規`V3SkillPowerUpCoreBoundaryPatch`)。両方とも`SeqInfo.Current==8`かつ`pCurrentStock.id∈{59,60}`でフィルタ。

**build/deploy済み**:
- clean Release build成功。
- `C:\Program Files (x86)\Steam\steamapps\common\smt3hd\Mods\NocturneModernGameplay.dll`へcopy済み。
- SHA-256: `a91d023b18298cdc16f9906d76ddecf24e3fd686cd8feece76d0a4a722f329c`(source/deploy先で一致確認済み)。
- Git commit/pushは行っていない。

**この文書作成時点では、上記telemetryを使った次回実機テストはまだ実施されていない。**

---

## 次にやること(再開時の手順)

1. 本文書と`01_CURRENT_STATE.md`を読む。
2. `Latest.log`に`V3-GATE1-VALUE` / `V3-GATE3B-BIT6` / `V3-SKILLPOWERUP-CORE`の新しい行が増えているか確認する(前回のセッションでは未実施だった)。
3. 増えていれば、High Pixieの成功回・失敗回それぞれで以下を突き合わせる。

   - `V3-GATE1-VALUE`の`passesGate`
   - `V3-GATE3B-BIT6`の`bit6`
   - `V3-SKILLPOWERUP-CORE`が呼ばれたか、呼ばれたなら`returnValue`
   - 既存`V3-RAW-0x4B-COMPARE`(`pUpSkillResult`との相関)
   - 既存`V3-MUTATION-DETECTED`(最終成立確認)

4. 判定ルール(前回セッションで定義済み):
   - **Case A**: 失敗回だけ`passesGate=False` → GATE 1が原因候補としてCONFIRMED寄り。
   - **Case B**: GATE1通過・core呼び出しあり・失敗回だけ`bit6=True`かつ`core return=0` → GATE 3b(bit6)が原因としてCONFIRMED寄り。
   - **Case C**: GATE1通過・`bit6=False`・`core return=3` → GATE 3a確率ロールが原因。
   - **Case D**: GATE1通過・`bit6=False`・`core return=1`なのにMutation未成立 → さらに下流に別gateあり。
5. 判定結果に基づき、100%化の実装候補(GATE1 bypass / GATE3b bit6 clear / GATE3a確率変更)のうち、どれが「Mutation Chanceだけを独立制御できる」候補として最も安全かを再評価する。
6. **今回のセッションでは、source変更・build・deploy・Git操作はまだ行っていない。次のセッションでも、上記1-5でEvidenceを確定してから実装に進むこと。**

---

## 禁止事項(継続)

- 旧Queue/Inline/Learn-As-New実装の復活。
- 意味不明フィールド(GATE1の`+0x24`の正体等)への断定的な意味付け。
- 実機Evidenceなしでの確率判定/gateの推測による確定。
- `01_CURRENT_STATE.md`・`00_PROJECT_RULES.md`への無断上書き。
