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
- **(2026-09-15昇格)** `rstUpdateSeqDefaultSkill`内`GBWK.DefSkillResult(+0x3C)==2`で分岐し、成立側で`GBWK.SkillCursor.<sub+0x20>.+0x14 = 8`(VA `0x182288AC1`)書き込み直後に`GBWK.SeqInfo.Current = 0x15`(21、VA `0x182288AE4`)を直接writeする(forget flow入口)。byte-exact disassemblyで確定(下記「6. rstUpdateSeqDefaultSkill全体のbyte-exact disasm」参照)。**旧STRONGLY SUPPORTEDから昇格。**

## STRONGLY SUPPORTED

- `TERMINOLOGY.md`の「seq=21→22 forget flow」記述はjump table実体decodeによりCONFIRMED級の強度に到達(「22→8復帰」の内部write自体は未追跡でSTRONGLY SUPPORTED止まり)。
- DefaultSkillとSkillPowerUpは`rstChkSkillAct`という共通ゲートを持つが、seq8/seq10という別々のstate machineケースとして分離されている(**注: `rstChkSkillAct`自体は`GBWK.MessageWait`専用のメッセージタイマーゲートであり、skill選択ロジックとは無関係。下記「7. rstChkSkillAct全体disasm」参照**)。

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

## 再開(2026-09-15、V3 zero-base継続調査として)

**再開方針(User明示)**: 本investigationを新規に立てず再開する。旧Queue/Inline/Learn-As-New実装(`docs/research/skill-mutation/master-archive.md`等)は**read-only historical evidence**として参照可だが、コード・制御フロー・状態管理・設計をV3へコピー/復活/移植することは禁止。Queueで使っていたhook/fieldが有用に見えても「Queueで使っていたから採用」ではなく、現行V3のzero-base調査でnative semanticsを再確認してから採否を判断する。実装PoCはまだ入れない(解析のみ)。

## CONFIRMED(2026-09-15、`rstUpdateSeqSkillPowerUp`全体逆アセンブル — seq10側のresult==1/2完全対称構造)

`rstUpdateSeqSkillPowerUp`(VA `0x18228C770`〜`0x18228CDE0`、377命令)を全体逆アセンブルした(`.analysis/disasm_rstupdateseqskillpowerup_v3_full.py`)。

### 関数冒頭の3段ゲート(PUpSkillResultチェックより前)

```
call 0x182169a40(ecx=0) -> AL; test al,al; jne <即return>          — gate1: 不成立なら今回フレームは何もしない
call 0x18216a490(ecx=0) -> AL; cmp al,1; jg <即return>; test al,al; jne <0x18228CA1F、別出口>
call 0x18216a170(ecx=0)                                              — gate3(副作用のみ)
```

`0x18216a490`の戻り値が1のときは`0x18228CA1F`という別分岐(`[GBWK+0x7E]=4`書き込み後、`PUpSkillResult=-1`にして即return、**本体のresult==1/2処理を一切実行しない**)へ抜ける。これは`PUpSkillResult`とは別軸の「このフレームはまだ書き込み準備ができていない」ゲートであり、既存コード(`FullCapacityAddNewBridgePoc.cs`)の「`rstUpdateSeqSkillPowerUp`は実際の書き込みフレームに至るまで何度も呼ばれる」という既知の挙動と整合する。

### `PUpSkillResult`(+0x4B)による本体分岐(byte-exact)

```
byte[GBWK+0x4B] < 0  → 0x18228CA7F系(「既に消費済み」後処理、下記参照)
byte[GBWK+0x4B] == 1 → 0x18228C870〜(ordinary分岐)
byte[GBWK+0x4B] == 2 → 0x18228C955〜(mutation分岐)
それ以外(==3等)       → 0x18228CA16(候補なし/失敗系、後述)
```

**ordinary分岐(VA `0x18228C870`)とmutation分岐(VA `0x18228C95E`)は完全に対称なコード**(命令列がほぼ1バイト単位で一致、対応するVAのオフセット差のみ):

```
rcx = [GBWK+0x60]              ; pCurrentStock
rbx = [rcx+0x50]                ; pCurrentStock.skill (Int32[])
rdi = movsx byte[GBWK+0x4C]     ; PUpSkillIndex(sbyte) — ordinary/mutation共通で同一fieldを読む
esi = movzx word[GBWK+0x4E]     ; PUpSkillID(word) — ordinary/mutation共通で同一fieldを読む
bounds check edi vs [rbx+0x18]
rcx = &rbx[0x20 + rdi*4]        ; = &pCurrentStock.skill[PUpSkillIndex]
call rstOverWriteSkill(rcx, esi) ; *slot = PUpSkillID  ← ★上書き確定の瞬間(唯一のwrite instruction)
call 0x182281a80(cl=2)           ; 種族+category別 message/pose lookup(presentation)
call 0x18227c080(ecx=0)          ; GBWK.MotionReq*へstaging(presentation)
call 0x182280a50(PUpSkillIndex,0); 主要な習得presentation dispatch(既知、sound等)
[GBWK+0x7E] = 1 (ordinary) / 2 (mutation)   ; ← 旧コメント「Flag=1 or 4」は誤りと判明、正しくは1/2
[GBWK+0x4B] = 0xFF (PUpSkillResult消費マーク) ; ordinary/mutationとも同一tailへjmpして収束
ret
```

**結論**: `rstOverWriteSkill`呼び出し(ordinary側VA `0x18228C8CB`、mutation側VA `0x18228C9B9`、いずれも同一managed method `rstupdate.rstOverWriteSkill`への呼び出し)が、上書きが不可逆になる**唯一の書き込み命令**。この前段(GBWK再読込・presentation準備)は一切skill[]を触らない読み取り専用処理であり、この呼び出し以降のpresentation call 3つ(`0x182281a80`/`0x18227c080`/`0x182280a50`)は既存investigation(ACQUISITION_LEARNASNEW旧セクション)で「いずれもstock.skill[]/pSkillの値を読み返さない」ことが既に確認済み。**これはPower-Up AddNewで既に実戦投入済みの`FullCapacityOverwriteSuppressor`(`rstOverWriteSkill`へのHarmony Prefix/Postfix)と全く同じ介入点であり、Mutation側の呼び出しサイトも同一managed methodへのHarmonyパッチで等しく捕捉できる**(現状は`FullCapacityAddNewBridgeArming.Armed`がPower-Up専用Triggerでしか立たないため、Mutationの呼び出しには一切影響していない)。

### `[GBWK+0x4B]<0`(既に消費済み)分岐の内訳

`[GBWK+0x7E]`(前段で1/2/3/4いずれかが書かれている「直近の結果分類」フィールド)を読み、値ごとに異なるnotification/message表示処理(`0x1822dd280`、文字列resource lookup`0x181cbd8f0`等)を行った後、最終的に`[GBWK+0x7E]=0`へリセットして終了する — 「実際の書き込みフレームの**次**のフレームで通知表示、その後リセット」という1フレーム遅延パターンと解釈できる(STRONGLY SUPPORTED、通知表示関数自体の中身は未解析)。

## CONFIRMED(2026-09-15、`rstCalcSkillPowerUpCore`前半逆アセンブル — PUpSkillIndex/PUpSkillIDの確定順序)

`rstCalcSkillPowerUpCore`(VA `0x18227E100`〜)を関数冒頭から逆アセンブルした(`.analysis/disasm_rstcalcskillpoweupcore_v3_full.py`)。

```
VA 0x18227E14F: rcx = [GBWK+0x60]              ; pCurrentStock
VA 0x18227E153: rdx = &GBWK+0x4C                ; &PUpSkillIndex(out-param)
VA 0x18227E157: r8d = 0
VA 0x18227E15A: call 0x1822810b0(rcx=pCurrentStock, rdx=&PUpSkillIndex, r8=0) -> AX
VA 0x18227E15F: [GBWK+0x4E] = AX                ; PUpSkillID = 戻り値(初期値、ordinary候補)
...
VA 0x18227E184: test si,si(PUpSkillID再読込); je <即return、coreResult=0相当>
```

**これは関数の絶対冒頭、dil(ordinary/mutation)分岐が一切発生する前の、両分岐共通の前処理である。** `0x1822810b0`は`01_CURRENT_STATE.md`既知の`rstRndGetPowerUpSkill`(唯一のPUpSkillIndex writer、VA `0x1965489A0`)への薄いwrapper/エントリと推定される(未確定、アドレス帯が別領域のため別途確認が要る)。この時点でPUpSkillID==0(=有効なordinary候補が1つも無い)なら、mutation分岐に到達する前に関数全体がcoreResult=0で即returnする。

**結論**: `PUpSkillIndex`は**dil分岐より前、関数冒頭で1回だけ確定し、ordinary/mutation両分岐で共有**される(mutation分岐側での再write箇所は無い、byte-exact確認済み)。`PUpSkillID`は同じタイミングで一旦「ordinary power-up候補値」により初期化されるが、mutation分岐(`cmbGetMutationSkill`成立時)ではVA `0x18227E597`(`mov word ptr [rcx+0x4e], ax`)で**上書きされる**。**したがってMutationは独立に発生するのではなく、常に「有効なordinary Power-Up候補が既に存在するslot」の上に成立する**、という構造的制約がある(Patch B/Cの「RNG-success側のdilをmutation方向へ倒す」という既知の挙動と整合)。

### 5つの優先確認項目への回答状況

1. **Mutation target skillIdの確定source**: `cmbGetMutationSkill`(VA `0x18227B6B0`)の戻り値、`GBWK.PUpSkillID`(+0x4E)へVA `0x18227E597`で上書き。CONFIRMED。
2. **`PUpSkillIndex`固定タイミング**: `rstCalcSkillPowerUpCore`冒頭、dil分岐より前、ordinary/mutation共通の単一writeで確定。CONFIRMED。
3. **`rstOverWriteSkill`呼び出し前後のresult/seq遷移**: 上記「result==1/2完全対称構造」の通り。CONFIRMED(byte-exact)。
4. **`rstAddSkill`をMutationから呼ばせる場合に必要な前提state**: 未着手。`rstAddSkill`内部ヘルパー(`0x18240DEB0`/`0x1824104E0`/`0x182410C60`)の解析が引き続き必要(旧UNRESOLVED項目1、未解消のまま)。
5. **seq21/22でDefSkillResult/EventParam等をPower-Up側と同じ意味で使えるか**: 未着手。

## CONFIRMED(2026-09-15、`rstAddSkill`内部ヘルパー3本 byte-exact解析 — 旧UNRESOLVED項目1解消)

`.analysis/disasm_rstaddskill_full.py`(既存)・`.analysis/disasm_rstaddskill_helper_trampolines.py`(新規作成)により、`rstAddSkill`(VA `0x182285A40`)本体と、それが呼ぶ3ヘルパー全てをbyte-exactでdisasmした。

### 重要な構造的発見: 3ヘルパーのうち2本は5バイトjmp trampoline

`0x18240DEB0`/`0x1824104E0`は、それぞれ`e9`(jmp rel32)5バイトのみのtrampolineであり、実体コードは別領域にある。旧来の「int3 3連続で関数境界」ヒューリスティックはこの手のtrampolineを誤検出するため、今回は固定windowのlinear disasmで実体を追跡した。

- `0x18240DEB0` → `jmp 0x19675DFF0`(実体)
- `0x1824104E0` → `jmp 0x19675E7A0`(実体)
- `0x182410C60`は非trampoline、そのもの自体が実体。

いずれもイメージ範囲内(`0x180000000`〜`0x19718F000`)。

### helper VA: `0x18240DEB0`(trampoline)→ 実体 `0x19675DFF0`

