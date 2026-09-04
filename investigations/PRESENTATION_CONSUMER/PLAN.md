# Presentation Consumer Investigation

## 目的

Mutation logic / stateは成功しているのに、nativeの「スキル変化」presentationだけ欠落する分岐点を特定する。

## Runtime Observation

Source object:

- `+0x91` unsigned
- `+0x91` signed
- `+0x92`
- `+0x94` raw / float
- `+0x98` raw / float

`State_182e31630`:

- `+0x04`
- `+0x08`
- `+0x0c`
- `+0x10`
- `+0x14`
- `+0x18`
- `+0x1c`

Checkpoints:

- `mutation-detected`
- `skillpowerup-prefix`
- `PUpSkillResult 2 -> -1 transition postfix`
- `replacement-visible`

## 判定

### Pattern A

visible / invisibleでsource差あり。

→ source / upstream writer・条件へ進む。

### Pattern B

source同じ / `State_182e31630`差あり。

→ relay / consumer途中へ進む。

### Pattern C

sourceもStateも同じ。

→ party scan / `0x182281c30`(= `rstSmoothMotion`) / downstream consumerへ進む。

## Native Findings(CONFIRMED, `SkillMutationV3.cs`(V3 zero-base PoC2診断コード)より削除前に保全)

以下は`src/SkillMutationV3/SkillMutationV3.cs`(投機実装ではなく読み取り専用telemetryのみを含むPoC2診断コード)のコメント中で確定していたが、本ファイル削除まで他の場所に記録されていなかった native fact である。ソースは2026-09-02時点のコード本文(byte-level disassembly / cpp2il ISIL / global-metadata.dat + `GameAssembly.dll`の`CodeGenModule.methodPointers`によるresolutionを併用)。

#### `0x182281C30` = `rstSmoothMotion`

- Signature: `Il2Cpp.rstcalc.rstSmoothMotion(ref Il2Cppmodel_H.dds3ModelHandle_t aHandle, int aGroup, int aNumber, float aBeforeLeng, float aSmoothLeng) : int`。
- `rstMotionReq`(本体VA範囲`0x182280d64`-`0x18228108e`)がこれを2箇所(call site VA `0x182280FE3` / `0x18228102B`)から呼び出す。両呼び出しとも`aGroup=0`、`aNumber`=source object `+0x91`(signed byte)、`aBeforeLeng`=source object `+0x94`(float)、`aSmoothLeng=0`、`aHandle`は未初期化/likely-nullのlocalを渡す。
- 識別根拠: `global-metadata.dat` + `GameAssembly.dll`の`CodeGenModule.methodPointers`テーブル解決、`cpp2il`のISILによるcross-check。
- `01_CURRENT_STATE.md`の旧記載「`0x182281C30` = `rstMotionReq`」はこの発見により訂正済み(REJECTED、`01_CURRENT_STATE.md`参照)。

#### Source object `+0x60`(既知の`real GBWK+0x60 = WorkStock`とは別物)

- source object(`[real GBWK.Pointer+0xb8][0]`経由、`+0x91/+0x92/+0x94/+0x98`と同じ既知object)の`+0x60`はqwordのpointer fieldである(static disassembly、`rstMotionReq`の論理範囲`0x182280d64`-`0x18228108e`内で確認)。
- 確認済みread site: `0x182280DD7` / `0x182281EA9` / `0x182281F0C` / `0x182281F54`。
- この`+0x60`が指すobjectの`+0x14`(word)は、`pCurrentStock+0x14`(raceId、既知field)と同じoffset patternだが、**別objectであり同一性は未確認(UNRESOLVED)**。単純な`pCurrentStock`ではない別contextのpointerである可能性がある点に注意。real GBWK自身の`+0x60`(= `WorkStock`)と紛らわしいが**完全に別のobject上の別field**である。

#### `State_182e31630+0x08`のset/consume/clearサイクル

- Reader/consumer: `0x182280D85`-`0x182280DD6`。`+0x08`が非0のとき`+0x0c`/`+0x10`/`+0x14`/`+0x18`を引数として`0x182281B70`を呼び出し、その後`+0x08=0`へ書き戻す(consume-and-clear)。
- Writer(`+0x08=1`): `0x182281042`-`0x182281047`。条件は「`0x182281c30`(`rstSmoothMotion`)の戻り値が`0`」かつ「その時点で`+0x04 != 0`」。
- 上記2経路が、01_CURRENT_STATE.mdの`State_182e31630`フィールド一覧における`+0x08`の唯一確認済みwriter/readerである。

#### `0x182281A80` presentation dispatch helper

