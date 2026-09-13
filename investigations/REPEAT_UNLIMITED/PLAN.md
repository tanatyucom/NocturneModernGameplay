# Skill Power-Up Repeat = Unlimited Investigation

更新日: 2026-09-05(GitHub checkpoint `06fcf964b955e89f84521971836ea7718408e92c` 以降、未保全だった研究状態をcanonical文書へ復元)

## 参照優先順位

会話再開時は、この文書を読む前に必ず以下を先に読むこと。

1. `00_PROJECT_RULES.md`
2. `01_CURRENT_STATE.md`(Canonical State。本文書はそこに未昇格の分類も含む)
3. 本文書(`investigations/REPEAT_UNLIMITED/PLAN.md`)

`investigations/POWERUP_MUTATION_CFG/`は本investigationの前段(Power-Up/Mutation振り分けCFGそのもの)。本investigationはそこから派生し、「bit6を安全に繰り返し可能にするnative制御点」をzero-baseで調査している。

## 目的

`[SkillPowerUp] Repeat = Native / Unlimited`を実装するにあたり、Mutation.Chanceへ副作用を出さず、旧`RepeatableSkillPowerUp.cs`(現在`Enabled=false`)の単純復活でもない、安全なnative制御点を特定する。**今回のセッションではまだ実装しない。解析のみ。**

**表記規約(2026-09-12追加)**: 本文書で重要なskill IDに言及する際は、`SkillNameResolver`(`src/SkillMutationV3/SkillNameResolver.cs`)による名前解決結果を可能な限り併記する(例: `19:"ザン"`、`22:"マハザン"`)。名前解決はゲーム内`datSkillName.Get`経由の表示名取得であり、生IDのみの記述より人間が追跡しやすいため、今後追記する箇所から順次適用する(既存の生ID記述は書き換えず、新規追記分から適用する)。

## 現在の状態(2026-09-05)

- Option D: REJECTED
- Current candidate: Option F(F2方式)
- Option F implementation(result conversion): NOT STARTED(F2 observer diagnosticのみ実装・実機投入済み。`__result`/bit6/native state書き込みは一切なし)
- Blocking issue: R0-B-like runtime case(`exclusionMatched=True`)が未観測。R0-C-CANDIDATEは2件観測済み。
- READY FOR IMPLEMENTATION(result conversion): NO

詳細は下記「Option Catalog」参照。

### 更新(2026-09-12): R0-B runtime positiveがCONFIRMEDへ昇格

上記2026-09-05時点の「Blocking issue」(`exclusionMatched=True`が未観測)は解消した。実機で`exclusionMatched=True`(classification=R0-B)を1件観測した(詳細は下記「R0-B runtime positive確認(2026-09-12)」参照)。

- R0-B runtime positive: **CONFIRMED runtime observed**(旧: NOT OBSERVED)
- deterministic reproduction: 引き続き**UNRESOLVED**(今回のobserved pathは1件のみ。同一条件での再現性は未検証)
- F2 observer diagnosticの`exclusionSkillIDs`スナップショットが、実際にR0-B相当の事象を正しく検出したことを実機で確認した(discriminatorの実効性そのものが今回初めて実証された)
- READY FOR IMPLEMENTATION(result conversion): 依然として**人間承認待ち**(NO)。技術的なblockerは大きく後退したが、実装着手には別途明示的な承認が必要(下記「R0-B runtime positive確認(2026-09-12)」の再評価内容を参照した上で判断すること)。

### 更新(2026-09-12、同日追記): Option F result conversion 初回実機成功

人間承認を得て、Option F result conversionの初回candidate実装(`src/SkillMutationV3/OptionFRepeatUnlimitedControl.cs`)を実施し、実機投入した。初回実機テストで**R0-C conversion 2件・R0-B protection 1件**を観測した(詳細は下記「Option F result conversion 初回実機成功(2026-09-12)」参照)。

- Option F implementation(result conversion): **実装済み・初回実機検証PASS**(旧: NOT STARTED)
- Option F R0-C result conversion: **CONFIRMED runtime observed**(観測した2件について)
- Option F R0-B protection: **CONFIRMED runtime observed**(観測した1件について)
- production stability / universal correctness: **NOT YET CONFIRMED**(観測件数が少ない、設定組み合わせ網羅も未了)
- READY FOR PRODUCTION: **NO**

## CONFIRMED(01_CURRENT_STATE.mdへ既に昇格済み)

- bit6 SET/TESTはそれぞれ`rstCalcSkillPowerUpCore`内1箇所のみ(VA `0x18227E4E8`/`0x18227E40C`)。
- RNG失敗側(dil=1 bypass)はbit6と無関係にgenuine Mutation attemptへ進む。
- bit6 CLEARは`rstUpdateSeqSkillPowerUp`(VA `0x18228C770`)内に2箇所存在し、`GBWK.Flag∈{3,4}` かつ `State_182e31630+0x1C==1` のときのみ発火する。
  - Site1(Flag==4): VA `0x18228CCB2`、`and dword ptr [rax+0x10], 0xFFFFFFBF`、State+0x1C testは`0x18228CC9F`、State+0x1Cリセットなし。
  - Site2(Flag==3): VA `0x18228CD89`、同一命令、State+0x1C testは`0x18228CD60`、直後`0x18228CDAA`で`State+0x1C=0`へリセット。
- `State_182e31630+0x1C`のreaderは上記2箇所のみ。writer(`=0`)は`0x18228CDAA`のみ。
- `rstMotionReq`(本体VA範囲`0x182280d64`-`0x18228108e`)がpresentation motion request発行の中心関数である(副産物としてPRESENTATION_CONSUMER investigationにも有用)。**訂正**: `0x182281C30`自体は`rstMotionReq`ではなく、`rstMotionReq`が2回呼び出す`rstSmoothMotion`である。詳細は`01_CURRENT_STATE.md`および`investigations/PRESENTATION_CONSUMER/PLAN.md`参照。
- **Core内部の呼び出し順序(2026-09-05追加)**: Core entry → `0x18227E15A`(`rstRndGetPowerUpSkill`呼び出し) → `GBWK.PUpSkillIndex`/`PUpSkillID`確定 → bit6 test(`0x18227E40C`)。bit6 SETによる`return 0`は、candidate selection前に落ちる経路ではなく、candidate selection後にnative candidateが既に確定した状態でbit6 gateにより破棄される経路である(CONFIRMED static、`01_CURRENT_STATE.md`「bit6 gateとcandidate selectionの順序」参照)。
- **bit6状態と16-scan到達可能性の関係(2026-09-05追加)**: bit6 SET + dil=0 → `0x18227E40C`で`return 0` → 16-scan未到達。bit6 CLEAR + dil=0 → 16-scanへ到達 → 条件次第で`cmbGetMutationSkill`(CONFIRMED static、既存CFGと整合)。

#### `rstCalcSkillPowerUpCore`全return0 path一覧(2026-09-05、byte-exact disassembly、CONFIRMED static)

Core(VA `0x18227E100`〜`0x18227E507`のret)には、正常系の`return 0`(AL=0、`0x18227E5A9: xor al,al`への合流)経路が**3箇所のみ**存在する(それ以外に、GBWKやpCurrentStockがnullだった場合の防御的null-check→例外throw経路が複数あるが、これらは「returnする」のではなく例外を投げてクラッシュする経路であり、正常return0とは区別する)。

| ID | branch VA | 条件 | bit6 testより前/後 | PUpSkillID | 備考(native reason) |
|---|---|---|---|---|---|
| R0-A | `0x18227E184`(`je 0x18227e5a9`) | `PUpSkillID(GBWK+0x4E) == 0` | bit6 testより**前**(promotion-check・16-scanより前、`rstRndGetPowerUpSkill`直後) | `== 0`確定 | 「候補なし」。`rstRndGetPowerUpSkill`が有効なcandidateを一つも返さなかった。 |
| R0-B | `0x18227E293`(`je 0x18227e5a9`) | `PUpSkillID`が`rstCreateBeforeSkillList`(VA `0x182280460`、呼び出しは`0x18227E261`)が構築した除外配列のいずれかの要素と一致 | bit6 testより**前**(promotion-check・bit6 testより前。RNG rollより前ですらある) | `!= 0`(R0-Aで既に除外済み) | 「候補は存在するが、native側が明示的に除外対象と判定した」。ユーザー提起の「exclusion / rstCreateBeforeSkillList系return0」は**実在を確認した**。 |
| R0-C | `0x18227E410`(`jne 0x18227e5a9`) | `pCurrentStock.flag`(+0x10) bit6(`0x40`) SET | bit6 test**そのもの**。promotion-check(`0x18227E3D2`)の**後** | `!= 0`(R0-A/R0-Bを通過済み) | 既知のbit6 gate。**この経路に到達するにはRNG rollが「成功」側(dil=0)である必要もある**(dil=1のRNG-bypass、dil=2のroll-failureはいずれもbit6 test自体をbypassして`0x18227e4bc`へ直接jmpするため、R0-Cへは到達しない)。 |

Native成功系の他の戻り値(参考、return0ではない): `AL=1`(ordinary Power-Up、`0x18227E4EC`)、`AL=2`(genuine Mutation成立、cmbGetMutationSkill nativeResult!=0)、`AL=3`(Mutation attempt failure。dil=2〈roll結果が閾値外〉、またはcmbGetMutationSkillのnativeResult==0、の2経路が合流)。