```
arguments: rcx=skillId(word、呼び出し元でcxへtruncate済み) / rdx=pCurrentStock相当ポインタ / r8=(呼び出し元は0を渡すが、関数冒頭でr8d自身を即座に0へ上書きするため実質未使用)
reads: [rdx+0x48](所持skill動的count) / [rdx+0x50](skill配列ポインタ、Int32[]、標準IL2CPP配列: +0x18=Length, +0x20=elem0, stride=4)
writes: 空きslot発見時のみ [skill配列+elem0+rax*4] = skillId(word値のzero-extend) / 空きslot発見時のみ [rdx+0x48]をinc(skillcnt++)
return(AL、sbyte扱い):
  - 0xFE = 所持skill配列(先頭からcount件、動的count)内にskillId重複あり(duplicate、書き込みなし)
  - 0xFF = 重複なしだが0..7の8slot全て埋まっている(空きなし、書き込みなし)
  - 0x00〜0x07 = 空きslotへ実際に挿入したslot index(skillcnt++実行済み)
role: 所持skill配列への「重複チェック付きAddSkill」本体。動的count(+0x48)まで重複scanし、無ければ固定8slot(0..7)を空き(skillID==0)scanして挿入・count++。**空きslot選択の経路が存在しないPUpSkillIndex生成(rstRndGetPowerUpSkill、Power-Up側)とは対照的に、この関数には空きslot挿入ロジックが完備している。**
confidence: CONFIRMED(static disassembly、byte-exact、実体・呼び出し元双方確認済み)
evidence: `.analysis/disasm_rstaddskill_helper_trampolines.py`出力、`0x19675DFF0`〜`0x19675E095`
```

補足(懸念点、未解明): `0x19675E037`〜`0x19675E043`(`movzx dx,...`/`xor dx,0x4821`/`and word ptr [rip-0x7e691d],dx`)は制御フローに影響しない孤立した命令列で、コードセクションへの自己書き込みに見える。前後のロジック(scanループの継続条件)には無関係と判断できるため、obfuscation/anti-disassembly由来のjunk命令の可能性が高い。実行時副作用の有無は未検証、UNRESOLVEDのまま残す。**本MODからこの領域への介入は一切行わない。**

### helper VA: `0x1824104E0`(trampoline)→ 実体 `0x19675E7A0`

```
arguments: rcx=skillId(word) / rdx=pCurrentStock相当ポインタ(呼び出し元は`0x18240DEB0`と同一のrdxを渡す) / r8,r9=未使用
reads: [rdx+0x78](別オブジェクトへのポインタ) → そのオブジェクトの+0x18(Length)/+0x20以降(要素、stride=2のUInt16[]、最大24要素固定scan)
writes: なし(純粋な検索、read-onlyな関数)
return(AL):
  - 0x00〜0x17(0〜23) = [rdx+0x78]配列内でskillIdが見つかったindex
  - 0xFF = 24要素全て走査したが見つからなかった
role: `pCurrentStock+0x78`が指す24要素UInt16[](候補/pending skill IDリストと推定)からskillIdをlinear searchするFindIndex。write無しのread-only helper。
confidence: CONFIRMED(static disassembly、byte-exact)
evidence: `.analysis/disasm_rstaddskill_helper_trampolines.py`出力、`0x19675E7A0`〜`0x19675E7DC`
```

### helper VA: `0x182410C60`(非trampoline、実体そのもの)

```
arguments: rcx=pCurrentStock相当ポインタ / rdx=index(word、helper2の戻り値をそのまま渡す、呼び出し元で確認)
reads: [rcx+0x78](helper2と同一オブジェクト) → そのオブジェクトの+0x18(Length、bounds check用)
writes: [そのオブジェクト+0x20+index*2] = 0(該当indexのUInt16要素をゼロクリア)
return: なし(void、edx=0にして終わるのみ)
role: helper2(`0x1824104E0`)が見つけたindexに対応する、`pCurrentStock+0x78`配列の該当要素をゼロクリアする。「候補/pendingリストから1件消費済みとして除去する」操作。
confidence: CONFIRMED(static disassembly、byte-exact、呼び出し元の引数受け渡しも確認済み)
evidence: `.analysis/disasm_rstaddskill_helpers_full.py`出力、および`rstAddSkill`全体disasmでの呼び出し確認
```

### `rstAddSkill`(VA `0x182285A40`)全体のcall graph再構成(CONFIRMED、byte-exact)

```
rstAddSkill():
  [IL2CPP static field初期化ガード、boilerplate]

  sourceObj = *(*(staticSlot)+0xb8)   ; 既知chain、real GBWK
  if (sourceObj == null) throw

  pCurrentStock = sourceObj.+0x60
  if (pCurrentStock == null) throw

  if (pCurrentStock.+0x14 == 0)
      skillId = sourceObj.+0x34      ; VA 0x182285A98
  else
      skillId = sourceObj.+0x32      ; VA 0x182285A92(既知の"EventParam読み取り"箇所と一致)

  sourceObj.+0x26 -= 1               ; VA 0x182285A9C、LevelUpCnt decrementの実体箇所

  pCurrentStock = sourceObj.+0x60    ; 再取得(防御的re-read)
  helper1(skillId, pCurrentStock, 0) ; = 0x18240DEB0、戻り値は一切チェックしない(fire-and-forget)

  pCurrentStock = sourceObj.+0x60    ; 再取得
  idx = helper2(skillId, pCurrentStock) ; = 0x1824104E0
  if (AL >= 0、すなわち0xFF(見つからず)でなければ)
      pCurrentStock = sourceObj.+0x60 ; 再取得
      helper3(pCurrentStock, idx)      ; = 0x182410C60、該当要素をゼロクリア

  ref pCurrentStockField = &(sourceObj.+0x60)
  tailcall 0x182281880(cl=1, rdx=&pCurrentStockField, r8=0)   ; presentation/通知系、詳細未解析・スコープ外
```

**重要な結論**:

1. **`rstAddSkill`はhelper1の戻り値(重複/満杯/成功index)を一切チェックしない。** 重複(0xFE)でも満杯(0xFF)でも関数は正常終了し、presentation tail-callまで到達する。`rstAddSkill`自身には失敗時の分岐が存在しない。
2. **pending/候補リスト(`pCurrentStock+0x78`)からのskillId除去(helper2+helper3)は、helper1での実際の付与成否とは無関係に無条件で実行される。** skillIdがこのリストに載っていれば、AddSkillが成功しようが(重複/満杯で)失敗しようが除去される。
3. **skillIdは`sourceObj`の`+0x32`/`+0x34`のいずれか(`pCurrentStock+0x14`で選択)からのみ供給される。** `GBWK.PUpSkillID`(+0x4E)とは別のfieldである。**用語訂正(User指摘、2026-09-15)**: `+0x32`(VA `0x182285A92`)は`01_CURRENT_STATE.md`既知の「EventParamを読む箇所」と同一VAだが、これは「その読み取りVAが既知」という一致にすぎない。`sourceObj`自体が「EventParam」と呼ばれてきた既知オブジェクトと同一であること、`+0x32`と`+0x34`それぞれの正確な意味論、writer、`pCurrentStock+0x14`によるどちらを選ぶかの分岐条件は、いずれもまだ確認していない。したがって**この2フィールドを「EventParam」と呼ぶのは時期尚早であり、正式な意味論確定まではUNKNOWNとして扱う**(呼称は`sourceObj+0x32`/`sourceObj+0x34`のまま)。Mutation側からこの経路を使うには、`PUpSkillID`の値をどちらかへ明示的にstagingする必要があるが、どちらを使うべきかは`pCurrentStock+0x14`の意味が分かるまで判断できない。
4. **`pCurrentStock+0x78`が指す24要素`UInt16[]`は、既存investigation(`HIDDEN_SKILL_ENTRY/PLAN.md`)で確認済みの`rstSkillInfo_t.SkillID`(ushort[]、+0x20、Length=24)と要素数・型が一致する。** 同一オブジェクトである可能性が高いが、参照経路(`r13`経由 vs `pCurrentStock+0x78`経由)が異なるため、同一性はHYPOTHESISに留める(未確認)。**現時点の扱い(User指摘、2026-09-15)**: 型・意味論とも確定していないため「24要素pending-likeな配列、意味論UNKNOWN」として扱う。curriculum管理用の別配列である可能性も排除しない。

### 5つの優先確認項目 — 更新

4. **`rstAddSkill`をMutationから呼ばせる場合に必要な前提state**: 上記call graphにより判明。
   - `sourceObj.+0x32`または`+0x34`(`pCurrentStock+0x14`依存で選択)へ、Mutation target skillIdをstaging。
   - `pCurrentStock`が有効であること(既存経路で自明)。
   - helper1が重複/満杯時に無反応で正常終了するため、事前のduplicate/full-capacityチェックをMOD側で用意しなくても native側が安全側に倒れる(ただしMutationとして「成功した」という結果通知は別途MOD側で用意する必要がある — `rstAddSkill`はPUpSkillResult等のPower-Up系fieldに一切触れない)。
   - CONFIRMED(byte-exact static)。

### ユーザー指示書セクション4(Mutation AddNewへの適用可否)への回答状況

- **A. Mutation target skillIdはPUpSkillIDからそのまま供給可能か**: 直接は不可(STRONGLY SUPPORTED)。`rstAddSkill`は`PUpSkillID`(+0x4E)を読まず、`sourceObj+0x32/+0x34`のみを読む。MOD側でMutation成立時に`PUpSkillID`の値を`+0x32`または`+0x34`(`pCurrentStock+0x14`の値で選択)へコピーするstaging処理を挟めば経路として成立し得る(未実装・未検証)。**ただし`+0x32`/`+0x34`自体の意味論・writer・`pCurrentStock+0x14`の選択条件が未確定のため、「staging処理を挟めば成立し得る」は設計仮説の域を出ない(次の優先investigation対象)。
- **B. EventParamへのstagingだけで成立するか**: staging自体は構造的に可能そうだが、「それだけで成立するか」(=`rstAddSkill`を正しいタイミング・正しい`pCurrentStock`状態で呼び出せるか)は未検証。`rstAddSkill`は`rstUpdateSeqDefaultSkill`(seq8)内2箇所からのみ呼ばれ、Mutation(seq10)側からは呼ばれない構造(既知CONFIRMED)。呼び出しタイミングそのものをどう作るかは別課題として残る。
- **C〜F**: 今回のhelper解析では未解決のまま(seq21/22 forget pipelineとの接続、DefSkillResult意味論、HiddenSlotCandidateInjectionとの接続はいずれも別investigationの範囲)。

## 次にやること(更新、2026-09-15、EventParam writer確定)

現状フレーム(最新):

- **`rstAddSkill` helper解析 = CONFIRMED**(`cmbAddSkill`/`cmbChkKeisyoSkillOwner`/`cmbDeleteKeisyoSkill`とVA完全一致)
- **`pCurrentStock+0x78` = `keisyoskill`(フュージョン継承待機リスト)= CONFIRMED**(旧HYPOTHESIS「`rstSkillInfo_t.SkillID`と同一」はREJECTED)
- **`sourceObj+0x32`/`+0x34` = `EventParam`/`GetHeartsSkill`(無関係の別subsystem、`pCurrentStock.id==0`で後者を選択)= CONFIRMED(フィールド識別)**
- **`EventParam`のwriter = CONFIRMED**: `rstcalc.rstCalcEventInfo(ref UInt16)`(VA `0x18227C330`)、`rstCalc`内のリトライループ(`rstUpdateSeqDefaultSkill`とは別の「計算」パス)。**同じループが`GBWK.DefSkillResult`も設定する**(`EventParam`だけでは不十分)。

優先順位:

1. ~~`rstAddSkill`内部ヘルパー解析~~ → 完了。
2. ~~`sourceObj+0x32`/`+0x34`の正体確定~~ → 完了。
3. ~~`pCurrentStock+0x78`の型と意味を確定~~ → 完了。
4. ~~`GBWK.EventParam`(+0x32)のwriter特定~~ → 完了(`rstCalcEventInfo`、上記参照)。
5. **[最優先]** Mutation AddNewの設計評価に着手: `Mutation result=2 → PUpSkillID確定 → rstOverWriteSkill抑止 → EventParam(+DefSkillResultの適切な値)をnative同等の意味論でstage → native rstAddSkillパイプラインへ合流`が成立するか。**前提として`rstChkAddSkill`の戻り値(al2)と`DefSkillResult`値の対応表(0/1/2等が何を意味するか)を先に押さえる必要がある(未解析)。** まだ設計候補の提示・実装PoCには進まない。
6. `rstChkAddSkill`(VA `0x18227F940`)の戻り値の意味論(新規、優先度高)。
7. `dil`(rstCalc内リトライループの比較値)の出自(新規)。
8. `rstCalcEventInfo`自身の内部実装(curriculumテーブル選択ロジック、hensinmaeとの対応)(新規)。
9. `rstCalc`と`rstUpdate`の呼び出しタイミング関係(同一フレームか次フレームか)(新規)。
10. `GBWK.SeqInfo.+0x14`(=1書き込み対象、forget-flow/通常分岐共通)の意味論。
11. seq22→8復帰時の内部write確認(旧UNRESOLVED項目2、継続)。
12. `rstReplaceSkill`の実際の呼び出し元(indirect callの可能性、継続)。
13. Power-Up/Mutation結果(`PUpSkillResult`/`PUpSkillID`)をDefaultSkill側state machine(seq8/21)へ合流させる具体的control point(継続)。
14. `HiddenSlotCandidateInjection.cs`(presentation層)がMutation経由のfull-capacity AddNewでもそのまま使えるかは、上記が固まり実機テストできる段階になってから判断する(現時点でPUpSkillResultチェックを持たない設計のため、構造的な障害は無いと見られるが未検証)。
15. 設計確定後、ユーザー承認を得てから実装フェーズへ。

## CONFIRMED(2026-09-15、続き — cpp2ilデコンパイル型定義+IL2CPPメタデータVA解決による決定的進展)

User指示(前回の優先順位付け)に従い、推測を避けるため`.analysis/cpp2il_cs/DiffableCs`(cpp2ilが生成した全型定義スタブ)と`.analysis/resolve_name_to_va.py`と同等の手法(global-metadata.dat直接パース + `CodeGenModule.methodPointers`テーブル引き)を使用した。**これは「読む」だけの静的解析であり、byte-exact disassemblyと同等以上に確度が高い(管理メソッド名・フィールド名はIL2CPPメタデータの実データそのものであり、推測ではない)。**

### 1. `sourceObj`(=real GBWK)の型が判明: `result2_H.rstData_t`

`.analysis/cpp2il_cs/DiffableCs/Assembly-CSharp/result2_H/rstData_t.cs`に完全なフィールドレイアウトがある。既知offsetと全て一致(CONFIRMED、型レベルでのクロスチェック):

```
+0x10 SeqInfo (cmpSeqInfo_t)      +0x26 LevelUpCnt (short)
+0x32 EventParam (ushort)         +0x34 GetHeartsSkill (ushort)
+0x3C DefSkillResult (sbyte)      +0x48 TargetCnt (sbyte)
+0x4B PUpSkillResult (sbyte)      +0x4C PUpSkillIndex (sbyte)
+0x4E PUpSkillID (ushort)         +0x60 pCurrentStock (datUnitWork_t)
+0x68 WorkStock (datUnitWork_t)   +0x7E Flag (sbyte)
+0x88 SkillCursor (cmpCursorInfo_t)  +0x91 MotionReq / +0x92 MotionReqMode / +0x94 MotionReqBeforeFrame / +0x98 MotionReqAfterFrame
+0x9C SelectSkillID (ushort)      +0xC8 MessageWait (int)
```

**重要な訂正**: `+0x91/+0x92/+0x94/+0x98`(既存investigationで「source object」の既知fieldとして扱われていたもの)は、`rstData_t`(=GBWK)自身のfieldである。旧「source objectとState_182e31630は別object」という区別自体は変わらないが、「source object」の実体はGBWK自身であることが型定義で裏付けられた。

**`+0x32`/`+0x34`の用語について(User指摘への回答)**: cpp2ilのfield名はIL2CPPメタデータの`string`テーブルから直接読んだ実名であり、憶測ではない。`+0x32`は正式に`EventParam`、`+0x34`は正式に`GetHeartsSkill`である。**この2つは同種の「EventParam相当2種」ではなく、全く別の subsystem に属する別々のfieldである**(下記参照)。したがって「EventParam」という呼称は`+0x32`単体については正しかった(2026-09-15訂正版の用語訂正を再訂正する)。ただし`+0x34`を「EventParam相当」と呼んでいた点は誤りであり、正しくは`GetHeartsSkill`と呼ぶ。

### 2. `pCurrentStock.+0x14` の正体: `datUnitWork_s.id`(CONFIRMED)

`.analysis/cpp2il_cs/DiffableCs/Assembly-CSharp/newdata_H/datUnitWork_s.cs`により、`pCurrentStock`(`datUnitWork_t : datUnitWork_s`)の`+0x14`は`ushort id`(ユニット固有ID、既存investigationの`unit=59`/`unit=60`等の表記と同一概念)である。

`rstAddSkill`の分岐は**`pCurrentStock.id == 0`かどうか**で`EventParam`/`GetHeartsSkill`のどちらを読むかを選ぶ。プレイヤーが所持する実在の悪魔は`id`が非0であるため、**通常の(悪魔の)Default Skill取得では実質的に常に`EventParam`側が使われる**(`GetHeartsSkill`側は`id==0`という特殊ケース専用、下記「Hearts」参照)。

### 3. `GetHeartsSkill`は無関係の別subsystem(CONFIRMED、REJECTED候補の整理)

`EventParam`/`GetHeartsSkill`を「同種2フィールド」と扱っていた前回の推測は誤りと判明。cpp2ilの全体grep(`rstCalcCore.cs`/`rstupdate.cs`)により、`GetHeartsSkill`は独立した"Hearts"サブシステムに属する:

```
rstCalcCore.cmbChkGetHeartsSkill(Byte HeartsID, datUnitWork_t pStock) -> ushort   VA 0x182279130
rstCalcCore.cmbGetHeartsSkillNums(Byte HeartsID) -> short                         VA 0x18227ADB0
rstupdate.rstGetHeartsSkill(SByte Mode) -> sbyte                                  VA 0x182285D30
```

`rstData_t`には`HeartsSkFlag`(+0x37)/`HeartsSkillResult`(+0x38)/`HeartsEventMode`(+0x39)/`HeartsEventData`(+0x3A)という専用field群が隣接しており、`EventParam`/fusion系とは別の完結したサブシステムであることが構造的に裏付けられる。**「Mutation AddNewへの適用」という本investigationの目的にとっては`GetHeartsSkill`経路は無関係と判断してよい(id==0という特殊ケース専用のため)。**

### 4. `rstAddSkill`3ヘルパーの正体がIL2CPPメタデータVA解決により確定(CONFIRMED、推測ではなく厳密一致)

`.analysis/resolve_keisyo_va.py`(新規、`resolve_name_to_va.py`と同一手法)で`fclCombineCalcCore`クラスの候補メソッドを解決し、以下が**VA完全一致**することを確認した:

| 旧呼称 | VA | 正体(managed method) | 署名 |
|---|---|---|---|
| helper1(`0x18240DEB0`, trampoline) | `0x18240DEB0` | `fclCombineCalcCore.cmbAddSkill` | `(UInt16 SkillID, datUnitWork_t pStock) -> sbyte` |
| helper2(`0x1824104E0`, trampoline) | `0x1824104E0` | `fclCombineCalcCore.cmbChkKeisyoSkillOwner` | `(UInt16 SkillID, datUnitWork_t pStock) -> sbyte` |
| helper3(`0x182410C60`, 非trampoline) | `0x182410C60` | `fclCombineCalcCore.cmbDeleteKeisyoSkill` | `(datUnitWork_t pStock, Byte Index) -> void` |

**これにより前回の「重複チェック付きAddSkill本体」「pending/候補リストからのFindIndex」「除去」という機能推測が、正式なmanaged method名で裏付けられた。** `rstAddSkill`は実質的に`cmbAddSkill`→(成否非chk)→`cmbChkKeisyoSkillOwner`→(見つかれば)`cmbDeleteKeisyoSkill`という、fusionモジュール(`fclCombineCalcCore`)の3メソッドを直接呼ぶラッパーである。

### 5. `pCurrentStock+0x78` の正体: `datUnitWork_s.keisyoskill`(CONFIRMED、旧HYPOTHESISをREJECTED)

`datUnitWork_s.cs`により`+0x78`は`UInt16[] keisyoskill`(継承スキル、フュージョン由来のスキル継承待機リスト)であることが確定した。`fclCombineCalcCore`クラスには`cmbAddKeisyoSkillFromSkill`/`cmbChkKeisyoSkillNums`/`cmbChkKeisyoSkillOwner`/`cmbChkLastKeisyoSkill`/`cmbClearKeisyoSkill`/`cmbDeleteKeisyoSkill`/`cmbGetKeisyoSkillEndIndex`/`cmbMoveKeisyoSkillToSkill`/`cmbRndAddKeisyoSkillToSkill`/`cmbRndGetKeisyoSkill`という豊富なAPIがあり、いずれも「フュージョン時に決定される継承候補スキルをpStockへ後から反映する」という一貫した意味論を持つ。

**REJECTED(訂正)**: `HIDDEN_SKILL_ENTRY/PLAN.md`記載のHYPOTHESIS「`pCurrentStock+0x78`(24要素`UInt16[]`)は`rstSkillInfo_t.SkillID`(`r13+0x20`、cmpDrawSkillの候補配列)と同一オブジェクトの可能性」は棄却する。`keisyoskill`(フュージョン継承待機リスト)と`rstSkillInfo_t`(Default Skill/curriculumの候補提示リスト、`rstCreateBeforeSkillList`が毎フレーム構築)は、フィールド名・API群から見て明確に別subsystemである。`HIDDEN_SKILL_ENTRY/PLAN.md`側は次回このREJECTEDを反映すること(本ファイルでは反映のみ記録)。

**`rstAddSkill`の完全な意味論(更新)**: 「Default Skillとして`skillId`(=`EventParam`)を付与する際、もし同じ`skillId`がこのユニットの`keisyoskill`(フュージョン継承待機リスト)にも載っていれば、そちらからも除去する」という**2つの独立した習得経路(レベルアップ既定習得 と フュージョン継承)の整合性を保つクリーンアップ処理**である。

### 6. `rstUpdateSeqDefaultSkill`(VA `0x182288790`〜`0x182288B1F`)全体のbyte-exact disasm完了(CONFIRMED)

既存の`.analysis/disasm_rstupdateseqdefaultskill_full.py`(前回セッション作成済み、境界`0x182288790`〜`0x182288B20`)を実行し全命令を確認した。構造:

```
rstUpdateSeqDefaultSkill():
  [IL2CPP cctor guard]
  result = rstChkSkillAct()                          ; VA 0x1822887D4

  if (result == 1):
      ; candidate配列([chain]+8, count=+0x18, elem0=+0x20)から要素取得・type-check(isinst)
      ; tailcall 0x1816f9470(obj, ?, mode=2, 0)        ; VA 0x18228884E(jmp)
      return

  if (result == 2):
      skillId = GBWK.EventParam                        ; VA 0x18228887C(movzx ebx, word ptr [rax+0x32])
      ; ↑ EventParamはここで「読まれる」のみ。書き込みではない。
      someVal = 0x1827c0a90(skillId, 0, 0)
      Notify(someVal); tailcall 0x1822dd280(cl=1)       ; メッセージ表示のみ、rstAddSkillは呼ばれない
      return

  ; result == 0 (通常ケース):
  if (fclChkMessage(0) != 0): return                   ; VA 0x18228892B/0x182288930

  ; candidate配列([chain]+0xd0, count=+0x18, elem0=+0x20)から要素取得・type-check(isinst)
  ; call 0x1816f9470(obj, ?, mode=0, 0)                 ; VA 0x18228899F、戻り値は以降未使用
  ; ↑ この呼び出しの中でEventParamが書かれている可能性が高いが、0x1816f9470自体はIL2CPP
  ;   generic method resolution thunk(下記参照)のため、静的追跡はここで頭打ち。

  if (GBWK.DefSkillResult == 2):                        ; VA 0x18228889BE(cmp byte[rax+0x3c],2)
      ; ★満杯(forget-flow)分岐★
      gate3()                                            ; 0x18216a170(既知)
      RecordTelemetry(tag=7)                             ; 0x18216ae20
      GBWK.SkillCursor.ResetUI()                          ; 0x1822eefa0(既知、"pure UI"関数)
      GBWK.SkillCursor.<sub+0x20>.+0x14 = 8                ; VA 0x182288AC1 ★CursorPos.Shift=8書き込み、既知VAと完全一致★
      GBWK.SeqInfo.Current = 0x15 (21)                     ; VA 0x182288AE4 ★SeqInfo.Current=21書き込み、既知VAと完全一致★
      goto AFTER_ADDSKILL

  ; DefSkillResult != 2 (空きあり):
  rstAddSkill()                                          ; VA 0x1822889E4

  AFTER_ADDSKILL:
  GBWK.SeqInfo.+0x14 = 1                                  ; VA 0x182288A10
  return
```

**重要な確定事項**:

- **`EventParam`は`rstUpdateSeqDefaultSkill`自身の中では一切書き込まれない**(result==2分岐での「読み」1箇所のみ)。`rstAddSkill`が呼ばれる`result==0`かつ`DefSkillResult!=2`の経路でも、事前のEventParam書き込みはこの関数の中には存在しない。**writerは別関数にある(下記UNRESOLVED参照)。**
- **`HIDDEN_SKILL_ENTRY/PLAN.md`記載の`CursorPos.Shift=8`書き込み(VA `0x182288AC1`)とSTRONGLY SUPPORTED止まりだった「`SeqInfo.Current=21`直接write」の具体VA(`0x182288AE4`)が、今回の全体disasmでbyte-exactに確定した。** 該当箇所(`investigations/HIDDEN_SKILL_ENTRY/PLAN.md`及び本ファイルのSTRONGLY SUPPORTEDセクション)は次回CONFIRMEDへ更新すること。
- forget-flow分岐(`DefSkillResult==2`)と通常分岐(`rstAddSkill`呼び出し)は、いずれも最終的に同じ`GBWK.SeqInfo.+0x14 = 1`書き込みへ合流する(`jmp 0x1822889e9`)。この`+0x14`はSeqInfo構造体内の別フィールド(`SeqInfo.Current`は`+0x11`、既知)で、意味は未特定。

### 7. `rstChkSkillAct`(VA `0x182285B50`、trampoline→実体`0x196556A50`)全体disasm完了(CONFIRMED)

`.analysis/disasm_rstchkskillact_real.py`・`real2.py`(前回セッション作成済み)で実体を全体disasmした。**`rstChkSkillAct`はskill選択やEventParamとは一切無関係で、`GBWK.MessageWait`(+0xC8)専用のメッセージ表示タイマーゲートである**:

```
rstChkSkillAct():
  if (GBWK.MessageWait <= 0):
      if (!0x18222c0e0(1,0,0)):   ; 何らかの表示要否判定(未解析、詳細スコープ外)
          return 0
      Notify(cl=1)                ; 0x1822dd280
      GBWK.MessageWait = 1
  ; MessageWait > 0 (前段からの続き、または元々>0だった場合)
  if (GBWK.MessageWait == 1):
      GBWK.MessageWait = 0
      return 2                    ; カウントダウン完了 → 呼び出し元は「今フレームだけ通知」扱い
  else:
      GBWK.MessageWait -= 1
      return 1                    ; カウントダウン中 → 呼び出し元は即return(何もしない)
```

`rstUpdateSeqDefaultSkill`にとって、この関数の役割は「前フレームの結果通知メッセージがまだ表示待ちでないか」を確認するゲートに過ぎない。**skillId決定ロジックとは完全に独立している。**

### UNRESOLVED(新規、次回最優先)

1. **`GBWK.EventParam`(+0x32)のwriter**: `rstUpdateSeqDefaultSkill`・`rstChkSkillAct`のいずれにも書き込みが無いことをbyte-exactで確認済み。有力な残り候補は`rstUpdateSeqDefaultSkill`のresult==0分岐内、`0x1816f9470`への呼び出し(VA `0x18228899F`、candidate配列走査+type-check付きcall)。**ただし`0x1816f9470`自体を逆アセンブルした結果、これはIL2CPPの「ジェネリックメソッド解決サンク」(`0x1800e69e0`への文字列リテラル付きicall解決 → 関数ポインタキャッシュ → `jmp rax`のクラスタ、既知の`0x1800Exxxxx`帯域と同種)であり、実際の呼び出し先は静的にはファイルから決定できない(icall解決はruntime依存)。** 静的解析でこれ以上追うには、(a) 同じ配列/type-checkパターンを持つ別の呼び出し元を探して間接的に絞り込む、または(b) より広いxrefスキャンで`EventParam`(+0x32)への書き込みパターンを持つ命令列を候補関数群から機械的に探す、のいずれかが必要。**まだ試していない。**
2. `GBWK.SeqInfo.+0x14`(=1書き込み対象、forget-flow分岐と通常分岐で共通)の意味論。
3. `0x18222c0e0`(rstChkSkillAct内の表示要否判定)・`0x1827c0a90`(result==2分岐、skillIdから何かを得る関数)の役割。
4. AL==1分岐(`rstChkSkillAct`が1を返すケース)の意味論(`rstAddSkill`は呼ばれず別のtailcallへ抜ける、詳細未解析)。
5. `[chain]+8`と`[chain]+0xd0`(result==1分岐とresult==0分岐でそれぞれ参照する、GBWK自身とは別の静的オブジェクトの+8/+0xd0)の実体。既知の`TARGET`(`0x182e4ed30`)/`ACTION`(`0x182e46930`)との対応関係は未確認。

### 現状フレーム(更新)

- **`rstAddSkill` helper解析 = CONFIRMED**(`cmbAddSkill`/`cmbChkKeisyoSkillOwner`/`cmbDeleteKeisyoSkill`と厳密一致)
- **`pCurrentStock+0x78` = `keisyoskill`(フュージョン継承待機リスト)= CONFIRMED**(型定義+API群で裏付け、`rstSkillInfo_t`との同一性HYPOTHESISはREJECTED)
- **`sourceObj+0x32` = `EventParam`、`+0x34` = `GetHeartsSkill`(無関係の別subsystem)= CONFIRMED(フィールド識別)**
- **Mutation target staging先 = 依然UNKNOWN**: `EventParam`のwriterが未特定のため、「MOD側で`PUpSkillID`を`EventParam`へstagingすればよいか」の設計評価はまだ行えない。writer特定が次の最優先。

## CONFIRMED(2026-09-15、続き — `GBWK.EventParam`のwriter確定)

User指示の優先順位に従い、既存の`.analysis/search_gbwk_plus0x32.py`(2026-09-12に前回セッションで作成済み・実行済みだったキャッシュ`gbwk_plus32_hits.txt`を再利用、GBWK static slot経由で`+0x32`を参照する全11箇所を機械的に列挙)を読み、新規に`.analysis/resolve_va_to_name.py`(VA→managed method名の逆引き)・`.analysis/resolve_nearest_method.py`(あるVAを含む管理メソッドの特定)を作成してIL2CPPメタデータで裏付けた。

### 8. `GBWK.EventParam`(+0x32)のwriterは`rstcalc.rstCalcEventInfo(ref UInt16 pParamBuf)`(VA `0x18227C330`)

`gbwk_plus32_hits.txt`の`site VA 0x18227ED9B`(`rstcalc.rstCalc`本体内、VA `0x18227E710`から`0x68B`バイト先)に、以下の**リトライループ**を発見した(byte-exact、`rstCalc`は`rstUpdate`とは別の「計算(Calc)」パス関数であり、`rstCalcSkillPowerUpCore`と同じく`rstCalc`内から呼ばれる):

```
rstCalc()内、DefaultSkill候補決定ループ:

loop_entry (VA 0x18227EDC0):
  rcx = &GBWK.EventParam                    ; GBWK chain + 0x32
  edx = 0
  al = rstCalcEventInfo(ref rcx, edx)        ; ★VA 0x18227C330 呼び出し、ref引数でEventParamへ直接書き込む★
  if (al < 0):  goto ABORT                   ; VA 0x18227EDCB(js)
  if (al == dil): goto CHECK_HENSINMAE_A     ; VA 0x18227EDD6(dil=ループ外で確定済みの比較値、出自未解析)
  if (al != 6):   goto RETRY_PREP            ; VA 0x18227EDDA
  ; al == 6:
  if (pCurrentStock.hensinmae != 0): goto USE_RESULT   ; VA 0x18227EE11(jmp ee44)
  else: goto RETRY_PREP                                 ; VA 0x18227EE0F(je ee7d)

CHECK_HENSINMAE_A (VA 0x18227EE13):
  if (pCurrentStock.hensinmae != 0): goto RETRY_PREP    ; VA 0x18227EE42(jne ee7d)
  ; hensinmae == 0 → fallthrough
USE_RESULT (VA 0x18227EE44):
  skillId = GBWK.EventParam                              ; VA 0x18227EE4D(再read、rstCalcEventInfoが書いた値)
  al2 = rstChkAddSkill(skillId)                           ; ★VA 0x18227F940 呼び出し(既知、MEMORY.md「stateless」)★
  GBWK.DefSkillResult = al2 (または0)                     ; ★VA 0x18227EEA7、下記参照★
  if (al2 != 0): goto LOOP_EXIT (success)                 ; VA 0x18227EE5D(jne ee9e)
  ; al2 == 0:
  GBWK.LevelUpCnt -= 1                                    ; VA 0x18227EE79
RETRY_PREP (VA 0x18227EE7D):
  ; GBWK再read
  goto loop_entry                                          ; VA 0x18227EE97(jmp edc0)

ABORT (VA 0x18227EE9C):
  cl = 0
  GBWK.DefSkillResult = 0                                  ; VA 0x18227EEA7経由(cl=0の場合)
  goto LOOP_EXIT
```

**確定事項(CONFIRMED、byte-exact + IL2CPPメタデータVA解決)**:

- `rstCalcEventInfo(ref UInt16 pParamBuf) -> sbyte`(cpp2il `rstcalc.cs`より正式シグネチャ確認済み、VA完全一致)が`GBWK.EventParam`の唯一確認できたwriterである。`ref`引数として渡された`&GBWK.EventParam`へ内部で直接書き込む(内部実装は未解析、スコープ外)。
- **このループは`rstUpdateSeqDefaultSkill`(Update/適用パス)ではなく`rstCalc`(Calc/計算パス)の中にある。** これは既知の`rstCalcSkillPowerUpCore`(同じく`rstCalc`内から呼ばれ、`PUpSkillID`/`PUpSkillResult`を計算する)と構造的に完全並行である: **Calc相当フェーズで候補を確定 → Update相当フェーズ(`rstUpdateSeqDefaultSkill`)で消費・適用、という2段パイプラインがDefaultSkillとSkillPowerUp/Mutationの両方で共通の設計パターンである。**
- **重要(User懸念への回答)**: このループは`EventParam`を書くだけでなく、**同じループ内で`GBWK.DefSkillResult`(+0x3C)も設定する**(`rstChkAddSkill(EventParam)`の戻り値、または早期abort時は`0`)。`rstUpdateSeqDefaultSkill`は`DefSkillResult==2`で forget-flow へ分岐することが既に確定しているため、**`EventParam`単体をstagingするだけでは`rstAddSkill`が正しく呼ばれる保証がない。`DefSkillResult`が「forget-flow行き」の値(`2`)にならないよう、native自身と同じ意味論で用意する必要がある。** これはUserが事前に懸念していた点そのものであり、CONFIRMEDな根拠を伴って裏付けられた。
- `rstChkAddSkill`(VA `0x18227F940`)は引数`SkillID`のみを取り、内部でGBWK/pCurrentStockへの書き込みは無い(`MEMORY.md`記載の「stateless」と整合)。`al2`の意味論(0/2等の具体的なコード値の対応)は本investigationでは未解析(スコープ外、別途349-zombie系investigationの範囲)。

### UNRESOLVED(新規)

1. `dil`(ループ内の比較値、`cmp al,dil`)の出自。`rstCalc`冒頭〜このループまでの間で確定していると推測されるが未追跡。
2. `rstCalcEventInfo`自身の内部実装(どのcurriculumテーブルから何を選ぶか、`hensinmae`によるtag1/tag6分岐との対応関係)。
3. `rstChkAddSkill`の戻り値(al2)の具体的な意味論(0/1/2等が何を表すか)と`DefSkillResult`との対応表。
4. 上記ループが**どのタイミング(どのseq、何フレーム前)で実行されるか**。`rstCalc`と`rstUpdate`の呼び出しタイミング関係(同一フレーム内か、次フレームか)は未確認。

### 現状フレーム(最終更新)

- **`rstAddSkill` helper解析 = CONFIRMED**
- **`pCurrentStock+0x78` = `keisyoskill` = CONFIRMED**
- **`sourceObj+0x32/+0x34` = `EventParam`/`GetHeartsSkill` = CONFIRMED(フィールド識別)**
- **`EventParam`のwriter = CONFIRMED**: `rstcalc.rstCalcEventInfo(ref UInt16)`(VA `0x18227C330`)、`rstCalc`内のリトライループから呼ばれる。
- **`DefSkillResult`も同じループで設定される = CONFIRMED**: Mutation AddNewの設計評価には`EventParam`のstagingだけでなく`DefSkillResult`の扱いも必須。
- **Mutation AddNewのPoC設計評価 = 次のステップとして着手可能**(ただしまだ未着手、User承認待ち)。

## CONFIRMED(2026-09-15、続き — `rstChkAddSkill`戻り値↔`DefSkillResult`意味論の完全確定)

User優先順位(1. `rstChkAddSkill`戻り値意味論 → 2. `rstCalcEventInfo`内部/`dil` → 3. Calc→Updateタイミング → 4. PoC設計評価)に従い、`rstChkAddSkill`(VA `0x18227F940`、trampoline→実体`0x19653FBE0`)を全体disasmした。

### 9. `rstChkAddSkill`の内部ロジック(CONFIRMED、byte-exact)

```
rstChkAddSkill(SkillID):
  al = cmbChkSkillOwner(SkillID, pCurrentStock, 0)   ; ★VA 0x182410660、下記参照★
  if (al >= 0):                                       ; VA 0x19653FC2B(jns) — "所有している"側
      return 0
  ; al < 0(符号bit set) — "所有していない"側:
  if (pCurrentStock.skillcnt >= 8):                    ; VA 0x19653FC4C(cmp [rax+0x48],8)
      return 2
  else:
      return 1
```

**`0x182410660`の正体もIL2CPPメタデータVA解決で確定**: `fclCombineCalcCore.cmbChkSkillOwner(UInt16 SkillID, datUnitWork_t pStock) -> sbyte`。**これはPower-Up/Mutation側パイプライン(`rstCalcSkillPowerUpCore`のpromotion-check、`01_CURRENT_STATE.md`既知の`cmbChkSkillOwner`call site群)と全く同一のmanaged method**である(`cmbChkKeisyoSkillOwner`とは別物、混同注意——こちらは`keisyoskill`ではなく通常の`skill[]`配列に対する所有チェック)。

### 10. `DefSkillResult`の意味論表(CONFIRMED、User事前確認内容と完全一致)

`rstChkAddSkill`の戻り値がそのまま(または早期abort時は`0`固定で)`GBWK.DefSkillResult`(+0x3C)へ書き込まれることは前セクションで確定済み。したがって:

| `DefSkillResult` | 意味 | `rstUpdateSeqDefaultSkill`での扱い |
|---|---|---|
| `0` | 候補skillIdを**既に所持**(duplicate) | `rstCalc`のループ内で消費される値であり、`rstUpdateSeqDefaultSkill`到達時点では通常観測されない(下記UNRESOLVED参照。ループはal2==0のとき退出せずRETRY_PREPへ進むため) |
| `1` | 未所持 かつ `skillcnt<8`(空きあり) | `rstAddSkill()`を呼ぶ通常経路 |
| `2` | 未所持 かつ `skillcnt>=8`(満杯) | forget-flow(`CursorPos.Shift=8`→`SeqInfo.Current=21`)へ分岐 |

**User事前確認(Power-Up側)との整合性**: 「owned→0」「unowned&skillcnt<8→1」「unowned&skillcnt>=8→2」という記憶は、DefaultSkill側(`rstChkAddSkill`経由)でも寸分違わず同一であることがbyte-exactに確認できた。**Power-Up側とDefaultSkill側は同じ`cmbChkSkillOwner`という共通primitiveの上に、それぞれ薄いラッパー(`rstChkAddSkill`と、Power-Up側の対応する関数)を被せているだけ**と分かる。

### UNRESOLVED(更新)

- `al2==0`(duplicate)の場合、`rstCalc`のループはRETRY_PREPへ進み次候補を試すため、**`DefSkillResult==0`が`rstUpdateSeqDefaultSkill`側で観測されることは通常ない**はずである(ループが自己解決するため)。ただし早期abort経路(`rstCalcEventInfo`が負値を返した場合)でも`DefSkillResult=0`が書かれるため、この場合は`rstUpdateSeqDefaultSkill`が`DefSkillResult==0`かつ`!=2`の状態で到達し、**通常分岐(`rstAddSkill`呼び出し)へ進んでしまう可能性がある**(この場合`EventParam`が有効な値を持たない/無効な状態でも`rstAddSkill`が呼ばれてしまうリスク、要runtime検証)。
- **Mutation AddNewへの設計含意(新規)**: MOD側でMutation経路をDefaultSkillパイプラインへ合流させる場合、`DefSkillResult`は`cmbChkSkillOwner(target, pCurrentStock)`と`pCurrentStock.skillcnt`から**native同一ロジックで計算して設定する**のが最も安全(native semanticsから逸脱しない)。単純に固定値`1`を書く設計は、target skillが偶然既所持だった場合や満杯だった場合にnative既知の安全策(duplicate skip / forget-flow誘導)を迂回してしまうため推奨しない。

## CONFIRMED(2026-09-15、続き — `dil`の出自、および`rstCalcEventInfo`内部ロジック完全解明)

### 11. `dil`の出自 = コンパイル時定数`0`(CONFIRMED、byte-exact)

`rstCalc`の全体disasmキャッシュ(`.analysis/scratch_rstcalc_full.txt`)を遡って確認した。`dil`はループ開始(VA `0x18227EDC0`)よりかなり手前、VA `0x18227E899`で

```
0x18227E899  4032ff   xor  dil, dil     ; dil = 0
```

により一度だけ確定し、この関数のこの分岐(switch-case、`SeqInfo`または類似の値による`jmp [table+ecx*4]`ディスパッチ先の1ケース)全体を通じて**一度も再代入されない**。他の全ての出現箇所(`test dil,dil`、`movzx ecx,dil`、`cmp al,dil`等)は全て読み取りのみである。

**結論**: `rstCalc`のリトライループ内`cmp al, dil`は実質的に**`cmp al, 0`**であり、`dil`は動的parameterではなくコンパイル時定数`0`である。「dilが呼び出し元次第で変化する」という当初の懸念は不要だった。

### 12. `rstCalcEventInfo`(VA `0x18227C330`)内部ロジック(CONFIRMED、byte-exact)

trampolineではなく実体そのもの(prologueが直接始まる)。全体をbyte-exactで解析した(`.analysis/disasm_rstcalceventinfo_full.py`・`_tail.py`)。構造:

```
rstCalcEventInfo(ref UInt16 pParamBuf):     ; r12 = &pParamBuf(呼び出し元から見て&GBWK.EventParam)
  GBWK = ...; WorkStock = GBWK.WorkStock(+0x68)
  if (pCurrentStock.level(+0x24) < WorkStock.level(+0x24)):
      return 0xFF (= -1, sbyte)              ; VA 0x18227C5C7、rstCalc側の「al<0 → ABORT」に対応

  if (GBWK.EventNums(+0x30) == 0):
      ; ★(再)スキャンフェーズ★ tblSkill.Get(WorkStock.id) で該当悪魔のcurriculumテーブルを取得
      rows = tblSkill.Get(WorkStock.id)        ; ★VA 0x181717DD0、cpp2il実名確認済み★
      matchCount = 0; firstMatchArrayPtr = null
      for i in 0..23:                          ; 24要素固定scan(既知パターンと同型)
          if (rows[i].+0x10(byte) == WorkStock.level(byteとして)):
              if (matchCount == 0): firstMatchArrayPtr = rows[i]の所属array (rows自体)
              matchCount++
      GBWK.EventNums = matchCount               ; VA 0x18227C504
      GBWK.pEvent = firstMatchArrayPtr           ; VA 0x18227C508(実際にはrows配列そのもの)

  ; ★消費チェックフェーズ★
  if (someStaticByte + GBWK.EventNums == GBWK.EventOfs(+0x31)):
      ; 全消費済み → 次levelへ進めて再スキャンさせる
      GBWK.EventNums = 0; someStaticByte = 0
      if (WorkStock.level < 0x63(99)):
          WorkStock.level += 1
      goto 関数先頭(再帰的にrescan)             ; VA 0x18227C5C1(je 0x18227c372)

  ; ★消費フェーズ★(未消費エントリが残っている場合)
  entry = GBWK.pEvent[GBWK.EventOfs]             ; bounds-checked
  pParamBuf = entry.SkillID(+0x12、ushort)        ; ★VA 0x18227C5FB: mov word ptr [r12], ax ★ ← 唯一のEventParam書き込み命令
  GBWK.EventOfs += 1                              ; VA 0x18227C623
  consumedEntry = GBWK.pEvent[旧EventOfs]
  return consumedEntry.+0x11(byte)                ; VA 0x18227C63B、rstCalc側で`dil(=0)`/`6`と比較される値
```

**確定事項**:

- `pParamBuf`(=`GBWK.EventParam`)への書き込みは**この1箇所のみ**(VA `0x18227C5FB`)。書き込む値は`GBWK.pEvent`配列(=`tblSkill.Get(WorkStock.id)`が返すcurriculumテーブル行配列)の`GBWK.EventOfs`番目要素の`+0x12`(ushort、スキルID)。
- `rstCalc`のリトライループで比較される`al`(戻り値)は、**同じ消費対象エントリの`+0x11`(byte)フィールド**であり、汎用的なstatus codeではなく**curriculumテーブル行ごとのタグ/分類byte**である。「`dil(=0)`/`6`」という比較値は、このタグbyteの意味論に対応する(既存`HIDDEN_SKILL_ENTRY`investigationのcurriculum tag概念(tag=1/tag=6グループ)と構造的に類似するが、**同一の値域・意味かは未確認のためCONFIRMEDでの同一視はしない**、下記UNRESOLVED参照)。
- **curriculumテーブル行の推定構造(未完全確定、位置のみCONFIRMED)**: `+0x10`(byte、フィルタ条件=level一致判定に使用)、`+0x11`(byte、消費後の戻り値=rstCalc側のタグ判定に使用)、`+0x12`(ushort、SkillID)。
- `WorkStock`(GBWK+0x68)は「levelを進めながらcurriculumを消化していくための作業コピー」として機能する。**消費しつくすと`WorkStock.level`をインクリメントして関数先頭から再実行する**(99が上限)。これは`rstCalc`側リトライループ(LevelUpCnt--を伴う)とは**別のリトライ機構**であり、二重のretry構造になっている(`rstCalcEventInfo`内部: level単位のrescan、`rstCalc`側: 個々のcandidate skillId単位のリトライ)。

### UNRESOLVED(新規)

1. `someStaticByte`(`rcx`経由でアクセスされる、GBWK以外の単一byte静的フィールド)の正体・意味論。「epoch/世代カウンタ」的な役割と推測されるが未確認。
2. `rows[i].+0x10`==levelでのマッチにおいて、`matchCount>1`の場合(複数該当時)にどのエントリが実際に選ばれるか(`pEvent`は配列全体を指すため、`EventOfs`による逐次消費で全マッチが順番に処理されると見られるが、複数マッチ時の順序保証は未確認)。
3. curriculumテーブル行の`+0x11`(タグbyte)の値域と、既存`HIDDEN_SKILL_ENTRY`のcurriculum tag(tag=1/tag=6)との対応関係(同一concept上の別表現か、別のtag体系か)。
4. `tblSkill.Get(id)`が返す配列の完全なレイアウト(cpp2il型定義未確認)。

### 13. `rstCalc`↔`rstUpdate`呼び出しタイミング関係(UNRESOLVED、static analysisの限界に到達)

実行可能セクション全域(`.text`)に対して`rstCalc`(VA `0x18227E710`)・`rstUpdate`(VA `0x18228CDE0`)への**直接call命令(E8 rel32)のxrefを機械的に全件スキャン**したが、**いずれも0件**だった(`.analysis/find_callers_of.py`)。

**結論**: `rstCalc`/`rstUpdate`はいずれも直接callされておらず、**間接呼び出し(関数ポインタテーブル経由)でdispatchされている**。両者とも引数`dds3ProcessID_t PID`を取ることから、汎用的な「process」scheduler(DDS3エンジン共通の仕組みと推定)によって毎フレーム呼ばれる登録関数である可能性が高い。

**この設計のため、「同一フレーム内でCalc→Updateの順に呼ばれるか、次フレームか」という問いは、xrefスキャンのような静的解析だけでは決定できない。** scheduler側のprocess登録テーブル・呼び出し順序ロジックまで遡って解析するか、runtime観測(`rstCalc`/`rstUpdate`双方にフレーム番号付きのread-onlyトレースを仕掛けて実測する)のいずれかが必要になる。**まだ試していない。static analysisの範囲内でこれ以上詰められる部分はここまで。**

## CONFIRMED(2026-09-15、続き — runtime観測: Calc→Update timing、および`rstUpdateSeqDestroyConfirm`の新発見)

static analysisの限界(項目3、間接dispatchでxref不能)を受け、User指示によりruntime観測へ切り替えた。`src/SkillMutationV3/MutationAddNewCalcUpdateTimingTrace.cs`(新規、read-only Harmony Prefix/Postfix、hardware breakpoint不使用)を実装・build・deploy(SHA-256: `6317355080277a0e960e17b96308e85f64fdaee1a73647359b0a30990231960a`、source/deploy一致確認済み)し、4箇所(`rstCalcEventInfo`後・`rstGetDefaultSkill`後・`rstUpdateSeqDefaultSkill`入口・`rstAddSkill`入口)にフレーム/seq/unit/EventParam/DefSkillResultを出力するフックを設置した。User実機テスト1回分(`Latest.log`)を解析した。

### 14. Calc→Update timing(CONFIRMED runtime、2パターン判明)

**パターンA(空きあり、`DefSkillResult=1`)**: 同一frame内で完結する。unit=103の観測例(frame=10344)で、`UpdateSeqDefaultSkill.Prefix`と`AddSkill.Prefix`が**同一frame番号**で発火した。

**パターンB(満杯、`DefSkillResult=2`)**: Calcフェーズで確定した`EventParam`/`DefSkillResult`は、forget-flow UIが解決するまで**数百フレーム(観測値: 約605フレーム、実時間約10秒)にわたってGBWKに保持され続ける**。unit=59の観測例:
- frame=5668〜5673: Calc確定(`EventParam`が最終的に396、`DefSkillResult=2`)
- frame=5673〜5774: `UpdateSeqDefaultSkill.Prefix`が同一値(`eventParam=396; defSkillResult=2`)を保持したまま複数回(約4〜5frame間隔)発火し続ける(forget UI表示中と推定)
- **frame=6275**(約600フレーム後): `AddSkill.Prefix`が発火。ただしこの時点で`seq=22`(下記15番参照、重要な新発見)

**結論(CONFIRMED runtime)**: CalcフェーズとUpdateフェーズは**同一フレーム内で密結合していない**。`GBWK.EventParam`/`GBWK.DefSkillResult`は、forget-flowという「プレイヤー入力待ち」を挟んでも壊れない永続stateとして機能する。**Mutation AddNewの設計は、この「native自身が既に持つ複数フレーム永続化パターン」をそのまま踏襲すべきである**(Mutation側で独自のlifecycle管理機構を新設する必要はなく、GBWK.EventParam/DefSkillResult相当のフィールドへ書いて放置すれば、native自身が責任を持って後続フレームで消費してくれる)。

### 15. `rstUpdateSeqDestroyConfirm`(seq22ハンドラ)が`rstAddSkill`を直接呼んでいる(CONFIRMED、新発見、旧UNRESOLVED「seq22→8復帰時のinternal write確認」に直接回答)

`rstAddSkill`(VA `0x182285A40`)への直接call xrefを全`.text`スキャンで再確認したところ、2箇所判明した(`.analysis/find_callers_of.py`):

- `0x1822889E4`(`rstUpdateSeqDefaultSkill`内、既知、DefSkillResult!=2の通常経路)
- `0x182288F69`(**新規確認**)

**`0x182288F69`の所属関数をIL2CPPメタデータで解決した結果、`rstupdate.rstUpdateSeqDestroyConfirm`(VA `0x182288B20`)であることが判明した。** 旧仮定(「`rstUpdateSeqDestroyConfirm`は次関数`rstUpdateSeqDestroySkill`(VA `0x1822890D0`)の直前、小さい関数」)は誤りで、実際には`0x182288B20`から`0x1822890D0`直前まで(約`0x5B0`バイト)の大きな関数だった。

`0x182288F69`直前の逆アセンブル(`.analysis/disasm_rstaddskill_secondcallsite.py`、VA `0x182288E65`〜`0x182288F69`)により、以下が判明した(CONFIRMED、byte-exact):

```
(pCurrentStock.skill[]から、忘れた対象skillを除いた要素を前方に詰める圧縮ループ)
  for (ecx = 0; ecx < pCurrentStock.skillcnt; ecx++):
      skill[ecx] = skill[ecx+1]  相当のshift処理(int32配列、+0x50、stride4)
  pCurrentStock.skillcnt -= 1                    ; VA 0x182288F66(mov [rdx+0x48], eax)
  rstAddSkill()                                   ; ★VA 0x182288F69★
  GBWK.Flag = 1                                   ; VA 0x182288F88
  (以降、通知処理・0x18216a170呼び出し・"GBWK.SeqInfo.Current = 0x15(21)"書き込み(VA 0x182289059)等が続く、詳細は下記UNRESOLVED)
```

**結論(CONFIRMED)**: `rstUpdateSeqDestroyConfirm`(seq22、forget選択の確定操作)は、native自身の「忘れた分の配列圧縮 → skillcnt-- → **`rstAddSkill()`呼び出し**」という一連の処理を単一関数内で行っている。この`rstAddSkill()`呼び出し時点で`GBWK.EventParam`は**Calcフェーズ(frame=5668〜5673)で書かれた値のまま変化していない**(runtime観測、eventParam=396がpoint4でも同一)。**これはnative自身が「forget確定 → 空いた分でDefaultSkillを挿入」という一体化した処理を行っていることの直接証拠であり、Mutation AddNewが本来乗るべき自然な合流点はこの`rstUpdateSeqDestroyConfirm`内`rstAddSkill()`呼び出し(VA `0x182288F69`)である可能性が高い**(まだ設計候補の提示・実装PoCには進まない、あくまでstatic/runtime evidence)。

### UNRESOLVED(新規)

1. **point1(`CalcEventInfo.Postfix`)がEventParamの実際の変化を全て捕捉できていない**: 複数episodeで、`point1`ログの最後の値と、直後の`point3`(`UpdateSeqDefaultSkill.Prefix`)が示す`EventParam`値が食い違っており(例: unit=59で387→396、unit=103で349→60)、この間にEventParamを書き換えたはずの`rstCalcEventInfo`呼び出しが`point1`ログに出現しない。Harmonyパッチ自体は正常適用(`GETDEFAULTSKILL-PATCH-STATUS`等で確認可能な仕組みと同様、今回は`rstCalcEventInfo`側は前回セッションの`DefaultSkillIteratorTrace`(`Enabled=true`のまま)が同時に同一メソッドをフックしており、そちらのログ(`DEFAULTSKILL-ITERATOR-CALL`)でも同じフレームに該当する変化が確認できなかった)。**原因未特定** — 有力候補: (a) `rstCalcEventInfo`のもう一つの既知呼び出し元(`rstCalcEvo`、進化イベント経由、VA `0x18227C6E0`)からの呼び出しである可能性、(b) 未発見の別writerが存在する可能性、(c) ログ出力自体の取りこぼし(MelonLogger書き込み頻度限界等)。**次回最優先。断定しない。**
2. `point2(GetDefaultSkill.Postfix)`が今回の実機テストで**一度も発火しなかった**。これは「`rstcalc.rstGetDefaultSkill`という管理メソッド自体が、通常のDefaultSkill取得フローでは呼ばれていない」ことを示唆する(Harmonyパッチ自体は適用済み、`GETDEFAULTSKILL-PATCH-STATUS; prefixes=1; postfixes=2`で確認済み)。前回セッションが「VA `0x18227EDC0`のretry loopは`rstGetDefaultSkill`である」とした識別(ISIL解析が例外を投げていたと前回セッション自身が記録)は、**今回のruntime観測により疑わしくなった**(該当loopは実際には別の呼ばれ方をしている可能性がある)。REJECTEDへの正式な降格は追加確認後に行う。
3. `rstUpdateSeqDestroyConfirm`内`0x182289059`(`SeqInfo.Current=0x15(21)`書き込み)が何を意味するか(seq22の処理中に再度21へ戻す?)は未解析。`0x182289011`以降のコード(`cmp al,1`分岐)が同一関数内の別ブロックなのか、隣接する別関数なのかも未確定(境界の再確認が必要)。

