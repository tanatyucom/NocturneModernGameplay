# Skill Power-Up Repeat = Unlimited Investigation

更新日: 2026-09-03(PC終了前の保全作業として記録)

## 参照優先順位

会話再開時は、この文書を読む前に必ず以下を先に読むこと。

1. `00_PROJECT_RULES.md`
2. `01_CURRENT_STATE.md`(Canonical State。本文書はそこに未昇格の分類も含む)
3. 本文書(`investigations/REPEAT_UNLIMITED/PLAN.md`)

`investigations/POWERUP_MUTATION_CFG/`は本investigationの前段(Power-Up/Mutation振り分けCFGそのもの)。本investigationはそこから派生し、「bit6を安全に繰り返し可能にするnative制御点」をzero-baseで調査している。

## 目的

`[SkillPowerUp] Repeat = Native / Unlimited`を実装するにあたり、Mutation.Chanceへ副作用を出さず、旧`RepeatableSkillPowerUp.cs`(現在`Enabled=false`)の単純復活でもない、安全なnative制御点を特定する。**今回のセッションではまだ実装しない。解析のみ。**

## CONFIRMED(01_CURRENT_STATE.mdへ既に昇格済み)

- bit6 SET/TESTはそれぞれ`rstCalcSkillPowerUpCore`内1箇所のみ(VA `0x18227E4E8`/`0x18227E40C`)。
- RNG失敗側(dil=1 bypass)はbit6と無関係にgenuine Mutation attemptへ進む。
- bit6 CLEARは`rstUpdateSeqSkillPowerUp`(VA `0x18228C770`)内に2箇所存在し、`GBWK.Flag∈{3,4}` かつ `State_182e31630+0x1C==1` のときのみ発火する。
  - Site1(Flag==4): VA `0x18228CCB2`、`and dword ptr [rax+0x10], 0xFFFFFFBF`、State+0x1C testは`0x18228CC9F`、State+0x1Cリセットなし。
  - Site2(Flag==3): VA `0x18228CD89`、同一命令、State+0x1C testは`0x18228CD60`、直後`0x18228CDAA`で`State+0x1C=0`へリセット。
- `State_182e31630+0x1C`のreaderは上記2箇所のみ。writer(`=0`)は`0x18228CDAA`のみ。
- `rstMotionReq`(本体VA範囲`0x182280d64`-`0x18228108e`)がpresentation motion request発行の中心関数である(副産物としてPRESENTATION_CONSUMER investigationにも有用)。**訂正**: `0x182281C30`自体は`rstMotionReq`ではなく、`rstMotionReq`が2回呼び出す`rstSmoothMotion`である。詳細は`01_CURRENT_STATE.md`および`investigations/PRESENTATION_CONSUMER/PLAN.md`参照。

## STRONGLY SUPPORTED

- vanillaの「Power-Up一回性」の主因はbit6 persistenceそのものである(native自身の条件付きCLEARが通常プレイでほぼ到達しない可能性が高まったため)。
- MOD側でbit6-clear機構を独自実装する必要性(native側に依存せず自己完結させるべき)。
- bit6-clear実装には、Mutation.Chanceへの副作用を防ぐための「dil=0分岐由来のMutation結果をordinary Power-Upへ強制変換するロジック」の併用が必須(構造的事実: bit6 CLEARはdil=0分岐全体を再開放し、次回ロールでgenuine Mutationにも到達し得るため)。
- 既存`src/SkillMutationV3/SkillPowerUpChanceControl.cs`の`SkillPowerUpChanceAlwaysPatch`が、上記変換ロジックと同型のパターンを既に実装・検証済み(現在は`Chance==Always`限定発火)。
- 既存`src/RepeatableSkillPowerUp.cs`のlifecycle境界検出ロジック(`rstCalcSeqDevilLevelUp`のPrefix/Postfix、`stockChanged`/`TargetIndex==16`、`unit==0`除外、`_captured`ガード)はtelemetry実績のある高品質な資産であり、部分的に転用すべき。
- native CLEAR機構(Flag=3/4×State+0x1C)は、MOD側lifecycle-clear実装と対象状況が異なるため、実機タイミング競合リスクは当初想定より小さいと考えられる。

## HYPOTHESIS

- `State+0x1C`の意味論: Skill Power-Up固有コード(Core/rstCreateBeforeSkillList/rstMotionReq)のいずれにもwriterが見当たらないことから、「Power-Up処理完了」でも「retry許可」でもなく、presentation lifecycle全体のもっと粒度の大きい完了マーカーである可能性が高い。「forget flow完了」「level-up cycle完了」説は根拠なし。
- native CLEARが通常プレイでほぼ発火しない可能性(writer不在から推論、実機telemetryなし)。