- `tableIndex = action + raceId * 7`(`action`は`rstUpdateSeqSkillPowerUp`の当該call siteでCONFIRMEDの定数`2`、`raceId`は`pCurrentStock+0x14`)。
- `rstUpdateSeqSkillPowerUp`は、この一連のreplacement+presentation経路(`rstOverWriteSkill` → `0x182281A80`のdispatcher → `rstInitSkillAct`)全体を`cmp byte[GBWK+0x4b], 1; jne <skip>`(`PUpSkillResult==1`のときのみ実行)でgateしている。
- 実際のtable entry読み出し(`GBWK+0xb8 -> [0] -> +0x20 + index*8`、IL2CPPのstatic-fields間接参照経由)はfail-closedとして未実装・未読み取り(entryPtr/entryIsNullはUNRESOLVEDのまま)。この式自体は再現済みだが、entry本体の意味論は依然UNRESOLVED。

#### `GBWK+0x4b`(`PUpSkillResult`)の生byte参照site

- Read: `0x18228C7B8`。
- Presentation経路のgate判定: `0x18228C863`(上記`0x182281A80`のgateと同一と推定されるが、明示的な同一性確認はしていない)。
- Write(`0xFF`即ち`-1`書き込み、「`PUpSkillResult 2 -> -1`遷移」の完了マーカー): `0x18228C93D`。本investigationのcheckpoint「`PUpSkillResult 2 -> -1 transition postfix`」に対応する具体VAが初めて判明した。

#### `State_182e31630`の2段階dereference chainの実証

- `SetMotionGiftFlag`の命令列で実証: `0x19653B45F`(`mov rax,[rip+disp]` → `rax = *(slotAddress)`)、`0x19653B466`(`mov rcx,[rax+0xb8]` → `rcx = *(rax+0xb8)`)、`0x19653B46D`(`mov [rcx+4],1` → `rcx`がstate object本体)。
- `01_CURRENT_STATE.md`記載のchain式(`slotValue = *(slotAddress)`; `statePtr = *(slotValue+0xb8)`、2段階)はこの実証と一致している(以前のrevisionにあった誤った3段階目`[0]`は含まれない)。

#### GATE 1 chainの disassembly範囲

- `investigations/MUTATION_CHANCE_GATE/PLAN.md`に既に保全済みのGATE1 chain(`TARGET+0x58 -> array[0]+0x24`, `value24>=7`)は、`rstcalc.rstCalc`のseq=8処理内`0x18227EF34`-`0x18227EF77`(RNG roll地点`0x18227EFD0`の直前)のstatic disassemblyに基づく。

#### GATE2B(新発見、2026-09-04、`SkillPowerUp.Always`調査中に発見)

- `rstcalc.rstCalc`のGATE1(`value24>=7`)通過後、Core呼び出し(`0x18227F0A1`)前に、**もう一段の分岐**が存在する: source object(上記`+0x60`)が指すobjectの`+0x14`(word)を判定し、`==0`ならroll(`0x1821690d0`)の結果を無視して無条件でCoreへ進み、`!=0`ならrollの結果(`test al,3`、Patch Aと同一VA`0x18227EFD0`)でCoreへ進むかどうかが決まる。CONFIRMED(static disassembly、`.analysis/disasm_precore_gate.py`)。
- `State_182e31630+0x1C`の書き込み元(`0x18227F012`)・Core-entry gateの全体像は`01_CURRENT_STATE.md`Phase A節「実機診断で確定した事項(2026-09-04)」および`investigations/REPEAT_UNLIMITED/PLAN.md`のUNRESOLVED#1/#2参照。
- runtime実測(Frost unit=60、2試行)ではGATE2B自体が両試行とも`source+0x60`がnullで測定不能だったが、GATE1・bit6が同一条件下でCore到達可否が分かれたことから、**pre-Core確率制御点は`0x18227EFD0`のrollであるとSTRONGLY SUPPORTED(CONFIRMEDへは未昇格)**。詳細・Evidence classの区分は`01_CURRENT_STATE.md`参照。

#### 計装上の注意(手法メモ、native factではない)

- replacementの実visibility確認は`rstOverWriteSkill`自身のPostfixではなく`rstUpdateSeqSkillPowerUp`のPostfixで行うこと。`rstOverWriteSkill`のPostfix時点では書き込みがまだ観測上反映されていないことがPoC2で確認されている(将来instrumentationを組む際の実装上の注意点であり、native側の意味論的事実ではない)。

## 現在の優先順位

- 大量ログ解析: ChatGPT
- native意味論: Claude
- 実装 / build / deploy: Codex
- actual-game observation: User