## 設計評価(2026-09-15、User承認: 候補B採用、実装はまだ)

Power-Up AddNew本番実装(`AddNewEmptySlotPoc.cs`/`FullCapacityAddNewBridgePoc.cs`/`DefaultSkillHandledClassificationBlock.cs`)を精読し、Mutation AddNewの設計候補3案(A: Power-Up既存インフラ直接拡張、B: 同一パターンをMutation専用に並行実装、C: DefaultSkillパイプラインseq8への完全合流)を比較した。

**User承認: 候補B(Power-Up本体ロジックは変更せず、同一native patternをMutation専用bridgeとしてzero-base再構成。ただしOrdinary/Mutation相互排他のためのguardのみ、Power-Up側Triggerへ最小限追加する)。** 候補Cは非推奨(native semanticsから乖離しやすく、DefSkillResultの意味論もseq21/22では参照されないため不要な複雑化になる)。候補Aは将来、Mutation側が実機で安定した後の統合リファクタリングとして検討する。

### 実装前に確定した3点

1. **`EventParam`のCANCELパス復元(CONFIRMED、全ソースgrep済み)**: Power-Up本番実装の`gbwk.EventParam=`書き込みは`FullCapacityAddNewBridgePoc.cs`の3箇所のみ(L792 stage、L888 idempotent再stage、L922 restore)。**CANCEL分岐(`FullCapacityAddNewBridgeMonitor.Postfix`のCANCEL側、`FULLCAP-ADDNEW-CANCEL`ログ箇所)には復元処理が無い** — forget UIをキャンセルした場合、`rstAddSkill`(seq22 call site)に到達しないためL888/L922が発火せず、`EventParam`がtarget値のまま取り残される。これはPower-Up本番実装の未文書化のギャップ。**Mutation側はこれを継承せず、COMPLETE/CANCEL両方の経路で明示的に`EventParam`をnative本来の値へ復元する設計とする。** Power-Up側は本番承認済みのため今回変更しない。
2. **Ordinary/Mutation bridge相互排他(設計方針確定)**: 単一frameでの`PUpSkillResult`共存は構造上不可能(排他的field)。リスクは「Mutation bridge active中にCore再評価でordinary側が誤起動しないか」。対策: 双方の`Trigger.Prefix`に相手側の`*BridgeState.Active`を追加でチェックする1行のOR条件ガードを入れる(Power-Up側への変更は非破壊的な追加のみ、既存ロジック不変更)。
3. **`Flag`/`PUpSkillResult`のseq21への持ち越し(明文化)**: Mutation bridgeはこれらのfieldを一切触らない。native自身の`rstUpdateSeqSkillPowerUp` tailが`Flag=2`/`PUpSkillResult=0xFF`を自分で確定させ、Mutation bridgeのseq21 redirectは native dispatcherがseq10から自然に離れた後(=native tail完走後)にのみ行うため、redirect時点で両fieldは既にnativeにより確定済み。seq21/22側のnativeコードはこれらを参照しないため、持ち越し/クリアいずれも不要。

### 訂正(User指摘)

「`DefSkillResult`は不要」という表現は不正確だった。正しくは: **「Mutation bridgeが候補Bの設計(native seq10 bodyを完走させ、既存のseq21/22完了経路へ途中合流する)を採る場合は、新たにDefSkillResultを書かなくてもよい可能性が高い」** — これは候補Bの具体的設計に条件付けられた結論であり、Mutation AddNew一般についての普遍的結論ではない。

### 既知gap記録(Power-Up本番実装、今回は修正しない)

**`FullCapacityAddNewBridgePoc.cs`の満杯側bridgeは、forget UIをCANCELした場合に`GBWK.EventParam`をnative本来の値へ復元しない(全ソースgrepで確認済み、上記参照)。** Mutation側は最初からCOMPLETE/CANCEL両方で明示的にrestoreする設計とするが、Power-Up側はREADY FOR PRODUCTION済みのため今回は変更しない。Mutation AddNewが安定した後、同じcleanupをPower-Up側にも入れるかは別途判断する。

### 次

2026-09-15、User承認によりPoC実装を開始した(候補B、満杯8枠+Mutation result=2のケースを最優先で検証)。詳細は`src/SkillMutationV3/MutationFullCapacityAddNewBridgePoc.cs`等のコード自体、および実機テスト結果を参照。

## PoC実装完了(2026-09-15、候補B、build/deploy済み、実機未検証)

新規ファイル:

- `src/SkillMutationV3/MutationFullCapacityAddNewBridgePoc.cs`: 満杯側bridge。`FullCapacityAddNewBridgePoc.cs`と同一パターンをMutation専用(`PUpSkillResult==2`)にzero-base再構成。既知gapの修正(COMPLETE/CANCEL両方で`EventParam`を明示的にrestore)を含む。`MutationAddNewBridgeState`/`MutationAddNewBridgeArming`/`MutationFullCapacityAddNewGate`/`MutationFullCapacityAddNewBridgePendingRedirect`という専用state群を持ち、Power-Up側のstateとは完全分離。
- `src/SkillMutationV3/MutationAddNewEmptySlotPoc.cs`: 空きあり側bridge。`AddNewEmptySlotPoc.cs`と同一パターンをMutation専用(`PUpSkillResult==2`)にzero-base再構成。

既存ファイルへの最小限の追加(Power-Up本体ロジックは不変更):

- `FullCapacityAddNewBridgePoc.cs`(`FullCapacityAddNewBridgeTrigger.Prefix`): `if (MutationAddNewBridgeState.Active) return;`を1行追加。
- `AddNewEmptySlotPoc.cs`: 同様に1行追加。
- `HiddenSlotCandidateInjection.cs`: `FullCapacityAddNewBridgeState.Active`単独チェックを、`FullCapacityAddNewBridgeState.Active`または`MutationAddNewBridgeState.Active`のいずれかを認識するよう変更(両者は構造上排他的なため同時trueにはならない)。

Clean build(error 0、warning 9、既存warningのみ、今回変更と無関係)。Deploy先: `Mods\NocturneModernGameplay.dll`。SHA-256(source/deploy一致確認済み): `da4364fc260ecbaec463bbe3171f45623642f8c805c5265ec230d3c2d29fe831`。

### CONFIRMED runtime(2026-09-15、初回実機テスト、3ケース全て成功)

`Latest.log`を解析した。エラー・警告(`failed safely`系)は0件。

- **unit=59(満杯8枠)**: `36:ディア→322:耐精神`。GATE-ARM→GATE-OPEN→BEGIN→REDIRECT-DEFERRED→ENTER-FORGET(seq21)→MUTCAP-INJECT(seq22)→MUTCAP-RESTORE→**MUTCAP-ADDNEW-COMPLETE**(`sourcePreserved=True; targetPresent=True; skillcnt=8; pending32After=396`=native本来の値に復元済み)。
- **unit=60(満杯8枠)**: `13:ジオ→290:一分の活泉`。同一パターンで完全成功(`pending32After=349`)。
- **unit=103(空きスロット)**: `43:パトラ→400:物々交換`。`rstOverWriteSkill`の実書き込みに到達するまで165回(約7秒、Ordinary側の「~10回に1回」より明確に長い、原因未調査だが実害なし)かかったが、最終的に**MUTADDNEW-POC-COMMIT**成功(`skillCntBefore=7→8`、`skillsAfter=[...,400]`)。既存の`FORGET-*`系トレースでも独立に同一変化を確認。

**未実施(次回)**: CANCELテスト(forget UI未完了での離脱)、Ordinary Power-Up AddNewの回帰確認(このセッションでは`FULLCAP-ADDNEW-*`/`ADDNEW-POC-*`ログ0件、Ordinary側は発生しなかった)。

### バグ発見・修正(2026-09-15、User報告): 変身悪魔(hensinmae!=0)でMutation targetがハイライトされない

**症状**: unit=60(Frost)は9枠目に正しくMutation target(一分の活泉)が表示されたが、unit=59(High Pixie)ではlv up習得スキルがハイライトされ、Mutation target(耐精神)が表示されなかった。

**根本原因**: `HiddenSlotCandidateInjection.cs`の元々のガード(`if (pInfo.SkillCnt != 0) return;`)は、HIDDEN_SKILL_ENTRY投資調査時点の「nativeが自前で候補を見つけられない場合(hensinmae==0)だけ注入する」という設計のまま。Mutation AddNewブリッジがこの同じ注入点を共有した結果、native自身が別の候補を見つけてしまうhensinmae!=0のunit(High Pixie)では注入がスキップされ、9枠目にはbridgeのtargetではなくnative自身が見つけた無関係な候補が表示されていた。`Latest.log`で`HIDDENSLOT-INJECT`がunit=60でのみ発火し、unit=59では一度も発火していないことで確認。

**修正**: ガードを撤廃し、bridge active中は`SkillID[0]`を常にbridge targetで上書きする方式に変更(`cmpDrawSkill`はindex 0のみを読むためCONFIRMED済み — class comment参照)。`SkillCnt`はnativeが既に1以上を書いていればそのまま尊重し(未確認の別consumerへの影響を避ける)、0のときのみ1へ引き上げる。Ordinary Power-Up AddNew側(既存、本番)にも同じ根本原因が潜在的に存在する可能性がある(未検証、hensinmae!=0のunitでの満杯AddNewをまだテストしていない可能性がある)。

Clean build(error 0)・deploy済み。SHA-256: `1543a00fe6b6648d057664982eb36165512750bb8cc2c0905406498494cf33a0`。

### バグ発見・修正(2026-09-15、User報告 → 調査 → User訂正 → 根本原因確定): SkillPowerUp.Chance=Always + Repeat=Unlimitedで、bit6 SET後にMutationが変換されず表面化する

**症状**: `unit=59`(High Pixie)、`SkillPowerUp.Chance=Always / Repeat=Unlimited / SkillMutation.Chance=Native`という設定下で、5/5の成立イベント全てがMutation(native `PUpSkillResult=2`)として表面化し、Ordinary Power-Up(`=1`)が一度も表面化しなかった。

**調査の経緯(訂正含む)**: 当初「Mutation AddNew実装とは無関係の別機能(Option F)の未解決問題」と整理したが、User指摘により訂正。「Mutation AddNewのguardが正当なresult=1を握り潰している」という仮説は、native自身の生`PUpSkillResult`遷移ログ(`FORGET-PUPSKILL-CHANGE`、Mutation AddNewのguardより前段)を確認した結果否定された(`unit=59`はnative自身が`PUpSkillResult=1`を一度も生成していなかった)。しかしUser再訂正: 「native自身が2を出すこと自体は普通にあり得る。問題は、Chance=Alwaysの2→1変換層が正しく働いているか」という、より正確な期待仕様の整理により、真因が判明した。

**根本原因(CONFIRMED、既存コード読解)**: `SkillPowerUpChanceControl.cs`の`SkillPowerUpChanceAlwaysPatch.Postfix`に

```csharp
if (_bit6WasSet) return; // Repeat=Native: already power-upped this cycle - defer entirely
```

というコードがあり、**コード自身のコメントで「Repeat=Native only path in Phase A」と明記されているにもかかわらず、実際のコードは`GameplaySettingsService.Repeat`を一切チェックしていなかった**。Phase C(`OptionFRepeatUnlimitedControl.cs`、Repeat=Unlimited)が後から追加された際、Phase A(`SkillPowerUpChanceAlwaysPatch`)のこの条件がRepeatモードを考慮するよう更新されていなかった。