**R0-B/R0-Cの一意識別に使える構造的事実(CONFIRMED static)**:
- R0-Bは**RNG rollより前**、R0-Cは**RNG rollが成功側(dil=0)だった場合のみ、promotion-check後**に到達する。両者は同一関数内で時系列上完全に分離した別々のbasic blockであり、CoreをPrefix/Postfixで外側からhookするだけでは(内部のどちらの分岐を通ったかという情報が関数境界の外に一切出てこないため)区別できない。
- **R0-Cは`bit6WasSet == true`のときにしか起こり得ない**(bit6 testの`jne`が真になる条件そのものが bit6 SET)。一方**R0-Bはbit6の状態に関係なく起こり得る**(bit6 testより前に判定されるため)。したがって、**`bit6WasSet == false`のときにrawResult==0だった場合、それはR0-Cではあり得ない**(R0-AかR0-Bのいずれかである)。これは`bit6WasSet==false`側の安全な除外にしか使えず、`bit6WasSet==true && rawResult==0`という本題のケース(R0-BとR0-Cが両方あり得る)そのものは解消しない。
- R0-B/R0-Cのいずれも、Core自身は**PUpSkillID/PUpSkillIndexへの書き込みを一切行わない**(読むだけ)。両経路とも観測可能な永続的side effectを残さないため、Postfix側から「どちらを通ったか」を後から状態差分で判定する手段(F3)は今回のCore自身の disassembly範囲では見つからなかった。

**`rstCreateBeforeSkillList`(VA `0x182280460`)の呼び出し構造(2026-09-05確認)**:
- Coreからの呼び出しは`0x18227E261`の1箇所(`r9=一時output object`、`rdx=pCurrentStock`、`r8d`/`ecx`は他フィールド由来の値、PUpSkillIDそのものは引数に含まれない=一般的な除外リストを構築するだけで、その後Core側のloop〈`0x18227E270`-`0x18227E29D`〉がPUpSkillIDとの一致を判定する)。
- `rstCreateBeforeSkillList`は全path(2つ目のcmbChkSkillOwner呼び出し`0x182280735`、およびexception throw padding領域まで、VA `0x182280460`〜`0x18228081D`)をbyte-exact disassemblyで確認済み(2026-09-05、F2 feasibility調査時)。書き込みは呼び出し元が渡した出力オブジェクト(`rstSkillInfo_t`、`+0x10`カウント/`+0x18`/`+0x20`配列)のみで、GBWK/pCurrentStock等の永続stateへの書き込みは一切なし。RNGロール関数(`0x1821690d0`)の呼び出しもない。詳細は下記「Option F」節のF2/F4比較参照。

## STRONGLY SUPPORTED

- vanillaの「Power-Up一回性」の主因はbit6 persistenceそのものである(native自身の条件付きCLEARが通常プレイでほぼ到達しない可能性が高まったため)。
- 既存`src/SkillMutationV3/SkillPowerUpChanceControl.cs`の`SkillPowerUpChanceAlwaysPatch`は、Option FのPostfix実装参考になり得る既存実績である。
- native CLEAR機構(Flag=3/4×State+0x1C)は、MOD側lifecycle-clear実装と対象状況が異なるため、実機タイミング競合リスクは当初想定より小さいと考えられる。

(旧Option D前提だった以下3項目はactiveなSTRONGLY SUPPORTEDから削除した。歴史的経緯は下記「REJECTED OPTION D(履歴)」参照: 「MOD側でbit6-clear機構を独自実装する必要性」「bit6-clear実装にはMutation結果→ordinary変換ロジック併用が必須」「`RepeatableSkillPowerUp.cs`のlifecycle境界検出ロジックを転用すべき」)

## HYPOTHESIS

- `State+0x1C`の意味論: Skill Power-Up固有コード(Core/rstCreateBeforeSkillList/rstMotionReq)のいずれにもwriterが見当たらないことから、「Power-Up処理完了」でも「retry許可」でもなく、presentation lifecycle全体のもっと粒度の大きい完了マーカーである可能性が高い。「forget flow完了」「level-up cycle完了」説は根拠なし。
- native CLEARが通常プレイでほぼ発火しない可能性(writer不在から推論、実機telemetryなし)。

## REJECTED

- bit6を単純にNOP等でCLEAR固定する方式単独での採用(Mutationへの副作用を防げないため)。
- VEH/hardware breakpoint常駐、code caveを正式実装として採用すること。
- **Option D**(lifecycle境界でbit6をclearし、`Repeat==Unlimited`時にresult 2/3をordinaryへ変換する案)。
  - 理由: bit6をclearすると、次回Coreで`0x18227E40C`のbit6 gateを通過し、16-scan → `cmbGetMutationSkill` → genuine Mutation result 2/3への到達可能性まで再開放される。つまり「Power-Upだけrepeat可能、Mutation eligibilityは変えない」という仕様に違反する。
  - Evidence(CONFIRMED static): bit6 SET + dil=0 → `0x18227E40C`でreturn0 → 16-scan未到達。bit6 CLEAR + dil=0 → 16-scanへ到達 → 条件次第でcmbGetMutationSkill。
  - 旧`## 現時点のRepeat=Unlimited設計方針(暫定、まだ実装しない)`節(2026-09-03時点)に記載されていた「lifecycle境界でbit6 clear」「Repeat==Unlimited時にresult 2/3をordinaryへ変換」という方針は、このOption D相当であり、**現在の採用案としては残さない**。旧方針の全文は下記「REJECTED OPTION D(履歴)」に保持する。

## UNRESOLVED(次回最優先)

1. ~~**`State_182e31630+0x1C = 1`のwriter**~~ → **静的に発見済み(2026-09-04、`PRESENTATION_CONSUMER/PLAN.md`・`01_CURRENT_STATE.md`参照)**: `rstcalc.rstCalc`内`0x18227F012`(`mov dword ptr [rcx+0x1c], edx`)がwriter。従来の探索は「リテラル`1`の書き込み」パターンを探していたが、実際の書き込みは**PUpSkillResultをそのままコピーしたレジスタ値**(0/1/2/3のいずれか)であり、`rstcalc`ファイル自体は既に確認範囲に含まれていたにも関わらず検索パターンの不一致で見逃していた。CONFIRMED(static disassembly)だが、runtime上でState+0x1Cの実値変化そのものを直接観測してはいないため、runtime cross-validationは引き続きUNRESOLVED。
2. `State+0x1C`の正式semantic。static disassemblyでは「その回のPUpSkillResultのコピー」という書き込みパターンまでは判明した(上記1参照)が、これが後続処理(bit6 CLEAR gateの`State+0x1C==1`条件等)にとって何を意味するか(「結果値そのものを見ている」のか「非0/0の二値としてのみ使われる」のか等)はruntime cross-validationなしには確定させない。
3. save/load時のbit6挙動、fusion/new unit初期化時のbit6挙動、`RepeatableSkillPowerUp.cs`のlifecycle境界検出ロジックの転用可能性: **NON-BLOCKING / still useful**。Option Fはbit6をclearせずnative bit6 lifecycleを維持する設計のため、これらはいずれもOption F実装のblocking prerequisiteではない。ただし完全にはREJECTせず、bit6全体理解に資する調査として保持する。
4. native CLEARの通常プレイでの実際の発火頻度(実機telemetryが必要、今回は静的解析のみ)。

## Option Catalog

### Option D: lifecycle境界でbit6 clear — REJECTED

`RepeatableSkillPowerUp.cs`のlifecycle境界検出を流用し、`stockChanged`/`TargetIndex==16`等のタイミングでbit6をclearする案。Repeat==Unlimited時に限り、Core Prefix/Postfixでresult∈{2,3}をordinaryへ強制変換することで、Mutationへの直接的な結果漏出は防ぐ設計だった。

**REJECTED理由**: bit6をclearする行為自体が、次回Core呼び出しで16-scan / `cmbGetMutationSkill`への到達可能性を構造的に再開放する。結果を後から強制変換しても、native側のMutation eligibility計算経路(16-scan到達)自体がRepeat設定によって変化してしまうため、「Power-Upだけrepeat可能、Mutation eligibilityは変えない」という仕様に違反する。旧方針全文は下記「REJECTED OPTION D(履歴)」参照。

### Option F: bit6維持 + read-only capture + 条件付き変換 — Current candidate(未実装)

仮設計(2026-09-05、F2採用方針で更新):

- bit6自体はSETのまま維持する(clearしない)。
- Core Prefix: `bit6WasSet`をread-only capture(bit6への書き込みは行わない)。
- `rstCreateBeforeSkillList` Postfix(新規、F2): `GBWK.PUpSkillID`(既存の安全なmanaged property)と、Harmonyが渡す`Il2Cppresult2_H.rstSkillInfo_t info`引数の`SkillCnt`/`SkillID`(offsetは`+0x10`/`+0x20`、byte-exact確定済み)を読み、一致すれば`exclusionMatchedThisCore = true`を記録する(read-only、native writeなし)。
- Core Postfix: 次の全条件を満たす場合のみ、ordinary success tail相当(`pCurrentStock.flag |= 0x40; result = 1`のみ)へ変換する。
  - `Repeat == Unlimited`
  - `bit6WasSet == true`
  - `rawResult == 0`
  - `GBWK.PUpSkillID != 0`(R0-Aを除外)
  - `exclusionMatchedThisCore == false`(R0-Bを除外)
  - 上記CFG上、残るのはR0-Cのみ(下記「Blocking Issue」節のCFG証明参照)。

目的: Power-Upだけrepeat可能にし、Mutation eligibilityはRepeat=Native時と同じに保つ。bit6をclearしないため、16-scan / `cmbGetMutationSkill`をRepeat設定によって新規に再開放しない。

ステータス: **F2 observer diagnostic実装・実機投入済み(`src/SkillMutationV3/OptionFF2Diagnostics.cs`)。result conversion(`__result`/bit6書き換え)は未実装・未承認。**

#### F2 observer diagnostic 実機validation