## REJECTED

- 「lifecycle境界につき1回」という粒度想定(`RepeatableSkillPowerUp.cs`の前提): native自身のCLEAR粒度は「Core呼び出し1回ごとの結果(Flag=3/4)」であり、lifecycle単位ではないことが判明したため、この前提は不正確と判断する。ただしlifecycle境界clear方式自体をMOD側の独立設計として使うことは引き続き有効。
- bit6を単純にNOP等でCLEAR固定する方式単独での採用(Mutationへの副作用を防げないため)。
- VEH/hardware breakpoint常駐、code caveを正式実装として採用すること。

## UNRESOLVED(次回最優先)

1. ~~**`State_182e31630+0x1C = 1`のwriter**~~ → **静的に発見済み(2026-09-04、`PRESENTATION_CONSUMER/PLAN.md`・`01_CURRENT_STATE.md`参照)**: `rstcalc.rstCalc`内`0x18227F012`(`mov dword ptr [rcx+0x1c], edx`)がwriter。従来の探索は「リテラル`1`の書き込み」パターンを探していたが、実際の書き込みは**PUpSkillResultをそのままコピーしたレジスタ値**(0/1/2/3のいずれか)であり、`rstcalc`ファイル自体は既に確認範囲に含まれていたにも関わらず検索パターンの不一致で見逃していた。CONFIRMED(static disassembly)だが、runtime上でState+0x1Cの実値変化そのものを直接観測してはいないため、runtime cross-validationは引き続きUNRESOLVED。
2. `State+0x1C`の正式semantic。static disassemblyでは「その回のPUpSkillResultのコピー」という書き込みパターンまでは判明した(上記1参照)が、これが後続処理(bit6 CLEAR gateの`State+0x1C==1`条件等)にとって何を意味するか(「結果値そのものを見ている」のか「非0/0の二値としてのみ使われる」のか等)はruntime cross-validationなしには確定させない。
3. save/load時のbit6挙動(未検証)。
4. fusion/new unit初期化時のbit6挙動(未検証)。
5. native CLEARの通常プレイでの実際の発火頻度(実機telemetryが必要、今回は静的解析のみ)。

## 現時点のRepeat=Unlimited設計方針(暫定、まだ実装しない)

1. `[SkillPowerUp] Repeat = Native / Unlimited`を新規設定軸として追加。
2. `Repeat==Unlimited`時のみ有効な新規クラス(zero-base、`RepeatableSkillPowerUp.cs`とは別ファイル)で、`rstCalcSeqDevilLevelUp`のlifecycle境界(`stockChanged`/`TargetIndex==16`)でbit6をclear。native側のCLEAR機構には依存しない自己完結設計とする。
3. `rstCalcSkillPowerUpCore`のPrefix/Postfixで、「Prefixでbit6がCLEARだった かつ Repeat==Unlimited」の場合に限り、coreResult∈{2,3}ならordinary Power-Upへ強制変換する新規ロジック(`SkillPowerUpChanceAlwaysPatch`のパターンを流用、ただし発火条件は`Chance==Always`ではなく`Repeat==Unlimited`に紐づける)。
4. `Chance=Always`の既存変換ロジックと二重発火しないよう、共有ヘルパーへ統合するか実行順序を明示的に設計する。
5. 「1 lifecycleにつき1回」という粒度制約(複数レベルアップ時)をユーザーが許容するかは要確認事項。

**この方針は固定ではない。** State+0x1C writerが判明した場合、再評価が必要。

## 次にやること(再開時の手順)

1. `00_PROJECT_RULES.md` → `01_CURRENT_STATE.md` → 本文書の順に読む。
2. `State_182e31630+0x1C = 1`のwriter探索を`camp*`/presentation/UI系クラスへ拡張(GameAssembly.dllのオフラインbyte-level disassembly、`.analysis/resolve_methods.py`のVA解決手法を再利用)。
3. writer判明後、上記「現時点のRepeat=Unlimited設計方針」を再評価。
4. 設計確定後、ユーザー承認を得てから実装フェーズへ。

## 禁止事項(継続)

- 旧Queue/Inline/RepeatableSkillPowerUp.csの単純復活。
- `SeqInfo.Current`/`TargetIndex`/`TargetCnt`/`pCurrentStock`/`WorkStock`への直接write。
- VEH/hardware breakpoint常駐、code caveの正式実装採用。
- 実機Evidenceなしでの確率判定/gateの推測による確定。
- source変更・build・deploy・Git操作は、次回明示的な指示があるまで行わない。