`OptionFRepeatUnlimitedControl`は`rawResult==0`(R0-A/B/C)のみを変換対象とし、`rawResult==2/3`には一切関与しない。したがって、bit6が既にSET(1周期内で既に1回成立済み)の状態で、native CFGの「RNG失敗(dil=1)→bit6を経由せず直接Mutation attempt」経路が`rawResult=2/3`を生成した場合、**どちらの変換層にもカバーされず、Mutationがそのまま表面化する**、というのが直接原因。今回のMutation AddNew実装とは無関係(このファイルは今回変更していない)。

**修正**: `if (_bit6WasSet && GameplaySettingsService.Repeat != "Unlimited") return;`に変更。Repeat=Nativeでは従来通り、Repeat=Unlimitedではbit6 SET後もMutation(2/3)→Power-Up(1)変換を継続する。`OptionFRepeatUnlimitedControl`(rawResult==0専用)とは役割が重ならないため競合しない。

Clean build(error 0)・deploy済み。SHA-256: `8af960e08e661c9e54a933d9874efabb098a99d5cb00512719c8342ce058d039`。

**CONFIRMED runtime(2026-09-15、実機確認済み)**: `unit=59`(High Pixie)で`FORGET-PUPSKILL-CHANGE`を確認したところ、3/3イベント全てが`pUpResultAfter=1`(Ordinary Power-Up)となり、Mutationは一度も表面化しなかった。`unit=60`/`unit=103`では`SkillPowerUpChance Always priority applied`(2/3→1変換)が複数回正しく発火していることも確認した。`[NocturneModernGameplay]`由来の`failed safely`ログは0件。

**残課題**: 同時にデプロイした`OptionFRepeatUnlimitedControl.cs`内の一時診断ログ(`OPTIONF-DIAG-EXCLUSION-HIT`/`OPTIONF-DIAG-TOTAL-CALLS`)は、今回の本修正で優先度が下がったため未活用のまま保留。`exclusionObserved`が常時Falseだった件自体は原因未確定のまま残っている(今回の修正はそれとは独立した別レイヤーの問題だったため、Repeat=UnlimitedのrawResult==0経路の挙動そのものには影響しない)。

### READY FOR PRODUCTION: NO(基本ケース3件は実機確認済み。hensinmae!=0でのハイライト修正・SkillPowerUp.Always/Repeat=Unlimited修正はいずれも未実機検証。CANCEL/回帰確認も残っている)

### CANCELパスの実機再現性について(2026-09-15、User報告)

forget-skill選択画面では、通常の操作ではスキルを選ばずに抜けることができない(User実機確認: 「そのまま忘れる以外出来ない」)。**native自身のUIがキャンセル入力を受け付けない設計である可能性が高い**(満杯状態で新規スキル習得が確定した以上、「どれを忘れるか」だけを選ばせ、「忘れない」選択肢自体が用意されていないと解釈できる)。

このため、`MutationFullCapacityAddNewBridgeMonitor`のCANCEL分岐(および対応するEventParam復元コード)は、通常のゲームプレイ操作では実質到達しない可能性が高い。防御的なコードとして残すが、実機での優先度は下げる。到達経路が判明すれば再度テストする。

### 次回実機テスト観点(User提示の成功条件)

最優先: 満杯8枠 + Mutation result=2 + AddNew。以下が通れば大成功:

```text
Mutation target確定 → 元skillがseq10で潰されない → Mutation presentationが正常
→ seq21 forget UIへ → hidden ninth entryが表示される → 1スキル忘れる
→ seq22 native compact → native rstAddSkill() → Mutation target習得
→ source skill preserved → skillcnt=8 → EventParam restore
```

その後: CANCEL、再突入、別Mutation target、Ordinary Power-Up回帰(誤起動しないことの確認)。

ログ確認: `MUTCAP-ADDNEW-*`(満杯側)、`MUTADDNEW-*`(空きあり側)をgrep。

## 重要な禁止事項(2026-09-15追加、User明示)

**`GBWK.EventParam`へのhardware write-breakpoint(VEH/Dr0-Dr7)による直接観測は、本investigationにおいて2回クラッシュした(前回セッションの`EventParamWriterCaptureProbe`、および今回セッションの`EventParamActualWriterTrace`)。以後、本investigationではこの手法を再使用しない。再開にはUserの明示承認が必要。**

- 前回(`EventParamWriterCaptureProbe`): セッション全体で無期限にarmし続ける設計で、高負荷なシーン遷移中と推定されるタイミングでクラッシュ。`Enabled=false`のまま保持。
- 今回(`EventParamActualWriterTrace`): arm直後、最初の実発火とほぼ同時にクラッシュ(`Latest.log`が例外メッセージ無しで突然途切れる、native-levelクラッシュの典型パターン)。**「短時間ならarmしても安全」という仮説も今回の結果により崩れた。** VEHハンドラ内で`stockPtr`/`seqInfoPtr`経由の追加ポインタ辿りを行っていたことが疑われる(他の安定動作しているprobe(`R13FetchAndWriteWatchTrace`等)はVEH内をregister比較のみに絞っている設計指針)。`Enabled=false`に戻し、`src/SkillMutationV3/EventParamActualWriterTrace.cs`にコメントで記録済み。

`GBWK.EventParam`のwriterは静的解析により`rstcalc.rstCalcEventInfo`(VA `0x18227C5FB`)とCONFIRMED済みであり、この結論自体は変わらない。今回のクラッシュはwriterの再確認作業中に発生したものであり、writer自体の同定結果を否定するものではない。

## CONFIRMED(2026-09-15、続き — writer再確認、static analysisのみ)

User指示により、hardware breakpointを使わず静的手法のみで残った疑問(`point1`ログが387→396等の変化を捕捉できなかった件)を再検証した。

### 16. `GBWK.EventParam`への全staticアクセス候補の再検証(CONFIRMED、第二のwriterは見つからず)

`.analysis/gbwk_plus32_hits.txt`(前回セッションが既に機械的に洗い出していた、GBWK static chain経由で`+0x32`を参照する全11箇所)を1件ずつ再確認した。

- 8箇所(`0x18227ED9B`/`0x18227EDE0`/`0x18227EE13`/`0x182281461`/`0x1822814B2`/`0x182285A5F`/`0x182285CC8`/`0x18228885B`)は、いずれも`GBWK`の正規3段chain(`[rip+off]→[+0xb8]→[+0]`)経由で`+0x32`を**読むだけ**(`movzx`系命令)であり、writeは無い。
- 残り3箇所(`0x18228B05A`/`0x18228B078`/`0x18228B098`)は**誤検出(false positive)と判明した**。これらは`rstUpdateSeqSkillPowerUp`のpresentation tail内にあり、GBWKの正規3段chainではなく**2段どまりのchain**(`[rip+off]→[+0xb8]`、最終`[+0]`デリファレンスを経ない別オブジェクト、DefaultSkillExternalStateProbeが「obj1」と呼んでいたものと同一)の`+0x68`から得た**別オブジェクト**への`+0x32`アクセスであり、`GBWK.EventParam`とは無関係(前回セッションのスキャンスクリプトが「GBWK chainの直後40命令以内に`0x32`という文字列が出現するか」という緩い条件で拾ったための誤検出)。

**結論**: 全`.text`スキャンの範囲で、`GBWK.EventParam`への書き込み命令は`0x18227C5FB`(`rstCalcEventInfo`内)の1箇所のみであることが再確認された。**第二のwriterは静的には見つからなかった。**

### 17. `rstCalcEvo`経由の呼び出し(VA `0x18227C6E0`)は今回のtimingテストで発火した可能性が低い(STRONGLY SUPPORTED)

`rstCalcEventInfo`への全直接call xrefを再スキャンした結果、既知の2箇所(`0x18227C6E0`、`0x18227EDC6`)のみで、第三の呼び出し元は存在しない(`.analysis/find_callers_of.py`で確認済み)。

`0x18227C6E0`を含む関数(`0x18227C680`〜、`rstCalcEvo`と推定、cctor guard付きprologueから始まる独立した関数)を逆アセンブルしたところ、**DefaultSkill側のretry loop(`dil(=0)`/`6`で受理判定)とは異なり、`cmp al,5`(タグ値5)で受理判定する、構造的に類似だが別のretry loop**であることを確認した(byte-exact)。これは「進化(evolution)時専用の別カリキュラムタグによるボーナススキル付与」ロジックと推定される。

今回のtimingテストセッション(`Latest.log`、unit=59/60/103のレベルアップ・SMART-AUTO戦闘)には進化イベントが発生した形跡がない(セッション中に観測されたのは通常のレベルアップのみ)。**したがって`rstCalcEvo`経由の呼び出しが、observed anomaly(387→396等の未捕捉遷移)の原因である可能性は低い**(ただし該当セッションのログは既に上書きされており、確実な反証はできないためSTRONGLY SUPPORTED止まりとする)。

### 18. 結論(User指示の項目4を適用): Harmony観測の不完全性として整理し、writerはCONFIRMEDのまま設計評価へ

第二のwriterは静的に見つからず、`rstCalcEvo`経由の可能性も低いと判断されたため、**`point1`(Harmony Postfixによる`rstCalcEventInfo`観測)が特定条件下で一部の呼び出しを取りこぼす、という観測手法側の不完全性として扱う**(IL2CPP interop環境でのHarmonyパッチが、ごく短時間に連続する呼び出しの一部を欠落させることは既知のリスクカテゴリであり、この projectでも他のprobeで同様の疑いが記録されている)。

**`GBWK.EventParam`のwriterは`rstcalc.rstCalcEventInfo`(VA `0x18227C5FB`)のままCONFIRMEDとする。** この結論への影響はない。Mutation AddNewの設計評価に進んでよい。

## 現状フレーム(2026-09-15、User優先順位1〜3完了 + runtime観測完了 + writer再確認完了)

- **1. `rstChkAddSkill`戻り値↔`DefSkillResult`意味論 = CONFIRMED**(`cmbChkSkillOwner`の符号 + `skillcnt>=8`判定、0/1/2の対応表確定)
- **2. `rstCalcEventInfo`内部 + `dil`出自 = CONFIRMED**(`dil`はコンパイル時定数0。`rstCalcEventInfo`はcurriculumテーブル`tblSkill.Get(WorkStock.id)`を消費し、`GBWK.EventParam`/`EventNums`/`EventOfs`/`pEvent`を管理する2階層retry構造と判明)
- **3. `rstCalc`↔`rstUpdate`タイミング関係 = UNRESOLVED**(間接dispatchのため直接xrefで決定不能。static analysisの限界)
- **4. Mutation AddNew PoC設計評価 = 着手可能な段階だが、項目3が確定していない状態での着手は「同一フレーム内合流」を前提にできないリスクを伴う**

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

## 関連事象への横断メモ(2026-09-17、記録のみ)

本investigationとは別スレッドだが、`rstUpdateSeqDestroyConfirm`(forget-confirm、seq21→22→8)のresultへの介入例として関連が深いため、ポインタのみ記録する。

`01_CURRENT_STATE.md` Phase G(1 LvUp内2回Skill Power-Up統合バグ / episode latch PoC)参照。`HandledCandidatesObserver`/`CoreReentryHandledCheck`(349-zombie対策、`08a06fe`)の`ALLOW-FIRST`が、FullCapacity AddNew完了(forget-confirm解決)後の同一level-up episode再entryを、candidate識別(`EventParam`)が変わることで素通ししてしまう統合バグをCONFIRMED。`stockPtr`単位のepisode-level success latch PoCを実装したが、`GBWK.LevelUpCnt`によるclear boundaryは同一戦闘内の多重lvupケースで未検証、READY FOR PRODUCTION: NO。次回優先はTest A/B(詳細はPhase G参照)。

本investigation(`Acquisition = Overwrite / LearnAsNew`)の設計評価自体には影響しない。