**Round 1(2026-09-05、build `a8c883b1...`)**: `OptionFF2CoreDiagnostics`(`rstCalcSkillPowerUpCore`のPrefix/Postfix)と`OptionFF2ExclusionListObserver`(`rstCreateBeforeSkillList`のPostfix、`Il2Cppresult2_H.rstSkillInfo_t`引数を位置引数`__3`で受け取る)を実装し、read-onlyのまま(`__result`/bit6/native state書き込みなし)実機投入した。8 Core invocation全てで`exclusionObserved=True`、R0-C-CANDIDATEを2件(invocation=1/pUpSkillID=291、invocation=8/pUpSkillID=112)観測。`exclusionMatched=True`は0件。

**Round 2(2026-09-05、build `4e40b4bb...`、CoreActive修正**前**)**: `exclusionMatched=True`時の詳細ログ(`V3-OPTIONF-F2-EXCLUSION`)と3段階lifecycleログ(`V3-OPTIONF-F2-LIFECYCLE`: CorePrefixReset/ExclusionObserved/CorePostfixConsume)を追加した版で、別セッション(invocation番号がRound 1と非連続)を実機投入した。この**build 4e40b4bb...でのruntime telemetry**から得られた結果:

- **CONFIRMED runtime observed path**: Core Prefix/Postfixのlifecycleは10/10対応した。
- **CONFIRMED runtime observed path**: R0-C-CANDIDATE(`rawResult=0 && bit6WasSet=True && PUpSkillID!=0 && exclusionObserved=True && exclusionMatched=False`)をinvocation 2, 6, 9, 10の4件観測した。
- **CONFIRMED runtime observed path**: `exclusionMatched=True`(R0-B-like runtime case)は0件。
- **CONFIRMED runtime observed path**: `rstCreateBeforeSkillList`はCoreの呼び出し(`0x18227E261`)以外からも多数発火している(lifecycleログで、Core invocation開始前に`stage=ExclusionObserved; invocation=0`が多数、Core invocation=1のPrefix→Observed→Postfix完了**後**も`stage=ExclusionObserved; invocation=1`が継続して観測された)。
- **DESIGN ISSUE FOUND**: `OptionFF2ExclusionListObserver`のPostfixは`OptionFF2CoreDiagnostics.CurrentInvocationId`という「今何番目のCore呼び出しか」というグローバルなカウンタ値だけでタグ付けしていたため、Core開始前・Core終了後のCore外呼び出しが同じinvocation番号としてログに紛れ込んでいた。**CurrentInvocationId-only correlation = REJECTED / scopeとして不十分**と判定する。このbuild(4e40b4bb...)のexclusionObserved/exclusionMatched/R0-C-CANDIDATE分類自体は、「そのinvocation番号のCore呼び出し中に限定して観測された」という保証を欠いたまま得られた値であり、上記4件は前提修正後に再確認が必要な暫定値として扱う。

この発見を受けて、次のCoreActiveスコープ修正を実装した。

**Round 3修正(2026-09-05、コード変更、build `b54829cb...`)**: `OptionFF2CoreDiagnostics`に`[ThreadStatic] bool CoreActive`を追加。Core Prefixで`true`にし、Core Postfixは`try/finally`で必ず`false`に戻す(例外時も残留しない)。`OptionFF2ExclusionListObserver`のPostfixは冒頭で`if (!OptionFF2CoreDiagnostics.CoreActive) return;`とし、**Core呼び出し中でない`rstCreateBeforeSkillList`呼び出しは一切ログ出力しない**ように変更した。また、`exclusionMatched=True`の場合だけでなく、Core Postfixで`rawResult==0`の全ケースについて、そのCore呼び出しで実際に観測した除外リスト全体(`exclusionSkillIDs`)を`V3-OPTIONF-F2-R0`として出力するようにした。

**Round 3実機再検証(2026-09-05、build `b54829cb...`)**: source/deployed SHA256一致(`b54829cb395328714772e520f084740d044122830411c33f7bab504c67caf740`)を確認した上で実機投入。今回観測したセッションでは`CorePrefixReset`/`ExclusionObserved`/`CorePostfixConsume`/`V3-OPTIONF-F2`がいずれも16件ずつで、`CorePrefixReset → ExclusionObserved → CorePostfixConsume`のcorrelationが16/16成立した。Round 2で大量に観測されていたCore外の`ExclusionObserved`は今回のセッションでは観測されず、`ExclusionObserved`のtotal自体がCore invocation数(16)と一致した。

- **CONFIRMED runtime observed path**: `CoreActive`ガードにより、今回のセッションではF2 observerの`ExclusionObserved`がCore invocation中だけに限定された。
- **CONFIRMED runtime observed path**: Core内cross-method correlationが16/16成立した。
- **CONFIRMED runtime observed path**: `V3-OPTIONF-F2-R0`を6件観測した。6件すべてが`rawResult=0 && bit6WasSet=True && PUpSkillID!=0 && exclusionObserved=True && exclusionMatched=False`(`classification=R0-C-CANDIDATE`)であった(例: invocation=1でpUpSkillID=291に対しexclusionSkillIDs=[305,357]、invocation=2でpUpSkillID=39に対し[44,396,353,72]、invocation=10でpUpSkillID=299に対し[349])。
- **CONFIRMED runtime observed path**: R0-C-CANDIDATEについて、candidate(`pUpSkillID`)と実際の`exclusionSkillIDs`が非一致であることを、boolean(`exclusionMatched=False`)だけでなくlist snapshotから直接確認した。
- **NOT OBSERVED**: `exclusionMatched=True` / `V3-OPTIONF-F2-EXCLUSION` / `classification=R0-B`は今回も0件。

**CoreActive scope runtime validation**: PASS(このセッションで観測した範囲、16/16)へ更新する。

**NOT generalized**:
- 今回PASSしたのは「今回観測したこのセッションの16 Core invocation」の範囲であり、「あらゆるnative pathで常にCoreActiveガードが正しく機能する」という一般則(all native paths always)へは昇格させない。
- Round 1/Round 2/Round 3を通じて`exclusionMatched=True`が観測されなかったことは、「F2がpositiveケースを検出できない」ことを意味しない。

**残blocker(2026-09-05時点)**: R0-B positive runtime observation(`exclusionMatched=True`が最低1件観測されること)。

**この残blockerは2026-09-12に解消した**(下記「R0-B runtime positive確認(2026-09-12)」参照、`exclusionMatched=True`/`classification=R0-B`を1件観測)。

**READY FOR OPTION F RESULT CONVERSION(2026-09-05時点)**: NO(CoreActiveスコープの実機PASSは得られたが、R0-B-like caseが依然未観測のため)。**2026-09-12時点の再判定は本文書冒頭「更新(2026-09-12)」および末尾の完了報告を参照。**

#### R0-Bを意図的に再現するための調査(2026-09-05)

**exclusion-list(`rstCreateBeforeSkillList`)のsemantic(CONFIRMED static。命名訂正あり、下記参照)**: 対象unitの`id`(`datUnitWork_s.id`、+0x14)が0かどうかで2種類の**unit id条件で選択されるnative skill table**(loop A: 24件、loop B: 8件)のいずれかを選び、各entryについてlevel条件(`ebp<=ecx`)を満たすもののみ`cmbChkSkillOwner(stock, entrySkillID, 0)`へ渡す。この呼び出しが`-1`(未所持)を返したentryだけが出力(`rstSkillInfo_t.SkillID`)へ追加される。すなわち**exclusionSkillIDs = 「選択されたnative table上、level条件を満たしているがまだ所持していないskill」の集合**。

**命名訂正(CONFIRMED static、この session)**: 以前このテーブルを便宜的に「種族/type別テーブル」と呼んでいたが、直接の証拠はない。loop Aが参照する関数`0x181717dd0`は、rstCreateBeforeSkillListから常に**固定引数`(0, 0)`で**呼ばれており(unit/種族由来の値を渡していない)、この関数は入力`0`に対して「(別のglobal配列)[0]」を返すだけの汎用lookupである(byte-exact確認)。つまりloop Aが参照するtable自体は**unitや種族に依存しない、常に同一のglobal table**であり、「種族別」ではない。差はunit側の所持状況・levelにのみ由来する。以後は**「unit id条件で選択されるnative skill table」**とだけ表記し、種族/typeという未証明のsemanticは付けない。

**`cmbChkSkillOwner`自体の実装(CONFIRMED static、VA `0x182410660`、全体disassembly)**: `stock.skill[0..stock.skillcnt)`(`datUnitWork_s.skill`/`skillcnt`、+0x50/+0x48)を線形scanし、引数skillIDと完全一致する要素があれば`0`(所持)、なければ`-1`(未所持)を返す。副作用なし。

**重要な訂正(CONFIRMED static)**: `GBWK.PUpSkillID`(R0-B/R0-C判定対象のcandidate)は、対象unitが所持しているskillそのものではない。`rstRndGetPowerUpSkill`(実体VA `0x1965489A0`)は`stock.skill[]`を線形scanし、各所持skillIDを`cmbGetPowerUpSkill(skillID)`(thunk VA `0x18227bb90`、実体VA `0x196539430`)へ渡し、**その戻り値(=そのskillの強化先skill ID、0なら強化不可としてcandidate対象外)を候補プールへ格納する**。RNGロールで選ばれたcandidateの最終的な戻り値(`PUpSkillID`)はこの`cmbGetPowerUpSkill`の**戻り値**(強化先ID)であり、元の所持skillID自体ではない。この訂正により、「`PUpSkillID`は`stock.skill[]`の要素なのでexclusion scanと構造的に排他」という前回までの懸念は誤りと判明した。「強化先skill」と「level条件を満たした未所持skill」は独立集合であり一致し得る。

**`cmbGetPowerUpSkill`のdata table抽出(CONFIRMED static、実データ抽出済み)**: `tblSkillPowerUp.fclSkillPowerUpTbl`(`UInt16[][]`、静的field)の実体は、`.cctor`(VA `0x1826A9020`)内で55件(`0x37`)の`(source, target)`ペアを**リテラル即値**として`new ushort[]{source,target}`相当のコードで直接構築している(byte-exact、symbolic disassembly抽出済み)。全55件:

```
source  target      source  target      source  target
1       4           28      30          117     118
2       5           29      31          118     119
3       6           32      34          119     121
7       10          33      35          121     120
8       11          36      39          123     124
9       12          37      40          124     125
13      16          38      41          290     291
14      17          43      44          291     292
15      18          49      50          293     294
19      22          55      56          294     295
20      23          25      26          300     299
21      24          26      27          301     299
                     111     113          305     306
                     113     112          306     307
                     112     114          314     324
                                          315     325
                                          316     326
                                          317     327
                                          318     328
                                          319     329
                                          320     330
                                          321     331
                                          322     332
                                          324     334
                                          325     335
                                          326     336
                                          327     337
                                          323     333
```

`cmbGetPowerUpSkill`自体(VA `0x196539430`)は、この配列を先頭から線形searchし、`entry[0]==入力skillID`となる最初のentryの`entry[1]`を返す(見つからなければ`0`)。

**unit id条件で選択されるnative skill table(loop A/Bの中身)の抽出状況(2026-09-05更新、PARTIAL)**:

- loop Aの参照先`0x181717dd0(0,0)`の正体を特定した(CONFIRMED static): これは`tblSkill.Get(Int32 id)`(managed method、VA `0x181717DD0`と完全一致)であり、`id=0`で呼んでいるため、返す値は**`tblSkill.fclSkillTbl[0].Event`**(`result2_H.fclSkillParam_t[]`型)である。各entryのfield layoutは`fclSkillParam_t{ byte TargetLevel(+0x10); byte Type(+0x11); ushort Param(+0x12) }`で、disassemblyで確認済みの`ebp=TargetLevel`・`byte[+0x11]`比較(1 or 6、`pCurrentStock.hensinmae`(+0x88)==0かどうかで分岐)・`Param=skillID`という読み取りパターンと完全一致する。すなわち**exclusion元テーブルはskill ID 0(未使用/sentinel skillのplaceholder)の`Event`配列を汎用の「default skill学習schedule」として転用したものである(CONFIRMED static)**。
- `tblSkill..cctor`(VA `0x181717F90`)は`tblSkillPowerUp..cctor`のような単純即値store(`mov word[x],imm`)ではなく、フィールドごとに専用のraw書き込み関数(`0x180002AD0=TargetLevel`, `0x180002C30=Type`, `0x1800021C0=Param`。命名注意: 元のC#宣言はpublic **field**であり、C#プロパティのcompiler生成accessorではなく、IL2CPP側の汎用field-write trampolineと見られる)をcallする形でfclSkillParam_tオブジェクトを構築する巨大な関数(局所stackだけで`0x4b528`バイト≒308KB確保)である。
- **targeted byte-pattern検索(2026-09-05実施)**: cctor全域(約0x60000バイト)を、Param setter(`0x1800021C0`)呼び出し直前の`mov dx/edx, 44`(0x2C)命令で検索した。合計1432件のParam setter呼び出しの中から、`Param=44`となる**真正の該当箇所を4件発見した**(誤検出2件は命令境界のずれによるものと確認し除外):
  | VA(Param setter call) | TargetLevel | Type | 格納先index(直後のArrayStore) |
  |---|---|---|---|
  | `0x1817642A0` | 48(0x30) | 5 | 8 |
  | `0x18176751E` | 27(0x1B) | 1 | 7 |
  | `0x181777468` | 12(0xC) | 1 | 5 |
  | `0x18177756E` | 12(0xC) | 6 | 6 |

  `rstCreateBeforeSkillList`が実際にフィルタするTypeは`1`または`6`のみ(`hensinmae==0`→Type 1、`!=0`→Type 6)なので、Type=5の1件目はこの用途には無関係(別skillのEvent配列の可能性)。残り3件(Type 1×2、Type 6×1)はTypeが一致しており、いずれかが`tblSkillTbl[0].Event`の実entryである可能性が高い。
- **index/route再考**: cctor開始直後に最初に構築される24要素配列(`tblSkillTbl[0].Event`だと想定していたもの)は全24entryが`{TargetLevel=0,Type=0,Param=0}`のplaceholder値だった。当初これを「index 0の実データ」と解釈しR0-Bはloop A起源ではない(NOT FOUND)と結論しかけたが、**candidate-pool diagnosticの実機データにより単純化前提の一部が誤りと判明した**: unit 59の`stock.id`は診断ログの`unit=59`列そのもの(=`datUnitWork_s.id`)であり、`id!=0`のため`rstCreateBeforeSkillList`は**loop A**を使う側である。unit 59は過去に`exclusionSkillIDs`へ`44`を含んでいた実績があるため、**loop Aの実データ(`tblSkillTbl[0].Event`)には`Param=44`のentryが存在するはずである**。したがって、cctor冒頭の全ゼロ24要素配列は`tblSkillTbl[0].Event`ではない(cctorの構築順序が単純な「配列index順」ではないか、この全ゼロ配列は別entry/別用途の共有placeholderである)と判断を修正する。上記4件のうちType一致する3件のいずれかが実際の`tblSkillTbl[0].Event`のentryである可能性が高いが、**どのentryが本当にindex 0に属するかを直接のフィールド代入命令(`fclSkillTbl[0] = ...`相当)まで追跡してはいない(STRONGLY SUPPORTEDまでで、CONFIRMEDへは昇格しない)**。
- loop B(`0x18227a960`)の参照先も、`0x18227a960`自体は「per-unit派生値(`r14b`)を受け取り、別配列から1バイトの閾値相当を返す」小さな関数であることは確認したが、その先で実際にskillリストを読む2つ目の配列(`array[r14]->+0x38->[ebx]`)がどのclassに属するかは今回未特定(UNRESOLVED)。

**三候補の所属判定(backward slice、2026-09-05実施)**: 各Param=44 setter callから直前の配列allocation(`call 0x1800e65b0`)まで逆追跡した(CONFIRMED static、命令単位)。

| candidate | Param setter VA | ArrayStore index | 直近の配列alloc VA | alloc count | 同一配列を共有する候補 |
|---|---|---|---|---|---|
| A | `0x18176751E` | 7 | `0x181766D40` | 24(`0x18`) | (単独) |
| B | `0x181777468` | 5 | `0x181776E96` | 24(`0x18`) | C |
| C | `0x18177756E` | 6 | `0x181776E96` | 24(`0x18`) | B |

B・Cは**同一の24要素配列**(alloc `0x181776E96`)のindex 5・6に属することが判明した(candidate B/Cが同一skillのEvent配列内の隣接entryである)。Aは別の24要素配列(alloc `0x181766D40`)に属する。

**未達成の最終hop(親object割当て、UNRESOLVED)**: 各配列の完成後、`fclSkill_t.Event`(+0x18、参照型のpublic **field**)への代入命令、および親`fclSkill_t`が`tblSkillTbl[N]`へ格納される`N`を特定するため、配列alloc直後から広い範囲(`0x181776E96`から+0x5000バイト)を走査したが、`TargetLevel`/`Type`/`Param`のような単純な`mov [obj+0x18], reg`形式の生storeは見つからなかった。参照型fieldの代入はIL2CPPのGC write-barrier絡みで別のtrampoline呼び出し経由になっている可能性が高く、今回の効率的な検索パターンでは捕捉できなかった。**したがって、A/B/Cのいずれかが`tblSkillTbl[0].Event`(index=0)に対応するかどうかは、依然としてUNRESOLVEDのままである。** `skill44 in loop A table`の位置づけは「配列レベルでのgrouping」までCONFIRMED staticとし、「`tblSkillTbl[0]`所属」の確認はUNRESOLVEDで維持する。

**intersection(実データ抽出+実機ログの突き合わせ、訂正版)**: R0-B判定は`PUpSkillID`(=`cmbGetPowerUpSkill`の**target**側)と`exclusionSkillIDs`の一致でのみ成立するため、55件表との照合で**relevantなのはtarget列との一致のみ**である。これまでの実機セッションで観測された`exclusionSkillIDs`(`{305,357}` `{44,396,353,72}→{396,353,72}` `{349}`)を55件表のtarget列と照合した結果、**`44`のみがtargetとして一致**した(`source=43 → target=44`)。`305`は表内に**sourceとして**存在する(`305→306`)が、targetとしては存在しないため、R0-B intersectionの根拠にはならない。`357,396,353,72,349`はsource/targetいずれにも存在しない。

**2026-09-12追記**: 上記は2026-09-05時点のintersection候補整理である。その後、`unit=136`(実機観測: モウリョウ)の`exclusionSkillIDs=[22]`が`19->22`(source=19, target=22、55件表に実在)と一致するintersectionを新たに観測し、これが実際に`rawResult=0`/`classification=R0-B`のruntime positiveへつながった(詳細は下記「R0-B runtime positive確認(2026-09-12)」参照)。すなわちintersection候補は`44`(source=43)だけでなく`22`(source=19)も実在し、後者は実際にR0-B成立まで到達した初のケースとなった。

- unit 59が、Round 3セッションの`invocation=2`(11:51:21、`V3-CFG-RNDPOWERUP`で確認)時点で`exclusionSkillIDs=[44,396,353,72]`を持っていたことをruntimeで確認済み(CONFIRMED runtime observed path)。
- 同セッション`invocation=6`(約45秒後)では`44`が消え`[396,353,72]`になっている(CONFIRMED runtime observed path、44がexclusion listから消えたという事実そのもの)。
- **HYPOTHESIS / likely interpretation**: この間にunit 59がskill 44を自然習得した可能性が高いと考えられるが、`stock.skill[]`の内容そのものをruntimeで直接観測してはいないため、この解釈自体はCONFIRMEDへ昇格させない。
- したがって、**「unit 59が`invocation=2`の時点でskill 43を所持していれば、その回にR0-Bが成立し得た」**という具体的候補が得られた。ただし unit 59がその時点で実際に`skill 43`を所持していたかどうかは、既存ログには直接現れておらず**未確認(UNRESOLVED)**。また、現在(このセッション終了後)ではskill 44は既に自然習得済みの可能性が高く、**同じunitで今から再現できるとは限らない**。

**R0-B発生のexact instruction path(CONFIRMED static、再確認)**:
- compare VA: `0x18227E28E`(`cmp si, word ptr [rdx+rax*2+0x20]`、`si`=`PUpSkillID`)
- branch VA: `0x18227E293`(`je 0x18227e5a9`)
- return0着地VA: `0x18227E5A9`(`xor al,al`)
- `PUpSkillID`は`GBWK+0x4E`から読み取り済み(`0x18227E17D`)。`PUpSkillIndex`(`GBWK+0x4C`)はR0-B判定そのものには使われない。
- bit6 test(`0x18227E40C`)より**前**、RNG rollより前。

**candidate selectionのdeterminism(UNRESOLVED)**: unit 59は少なくとも所持skill 36(→target 39)を持つことがRNG rollの結果として確認されている。もしskill 43も同時に所持していれば、`rstRndGetPowerUpSkill`の候補プールは複数(36, 43, ...)になり、RNGロールでの選択は**deterministicではない**(候補が43単独である場合のみ実質deterministicになり得るが、それを裏付けるデータはない)。

**追加diagnostic実装済み(2026-09-05、`src/SkillMutationV3/OptionFCandidatePoolDiagnostics.cs`)**: `cmbGetPowerUpSkill`単体のPostfixは呼び出しにunit contextが伴わないため採用しなかった。代わりに`rstRndGetPowerUpSkill`(unitを引数に持つ)のPrefixで`stock`(`Il2Cppnewdata_H.datUnitWork_t`、Harmony位置引数`__0`)を捕捉し、`stock.skill[0..skillcnt)`を読み取り専用で列挙、各owned skillIDへ`cmbGetPowerUpSkill`を呼び出して`(skillID, target)`の全candidate poolをログする(`V3-OPTIONF-CANDIDATE-POOL`)。`rstRndGetPowerUpSkill`はCore内の唯一確認済み呼び出し(`0x18227E15A`)から同期的にnestedされるため、`OptionFF2CoreDiagnostics.CoreActive`/`CurrentInvocationId`をread-only参照してinvocation idを付与する(frame近接ではなく直接参照によるcorrelation)。CoreActiveがfalseの場合もログは抑制せず`coreActive=False; invocation=N/A`と明示する。unit固有分岐なし(全unit共通)。native/game state書き込みなし。build/deploy/SHA256一致確認済み(`764c8c9c2699b16c3fee320b964c3f2951f1e7fc9583dae7f42fc9793c78f923`)。

**Candidate-pool diagnostic 実機validation(2026-09-05、build `764c8c9c...`、CONFIRMED runtime observed path)**: `CoreActive=true`の同期Core内callで4件正常動作した。

| unit | ownedSkills | powerUpPairs | candidateCount | exclusion skillCnt |
|---|---|---|---|---|
| 59 | [36,16,387,54,46,184,47,44] | [36->39] | 1 | (別途R0観測、下記) |
| 103 | [111,59,409,7,43,290,60] | [111->113,7->10,43->44,290->291] | 4 | 0 |
| 0 | (未記録) | [290->291] | - | [305,357] |
| 60 | (未記録) | [7->10,301->299,13->16,1->4] | - | 0 |

- unit 59: `skill44`所持済み、`skill43`未所持 → 現在のsaveでは`43->44`ルートは不可(CONFIRMED runtime observed)。
- unit 103: `43->44`のpower-up pairが実在(所持`skill43`から到達可能)。ただし**今回のCore呼び出しでは`exclusion skillCnt=0`**(=`rstCreateBeforeSkillList`が今回除外候補を1件も出さなかった)ため、現状では`44`がexclusion listに入っておらずR0-Bには到達しない(CONFIRMED runtime observed)。
- unit 0 / unit 60: 観測されたpowerUpPairsとexclusionSkillIDsの間にintersectionなし。

**2026-09-12時点の位置づけ**: 上記時点ではunit103の`43->44`が唯一の既知intersection候補だったため、本文書は当時unit103を中心に記述していた。その後unit103はlevel 11/12/13へレベルアップしても`exclusion skillCnt=0`が継続し(`skillIDsNamed=[]`で直接確認、`44`は一度もexclusion listに現れなかった)、代わりに**unit136(モウリョウ)の`19:"ザン"->22:"マハザン"`が実際にexclusion一致・R0-B成立まで到達した**(下記「R0-B runtime positive確認(2026-09-12)」参照)。したがって現在の中心Evidenceはunit103ではなくunit136/モウリョウである。unit103に関する記述自体は「当時の観測事実」として削除せず残す。

**loop A/B route condition再確認(CONFIRMED static)**: `rstCreateBeforeSkillList`冒頭、`ebx = movzx word ptr [r15+0x14]`(r15=pCurrentStock、+0x14=`datUnitWork_s.id`)→`test bx,bx`→`je 0x182280641`。すなわち:
- **`id == 0` → loop B**(8-entry、`0x18227a960`経由)。
- **`id != 0` → loop A**(24-entry、`tblSkill.Get(0).Event`経由)。

candidate-pool diagnosticがログする`unit=`列は`stock.id`そのものである(`OptionFCandidatePoolDiagnostics`の実装参照)。したがって**unit 59(`id=59`)・unit 103(`id=103`)・unit 60(`id=60`)はいずれも`id!=0`のためloop Aを使用し、unit 0(`id=0`)のみloop Bを使用する**(CONFIRMED static+runtimeログのid値から直接導出)。loop Aの参照先(`tblSkill.Get(0).Event`)はunit非依存の共有globalテーブルであるため、**loop Aを使う全unit(59, 103, 60)は理論上同一のsource entry集合を参照する**。

**hensinmae→Type条件(CONFIRMED static、命令単位で再確認)**: `rstCreateBeforeSkillList`のloop A本体、`0x182280500: cmp word ptr [r15+0x88], 0`(+0x88=`datUnitWork_s.hensinmae`)→`0x182280509: je 0x182280548`。
- **`hensinmae == 0` → `je`成立 → `0x182280548`側 → Type比較は`0x18228057F: cmp byte ptr [rdx+0x11], 1`(**必要Type = 1**)。
- **`hensinmae != 0` → `je`不成立 → フォールスルー側 → Type比較は`0x182280542: cmp byte ptr [rdx+0x11], 6`(**必要Type = 6**)。

**unit59 / unit103への当てはめ**:
- unit 59が過去`44`をexclusionへ含んでいた事実(`id=59!=0`→loop A使用確定)は、**loop Aの実データ(`tblSkillTbl[0].Event`)に`Param=44`のentryが存在することの直接的な状況証拠となる**(STRONGLY SUPPORTED)。上記4件のtargeted slice結果のうち、Type一致する3件(Level27/Type1, Level12/Type1, Level12/Type6)のいずれかがこれに対応すると推測されるが、どのVAが本当に`tblSkillTbl[0]`所属かを直接のフィールド代入命令まで追跡してはいないため、CONFIRMEDへは昇格しない。
- unit 103も`id=103!=0`で**同じloop A(同一の共有テーブル)を使う**にもかかわらず、今回`exclusion skillCnt=0`(1件もヒットしない)だった。同一テーブルを共有する以上、entry自体が「unit 103にだけ存在しない」ことはあり得ないが、**理由はUNRESOLVED**とする(unit 103の実際のlevel/hensinmaeを今回observedしていないため)。remaining candidates: (a) `TargetLevel条件`(`TargetLevel<=現在のlevel由来のthreshold`)を現在どのentryも満たしていない、(b) unit 103の`hensinmae`分岐で選ばれるType側に該当entryが少ない/存在しないことによるType不一致。両方とも未確認のまま、いずれか一方に絞り込む根拠はまだない。

**can unit103 become valid target later**: unit 103が今後levelを上げ、`TargetLevel`条件(観測された候補: 12 または 27)を満たす時点に達すれば、loop Aの`Param=44` entryが`exclusion skillCnt`に現れる可能性がある(STRONGLY SUPPORTED)。その時点でもskill 43を所持し続けており、かつskill 44をまだ自然習得していなければ、R0-Bへ到達し得る。ただし正確な`TargetLevel`がどのentryかを一意に特定できていないため、確実な閾値としては断定しない(UNRESOLVED)。

**Evidence**:
- R0-B static path = CONFIRMED static
- `PUpSkillID = cmbGetPowerUpSkill(source)`のtarget(詳細は上記「重要な訂正」参照) = CONFIRMED static
- exclusion list = qualifying unowned native-table skills = CONFIRMED static
- `cmbGetPowerUpSkill`のdata table(55件) = CONFIRMED static(実データ抽出済み)
- loop Aの参照先は`tblSkill.Get(0).Event`(`fclSkillParam_t[]`)であること = CONFIRMED static(命名・構造まで特定)
- loop A/B route condition(`id==0`→loop B、`id!=0`→loop A) = CONFIRMED static
- `skill44 in loop A table` = **CONFIRMED cross-evidence**: unit59(`id=59!=0`)はstatic上loop A使用が確定しており、かつruntimeでそのloop A output(`exclusionSkillIDs`)に`44`を観測済み。ただし、cctor内で見つかったType一致3件のうちどれが`tblSkillTbl[0].Event`所属かは未確定のため、**その特定entryについてはCONFIRMED staticとは書かない**(下記「三候補の所属判定」参照)。
- `skill44 in loop B table` = UNRESOLVED(loop Bのsource classを今回も未特定)
- unit id条件で選択されるnative skill tableの具体的entry**全件**の抽出 = UNRESOLVED(PARTIAL、構造・route conditionはCONFIRMED static、全entryの機械的抽出はcctorがmanaged setter呼び出し形式のため今回未達成)
- candidate-pool diagnosticがCoreActive=trueの同期Core内callで4件正常動作 = CONFIRMED runtime observed
- unit59は現在skill44所持・skill43未所持 = CONFIRMED runtime observed
- unit103はskill43所持・candidate pair 43->44を保有 = CONFIRMED runtime observed
- unit103の今回Coreではexclusion skillCnt=0 = CONFIRMED runtime observed
- unit103 exclusionCnt=0 explanation = **UNRESOLVED**(remaining candidates: TargetLevel条件未達 / Type・hensinmae条件不一致)
- R0-B structural possibility = STRONGLY SUPPORTED(CONFIRMED runtimeとはしない)
- R0-B runtime positive = **CONFIRMED runtime observed(2026-09-12、下記「R0-B runtime positive確認(2026-09-12)」参照)**。旧記載(NOT OBSERVED)はこの時点で撤回する。
- deterministic reproduction = UNRESOLVED(今回のobserved pathは1件のみ)
- CoreActive scope = CONFIRMED runtime observed(16/16、Round 3セッション)
- R0-C candidate identification = CONFIRMED runtime observed cases(6件、Round 3セッション)

#### R0-B runtime positive確認(2026-09-12)

2026-09-05時点の記述(「R0-B runtime positive = NOT OBSERVED」、`exclusionMatched=True`が0件)は、実機で以下の1件を観測したことにより更新する。過去の記載自体は「当時は未観測だった」という事実として履歴に残し、書き換えない。

**Observed reproduction(CONFIRMED runtime observed、1件)**:

- 対象: `unit=136`(今回の実機セッション上の対応: **モウリョウ**。ユーザー実機確認。`datUnitWork_s.id`の一般的semanticはUNRESOLVEDのままであり、これは普遍的対応ではなくこのセッション上の観測にすぎない)
- level: `10`
- hensinmae: `0`
- source skill: `19:"ザン"`
- target skill(Power-Up pair): `19:"ザン"->22:"マハザン"`
- 同一Core invocation内のexclusionSkillIDs: `[22:"マハザン"]`
- `rawResult=0`
- `pUpSkillID=22`
- `exclusionObserved=True`
- `exclusionMatched=True`
- `classification=R0-B`
- ユーザー実機観測: 該当レベルアップ回でスキル変化演出が発生しなかった(native側の判定と実際のゲーム内観測が一致)

ログ出典: `OptionFF2CoreDiagnostics`/`OptionFF2ExclusionListObserver`(`src/SkillMutationV3/OptionFF2Diagnostics.cs`)の`V3-OPTIONF-F2`/`V3-OPTIONF-F2-EXCLUSION`/`V3-OPTIONF-F2-EXCLUSION-ALL`各tag、`OptionFCandidatePoolDiagnostics`(`src/SkillMutationV3/OptionFCandidatePoolDiagnostics.cs`)の`V3-OPTIONF-CANDIDATE-POOL`。skill名表示は`SkillNameResolver`(`src/SkillMutationV3/SkillNameResolver.cs`、2026-09-12新規)経由。

**一般化ガード**: この1件から「すべてのR0-Bケースが必ず同じ条件(hensinmae=0/Type1経路、特定unit等)で起こる」とは一般化しない。あくまで「R0-Bは実在し、かつF2 observerの`exclusionMatched`シグナルは実際に正しくR0-Bを検出できる」という、discriminatorの実効性そのものの実証として扱う。

**このEvidenceが与える影響(再評価)**:

1. **discriminatorの実証**: 2026-09-05時点では、`exclusionMatched`シグナル自体が「一度も`True`になったことがない」ため、「このシグナルは理論上R0-Bを検出できるはずだが、実際に検出できることは未検証」という状態だった。今回、実際にR0-Bが発生した瞬間に`exclusionMatched=True`/`classification=R0-B`が正しく発火し、かつ同じ瞬間にユーザー実機でスキル変化なしを確認できたことで、**シグナルの実効性(false negativeでないこと)を初めて実証した**。
2. **R0-C一意識別の論理的根拠の強化**: Core全体のreturn0経路は3つのみ(R0-A/B/C、byte-exact disassemblyでCONFIRMED static、`0x18227E100`〜`0x18227E507`の完全disassembly範囲内で網羅済み)。したがって、`rawResult==0`の観測において`PUpSkillID!=0`(R0-Aを排除)かつ`exclusionMatched==false`(R0-Bを排除、かつ今回そのシグナル自体の実効性を実証済み)であれば、**残る可能性はCFG上R0-Cのみ**という消去法が成立する。これは推測ではなく、Core全体を完全disassembly済みという前提の下での網羅的排他(exhaustive elimination)である。
3. **`bit6WasSet`条件の位置づけの整理**: 上記の消去法が成立する場合、`bit6WasSet==true`は独立した追加条件ではなく、R0-Cへ到達したことの**論理的帰結**である(R0-C自身の分岐条件がbit6 SETそのものであるため)。したがって`bit6WasSet`は主要discriminatorとしてではなく、消去法の結果と矛盾しないかを確認する**整合性チェック**(もし`bit6WasSet==false`なのにR0-A/B消去法でR0-Cと判定されたら、それは未知の第4経路か観測系の不具合を意味する)として位置づけ直す。

#### Option F result conversion 初回実機成功(2026-09-12)

人間承認を得て、`src/SkillMutationV3/OptionFRepeatUnlimitedControl.cs`(Option F result conversion初回candidate、独立Prefix/Postfix observer、`OPTION-F-DECISION`ログ出力)を実装・build・deployし、`SkillMutation.Chance=Native / SkillPowerUp.Chance=Always / SkillPowerUp.Repeat=Unlimited`の設定で実機投入した。

**Observed reproduction(CONFIRMED runtime observed、3件)**:

- **R0-C conversion(2件)**:
  - `unit=59`(今回セッション上の対応: ハイピクシー)、`pUpSkillID=39:"メディア"`、`classification=R0-C`、`action=CONVERTED_TO_ORDINARY_SUCCESS`。
  - `unit=60`(今回セッション上の対応: ジャックフロスト。ユーザー実機確認済み)、`pUpSkillID=16:"マハジオ"`、`classification=R0-C`、`action=CONVERTED_TO_ORDINARY_SUCCESS`。
  - いずれも、変換が起きた同じCore呼び出しの`V3-OPTIONF-CANDIDATE-POOL`スナップショット(Core実行直前)を確認したところ、変換対象skill(`39`/`16`)は**その時点でまだownedSkillsに含まれていなかった**(CONFIRMED runtime observed、ログ直接確認)。すなわちこの2件はいずれも、変換によって初めて成立した新規スキル習得であり、既に所持済みskillへの無意味な変換(no-op)ではない。
- **R0-B protection(1件)**:
  - `unit=136`(今回セッション上の対応: モウリョウ)、`pUpSkillID=22:"マハザン"`、`classification=R0-B`、`action=NOT_CONVERTED`、`reason=Native exclusion matched.`。これは前節「R0-B runtime positive確認(2026-09-12)」と同一のザン→マハザン経路であり、`Repeat=Unlimited`かつ`SkillPowerUp.Chance=Always`という、Option Fが最も発火しやすい設定下でも、R0-Bが正しく保護され続けることを確認した。

**独立診断とのクロス検証(CONFIRMED runtime observed)**: 同一セッションの既存`OptionFF2Diagnostics`(`__result`を一切書き換えない、独立したread-only observer)側のログでも、同じ3件について一致するnative raw classificationを確認した。

```
V3-OPTIONF-F2; invocation=2; rawResult=0; pUpSkillID=39; exclusionMatched=False; classification=R0-C-CANDIDATE
V3-OPTIONF-F2; invocation=5; rawResult=0; pUpSkillID=16; exclusionMatched=False; classification=R0-C-CANDIDATE
V3-OPTIONF-F2; invocation=7; rawResult=0; pUpSkillID=22; exclusionMatched=True;  classification=R0-B
```

副次的に、この独立診断の`rawResult`フィールドが両R0-Cケースとも変換前の`0`(真のnative値)を正しく記録していたことから、**このbuildでは`OptionFF2CoreDiagnostics`のPostfixが`OptionFRepeatUnlimitedCorePatch`のPostfixより先に実行される**ことをCONFIRMED runtime observed(1セッション分)として確認した。ただしこれは`OptionFF2CoreDiagnostics`との組み合わせに関する観測であり、別途UNRESOLVEDとして残る`MutationDisabledBit6Guard`との実行順序問題(`SkillMutation.Chance=Disabled`時)には言及しない(今回`Mutation.Chance=Native`のままだったため、その組み合わせは未検証のまま)。

**Evidence classification**:

- Option F R0-C result conversion works in observed cases = **CONFIRMED runtime observed**(観測した2件について)
- Option F R0-B protection works in observed case = **CONFIRMED runtime observed**(観測した1件について)
- production stability / universal correctness = **NOT YET CONFIRMED**(観測件数が少なく、設定組み合わせ〈`SkillMutation.Chance=Disabled`との組み合わせ等〉も未網羅)
- deterministic repeated stability = **引き続きruntime coverageが必要**(1セッション・少数観測のみ)

**一般化ガード**: 今回の3件はいずれも1セッション内の観測である。「Option Fが常に正しく動作する」という一般化はまだ行わない。`unit=59=ハイピクシー`/`unit=60=ジャックフロスト`/`unit=136=モウリョウ`は今回のセッション上の対応としてのみ記録し(unit=60はユーザー実機確認済み)、`datUnitWork_s.id`の普遍的semanticはUNRESOLVEDのまま(継続)。

**READY FOR PRODUCTION**: NO(観測件数不足、設定組み合わせ未網羅、`Mutation.Disabled`との共存問題も未解決のため)

#### Native Skill Power-Upの既所持target重複(2026-09-12、CONFIRMED runtime observed)

同一実機セッションで、Option Fとは無関係の**native自身のSkill Power-Up**について重要なEvidenceを得た。

**実機観測**:
- 対象: ジャックフロスト(今回セッション上`unit=60`)
- 既に`マハブフ`を所持
- `ブフ->マハブフ`のSkill Power-Upが(native側で、Option Fの変換を経由せず)成立
- 結果、`マハブフ`が**2枠並ぶ**ことをユーザーが実機目視で確認

**Evidence**: CONFIRMED runtime observed(この観測ケースについて)

**解釈**:
- 少なくともこの観測ケースでは、native Skill Power-Upは「target skillを既に所持している」ことだけでは成功を禁止しない。同一skill IDの重複所持が実際に成立し得る。
- これはOption Fの`__result`変換ロジックとは無関係の、**native自身の既存挙動**である(この重複はOption Fが介在していない通常のPower-Up成立で発生した)。
- **設計上の帰結**: Option F側に独自の「target既所持なら変換禁止」guardを追加しない。native自身が既所持targetへの重複を許容している以上、Option Fが独自にそれを禁止するとnative semanticから乖離してしまう。
- **一般化ガード**: 「常に全skillで重複可能」とは一般化しない。あくまでこの1件の観測ケース(ジャックフロスト、ブフ→マハブフ)に基づくEvidenceである。
- **今後の効用**: 将来「Option Fのせいで重複が起きたのでは」という疑いが生じた場合、この観測(Option F非介在でも重複が発生する)が切り分けの直接的な反証Evidenceとなる。

#### ordinary success tail(`0x18227E4C1`〜`0x18227E4EC`)side effect完全確認(2026-09-05、CONFIRMED static)

byte-exact disassemblyで全命令を確認した。side effectは以下の2つのみ:

1. `0x18227E4E8`: `or dword ptr [pCurrentStock+0x10], 0x40`(bit6 SET)
2. `0x18227E4EC`: `mov al, 1`(戻り値)

それ以外(GBWK write、PUpSkillID/PUpSkillIndex mutation、helper call、他のstate write)は一切存在しない。区間内の他命令はすべてGBWK/pCurrentStockへのnull-check再読み込み(例外throw用の防御コードのみで通常pathでは分岐しない)である。Option Fが「ordinary success tail相当」を再構築する場合、再現すべきside effectはこの2点のみでよいことがCONFIRMED staticとなった。

**追加の合成事実(2026-09-12、既存CONFIRMED staticからの論理的導出、新規disassembly不要)**: Option FがR0-Cケースをordinary成功へ変換する対象は、定義上`bit6WasSet==true`のケースのみである(R0-C自身の分岐条件がbit6 SETであるため)。かつCore自身の同期実行内でbit6をCLEARする命令は存在しない(bit6 CLEARは別関数`rstUpdateSeqSkillPowerUp`内のみに存在し、Coreからは呼ばれない)。したがって、**Option Fの変換対象ケースでは、変換時点で`pCurrentStock.flag`のbit6は既にSET済みであることが保証されており、`flag |= 0x40`の再実行は冪等(no-op)である**。Option Fの実際に必要な変換操作は`__result`(戻り値)を`1`へ変更することのみで足り、bit6への別途書き込みは不要という結論になる(ただし冗長に`flag |= 0x40`を実行しても副作用が増えるわけではない — 既にSET済みのbitを再SETするだけであるため、安全性の観点でも問題はない)。

#### Blocking Issue: return0 disambiguation(2026-09-05更新)

現時点の単純条件(`bit6WasSet && rawResult==0 && PUpSkillID!=0`)だけでは不十分である。

理由: 上記「全return0 path一覧」のR0-B(`rstRndGetPowerUpSkill → PUpSkillID != 0 → rstCreateBeforeSkillList除外 → return0`)が**実在することを確認した**(CONFIRMED static)。R0-Bは`bit6WasSet`の値に関係なく発生し得るため、`bit6WasSet==true && rawResult==0`という条件だけではR0-B(除外由来、変換してはいけない)とR0-C(bit6 gate由来、変換してよい)を区別できない。これを誤認すると、nativeが意図的に除外したcandidateをordinary Power-Up成立へ強制昇格させてしまう。

**`rstCreateBeforeSkillList`全path完全disassembly(2026-09-05、byte-exact、VA `0x182280460`〜`0x18228081D`、CONFIRMED static)**:

関数全体(2つ目のcmbChkSkillOwner呼び出し`0x182280735`を含む後半、およびそれ以降のexception throw padding領域まで)を確認した。

- **RNG呼び出し**: 一切なし。`0x1821690d0`(Core自身が使うRNGロール関数)はこの関数のどこからも呼ばれない。
- **書き込み先**: 観測されたメモリ書き込みは全て、呼び出し元(Core)が引数として渡した出力オブジェクト(`r9`引数、実体は後述`rstSkillInfo_t`)の3フィールド(`+0x10`カウント、`+0x18`配列、`+0x20`配列)のみ。GBWK/pCurrentStock/`datUnitWork_t`/他のglobal・static stateへの書き込みは**一切存在しない**(前半・後半どちらのループも同様)。
- **heap/object allocation**: この関数自身は新規allocationを行わない(出力オブジェクトと2つの配列はCore側で事前allocate済みのものを引数で受け取るだけ)。`0x1822804ED: call 0x181717dd0(0,0)`は既存の読み取り専用テーブル参照を取得するlookupと推定される(この呼び出し先自体の内部は未解析、後述UNRESOLVED)。
- **helper call**: profiler marker初期化(`0x1800e67f0`)、IL2CPPクラス初期化保証(`0x180079560`、いずれも冪等でgame stateに影響しない標準boilerplate)、`0x181717dd0`(前述、推定read-only lookup、未解析)、`cmbChkSkillOwner`(`0x182410660`、呼び出しは2箇所: `0x1822805C7`・`0x182280735`)、`0x18203f220`(2箇所、`ecx`定数`0xE80`/`0xE86`、戻り値を`test al,al`でしか使わない=フラグ/条件チェックと推定、未解析)、exception throw helper群(`0x1800e6970`/`0x1800e6760`/`0x1800e6940`、境界外/null時の防御的throwのみ)。
- **output object以外への副作用**: 確認された範囲では**なし**。ただし`0x181717dd0`と`0x18203f220`自身の内部は未解析であり、これらが独自にglobal stateへ書き込む可能性は理論上否定できない(UNRESOLVED、後述)。**`cmbChkSkillOwner`(VA `0x182410660`)自身の副作用は、上記(「`cmbChkSkillOwner`自体の実装」節)で全体disassembly済みでありCONFIRMED static(副作用なし)である**(2026-09-12訂正: 以前この段落で「未検証・STRONGLY SUPPORTED留め」としていたのは、同一関数について既に得ていたCONFIRMED static判定と矛盾する古い記述だった。未解析のまま残るのは`0x181717dd0`/`0x18203f220`の2つのみ)。

**出力オブジェクトのmanaged型が判明(2026-09-05、System.Reflection.Metadataによるsignature解析、CONFIRMED static)**:

`rstcalc.rstCreateBeforeSkillList`の実際のmanaged method signature(interop assembly)をbyte-level解析した結果:

```
void rstCreateBeforeSkillList(sbyte arg1, Il2Cppnewdata_H.datUnitWork_t stock, short arg3, Il2Cppresult2_H.rstSkillInfo_t info)
```

`Il2Cppresult2_H.rstSkillInfo_t`のfieldは`SkillCnt`・`TargetLevel`・`SkillID`の3つのみで、disassemblyで確認した`+0x10`(カウント)/`+0x18`(配列)/`+0x20`(配列)と型・個数が一致する(`SkillCnt`=カウント、`TargetLevel`=各候補のlevel、`SkillID`=各候補のskill ID、24要素固定長配列)。**ただしこの型には`get_SkillCnt`/`get_SkillID`等のmanaged property accessorは生成されていない**(このセッションでinterop assembly側を直接確認済み)ため、C#の通常property構文では読めない。

**managed read feasibility(F2の実現可能性)**:

HarmonyでこのメソッドをPostfixすると、Harmonyの引数バインディング機構により`Il2Cppresult2_H.rstSkillInfo_t info`パラメータを型そのままの参照として受け取れる(このオブジェクトはCore自身がこの1回の呼び出しのために新規allocateしたものであり、`State_182e31630`のような「staticスロットを解決して辿る」間接参照は一切不要)。取得した`info.Pointer`に対し、disassemblyで確定済みのoffset(`+0x10`=SkillCnt、`+0x20`=SkillID配列の先頭要素)へ`Marshal.ReadByte`/`Marshal.ReadInt16`で読むことは、既存コード(`SeqSkillPowerUpBit6ClearDiagnostics.TryReadState1C`等)と同種の手法であり、かつ既存手法より**間接参照の段数が少なく安全**(staticスロット解決不要、Harmonyが直接渡すインスタンスそのものを読むだけ)。**F2は高い確度でYES**。

**F2 vs F4比較**:

| 観点 | F2(rstCreateBeforeSkillList Postfixで観測) | F4(Core Postfixから再呼び出し) |
|---|---|---|
| safety | 既存呼び出しを観測するだけ、何も再実行しない | `cmbChkSkillOwner`を最大2回・`0x181717dd0`/`0x18203f220`を再実行する必要がある |
| determinism | Coreが実際に使った値をそのまま観測 | 再現性は「再呼び出し時の引数が完全に同一」という前提に依存 |
| implementation complexity | Postfix 1つ+offset読み取りのみ | 出力オブジェクト(`rstSkillInfo_t`)+2配列の新規allocateをmanaged側から再現する必要があり複雑 |
| Harmony/IL2CPP reliability | 標準的なPostfix引数バインディングのみ | managed側からnative関数を「正しい引数構築で」再呼び出しする追加リスク |
| reentrancy risk | なし(観測のみ) | `0x181717dd0`/`0x18203f220`の未検証副作用を2重発火させるリスクが残る(`cmbChkSkillOwner`自体はCONFIRMED static、副作用なし) |
| side-effect risk | なし | `0x181717dd0`/`0x18203f220`の未解析副作用に依存(`cmbChkSkillOwner`はCONFIRMED static、副作用なし) |
| maintenance burden | 低(1 Postfix、offsetは byte-exact確定済み) | 高(呼び出し引数の再構築ロジックが必要) |

**結論: F2を採用候補として優先する。F4は関数全体の副作用が「output object以外への副作用なし」まで確認できた一方、`0x181717dd0`/`0x18203f220`という2つの内部呼び出し先自体の副作用が未解析であり(`cmbChkSkillOwner`はCONFIRMED static、副作用なしと既に判明済み)、F4はそれらを実際に2重発火させるためリスクが残る。F2はそれらを一切再実行しないため、この未解析リスクの影響を受けない。**

**Core内での呼び出し一意性(exclusionMatchedThisCoreの安全性根拠)**: `rstCreateBeforeSkillList`はCore内で`0x18227E261`の1箇所からのみ呼ばれ(xref未確認、UNRESOLVED)、かつCore自身が単一スレッド・同期呼び出しであるため、「Coreの1回の実行中に`rstCreateBeforeSkillList`が呼ばれるとすれば必ずこのCore自身の呼び出しである」という前提は、他の呼び出し元の有無に関わらず**構造的に成立する**(ネストしたsynchronous callである以上、割り込みは発生しない)。CFG上、`rstCreateBeforeSkillList`は`PUpSkillID != 0`のとき常に1回呼ばれ、R0-Cへ到達する経路は必ずこの呼び出しの後を通るため、**R0-Cに到達した時点で「今回のrstCreateBeforeSkillList呼び出しで除外match無し」であることはCFG上保証されている**(除外matchがあればR0-Bで先にreturnし、bit6 testまで到達しない)。

**現在のblocking issue**: F2の実装(`rstSkillInfo_t.SkillCnt`/`.SkillID`読み取りコードの実装・コンパイル確認)自体はまだ行っていない(今回のセッションでは解析のみ、実装禁止の指示を遵守)。次回は実際にF2用のHarmony Postfixコードを書いて`dotnet build`が通るか(型・namespace解決、Harmonyの引数バインディングが実際に成立するか)を確認する必要がある。

## State+0x1Cの扱い(Option Fでも維持)

`State_182e31630+0x1C`はunit-localなrepeat履歴には使わない。runtimeでcross-unit overwriteを観測済みのため(`01_CURRENT_STATE.md`「Repeat=Unlimited設計への注意(重要な設計制約)」参照)、Option FでもState+0x1Cをunit識別やrepeat history keyに使わないこと。

## Evidence修正(2026-09-05)

- bit6のreader/writer inventory、whole-.text scan、disassemblyはすべて**CONFIRMED static**である。**CONFIRMED runtime**は実機ログで直接観測したものだけに限定する(例: 実機テストA〜Eの個別観測、`01_CURRENT_STATE.md`該当箇所)。以前の設計報告にあった「bit6 reader/writer inventory = CONFIRMED runtime」という記載は誤りであり、正しくはCONFIRMED staticである。
- `MutationDisabledBit6Guard`はbit6のwrite precedentではない。同Guardは`__result`の補正のみを行い、bit6自体へのwriteは行わない(`src/SkillMutationV3/MutationDisabledBit6Guard.cs`実装内容、`01_CURRENT_STATE.md`該当記載と一致)。

## REJECTED OPTION D(履歴)

以前の暫定方針(2026-09-03時点、当時は正式名称なし)は次の通りだったが、上記の通り**Option Dとして正式にREJECTED**である。現在の採用案(Option F)としては扱わない。歴史的記録としてのみ保持する。

1. `[SkillPowerUp] Repeat = Native / Unlimited`を新規設定軸として追加。
2. `Repeat==Unlimited`時のみ有効な新規クラス(zero-base、`RepeatableSkillPowerUp.cs`とは別ファイル)で、`rstCalcSeqDevilLevelUp`のlifecycle境界(`stockChanged`/`TargetIndex==16`)でbit6をclear。native側のCLEAR機構には依存しない自己完結設計とする。
3. `rstCalcSkillPowerUpCore`のPrefix/Postfixで、「Prefixでbit6がCLEARだった かつ Repeat==Unlimited」の場合に限り、coreResult∈{2,3}ならordinary Power-Upへ強制変換する新規ロジック(`SkillPowerUpChanceAlwaysPatch`のパターンを流用、ただし発火条件は`Chance==Always`ではなく`Repeat==Unlimited`に紐づける)。
4. `Chance=Always`の既存変換ロジックと二重発火しないよう、共有ヘルパーへ統合するか実行順序を明示的に設計する。
5. 「1 lifecycleにつき1回」という粒度制約(複数レベルアップ時)をユーザーが許容するかは要確認事項。

**Historical note**: 当時(2026-09-03)は`State_182e31630+0x1C`のwriterが未発見であり、この方針自体も「まだ実装しない」暫定案として保留されていた。現在はwriter判明済み(上記CONFIRMED/UNRESOLVED参照)であり、かつ本方針はOption Dとして正式にREJECTED済みである。再評価待ちの暫定案ではなく、確定的に不採用の履歴として扱う。

上記2項目の前提だった「bit6-clear実装にはMutation結果→ordinary変換ロジックの併用が必須」という構造的事実自体は今も正しいが、併用ロジックの有無に関わらずbit6 clearそのものが16-scan / `cmbGetMutationSkill`への到達可能性を再開放するため、この前提を満たしてもOption D全体としてはREJECTEDである(詳細は上記REJECTED参照)。

旧Queue / Inline / Learn-As-New実装は復活禁止(継続、下記「禁止事項」参照)。

## NEXT(2026-09-12更新)

2026-09-05時点の項目1〜4(全return0 path列挙、PUpSkillID!=0のままreturn0するpathの分類、bit6 gate由来return0の一意識別条件探索、ordinary success tailのside effect完全確認)、および`rstCreateBeforeSkillList`全pathのdisassembly・出力object型特定・F2/F4比較・**F2用Harmony Postfixの実装・実機投入・R0-B runtime positiveの実観測**は完了した(旧項目3は完了扱いへ更新)。残る次のステップ:

1. `rstCreateBeforeSkillList`の呼び出し元xrefを全体スキャンし、Core(`0x18227E261`)以外から呼ばれていないかを確認する(exclusionMatchedThisCoreの安全性根拠の追加裏付け。前述のとおり単一スレッド同期呼び出しである限り理論上は不要だが、念のため確認する)。**引き続きUNRESOLVED。**
2. `0x181717dd0`/`0x18203f220`自体の副作用有無を確認する(F2では再実行しないため実装のblockerではないが、Evidence disciplineとして残すUNRESOLVED)。**引き続きUNRESOLVED。**(`cmbChkSkillOwner`〈`0x182410660`〉は2026-09-12訂正: 全体disassembly済みでCONFIRMED static、副作用なしと既に判明しているため、このUNRESOLVED項目から除外した。)
3. ~~F2用のHarmony Postfixコードを実装し、buildが通ることを確認する~~ → **完了(`src/SkillMutationV3/OptionFF2Diagnostics.cs`、実機投入済み、2026-09-12にR0-B runtime positiveを実観測)。**
4. deterministic reproductionの検討(同一条件での再現性確認)。**部分的に前進**: Option F result conversion実装後の初回実機テストでR0-C conversion 2件・R0-B protection 1件を観測(2026-09-12、上記「Option F result conversion 初回実機成功」参照)。ただしまだ1セッション分のみで、**追加runtime coverageが引き続き優先度高でUNRESOLVED**。
5. ~~Option F result conversionの実装可否は...人間承認後のみ実装フェーズへ進む~~ → **完了**: 人間承認を得てOption F result conversion初回candidate(`src/SkillMutationV3/OptionFRepeatUnlimitedControl.cs`)を実装・実機投入し、初回検証PASS(2026-09-12)。
6. **次の推奨(2026-09-12時点)**: 同一設定(`SkillMutation.Chance=Native` / `SkillPowerUp.Chance=Always` / `SkillPowerUp.Repeat=Unlimited`)のまま追加レベルアップを行い、R0-C conversionとR0-B protectionの再現性(observed case数)を積み増す。`SkillMutation.Chance=Disabled`との組み合わせは、設定間相互作用レビュー(2026-09-12)で技術的にUNRESOLVEDと判定済みの既知制限であり、今回は触れず維持する。
7. production sign-off(READY FOR PRODUCTION)には、上記6の追加runtime coverageに加え、`Mutation.Disabled`との共存問題の解決(Harmony実行順序の実機検証、または単一Postfixへの統合設計)が必要。

## 禁止事項(継続)

- 旧Queue/Inline/RepeatableSkillPowerUp.csの単純復活。
- `SeqInfo.Current`/`TargetIndex`/`TargetCnt`/`pCurrentStock`/`WorkStock`への直接write。
- VEH/hardware breakpoint常駐、code caveの正式実装採用。
- 実機Evidenceなしでの確率判定/gateの推測による確定。
- Option Fのユーザー未承認での実装・build・deploy・Git操作。
- source変更・build・deploy・Git操作は、次回明示的な指示があるまで行わない。
