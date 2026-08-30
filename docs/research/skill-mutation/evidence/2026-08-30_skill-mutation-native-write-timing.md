# Investigation: Native Mutation Write Timing (rstOverWriteSkill ref copy-back)

## Question
`rstOverWriteSkill(ref int slot, ushort skill)` が実際にどのストレージへ書き込んでいるのか。native write は `pCurrentStock` / `WorkStock` のどちらの skill array に、いつ反映されるのか。

## Baseline
- Game: SMT3 Nocturne HD Remaster (Steam, IL2CPP build)
- Mod: NocturneModernGameplay, SkillMutation / Learn-As-New feature
- Repro: Skill Mutation発生 → 8枠満杯 → Learn-As-New synthetic forget flow

## Known Facts

- [CONFIRMED] `rstOverWriteSkill`はGameAssembly.dll側で`*slot = skill`という単純な直接代入(RVA `0x2285ff0`)。
- [CONFIRMED] C#側の`rstupdate.rstOverWriteSkill`は`miIL=True`の通常マネージドメソッド(P/Invoke宣言ではない、RVA=`0x1b6644`、CodeSize=77バイト、Fat header)。標準ILには存在しないバイト列(`0xFE 0x1C`等)を含み、Il2CppInteropが生成する特殊なマーシャリング/ネイティブ呼び出しコードと推定される。
- [CONFIRMED] `datUnitWork_t.skill`は`Il2CppStructArray<int>`(short/ushortではない)。getter/indexerは毎回ネイティブメモリを直接読み書きし、managed側キャッシュは存在しない(Il2CppInterop.Runtime 1.4.5.0のソース確認済み)。
- [CONFIRMED] `stock.skill`を複数回呼んでもmanaged wrapper instanceは毎回新しくなるが、native fieldが同じarrayを保持している限り、各wrapperは同じnative arrayを直接参照する。
- [CONFIRMED] `MUTATION-PROBE sequence-change`(`item.Stock`という固定参照経由)と、`GBWK.pCurrentStock`(その瞬間に都度取得した参照)は、実測で`datUnitWork_t.Pointer`・`skill`配列オブジェクトの`.Pointer`ともに同一(`sameStockPtr=True sameSkillArrayPtr=True`、8/8件で確認)。
- [CONFIRMED] `OVERWRITE-VALUE-CHECK`(`rstOverWriteSkill`のPostfixタイミング)では、8/8件で`currentSkillAtIndex`/`workSkillAtIndex`ともに元の値のまま(`currentMatchesArg=False workMatchesArg=False`)。ネイティブの`ref int __0`引数上は変異後の値(`argSlotValueAfter`)を示しているにもかかわらず、`pCurrentStock`/`WorkStock`どちらの実配列にもまだ反映されていない。
- [CONFIRMED] その後(`rstUpdateSeqSkillPowerUp`のPostfix内、`ObserveMutationSequence`実行時)、`item.Stock`経由の`DescribeSkills`では、mutated値が正しく配列に反映されて見える(`sequence-change`の`after`に正しい値)。

## Hypotheses

- [HYPOTHESIS] `ref int __0`(rstOverWriteSkillのPrefix/Postfixで受け取る引数)は、native関数呼び出しの実引数への直接ポインタではなく、Harmony/Il2CppInteropが用意する一時的なブリッジ変数である可能性が高い。根拠: `refAddr`(`fixed`で取得した物理アドレス)が、異なるunit/index/フレームの7件全てで完全に同一の固定値(`0xBB83F6E630`)だった。x64呼び出し規約で毎回異なる実引数が同一絶対アドレスになることは通常考えにくい。
- [HYPOTHESIS] 提案されたモデル:
  ```
  native caller: actual skill[index] = original
      ↓
  rstOverWriteSkill(ref temp): temp = original → mutated
      ↓
  Harmony/Interop Postfix実行時点: argSlotValueAfter=mutated だが actual skill[index]はまだoriginal
      ↓ (この時点が OVERWRITE-VALUE-CHECK)
  ref temp を native caller側へ copy-back
      ↓
  actual skill[index] = mutated
      ↓
  rstUpdateSeqSkillPowerUp続行、outer Postfix到達
      ↓ (この時点が sequence-change / ObserveMutationSequence)
  ```
  このモデルは、これまでの一見矛盾する4つの観測結果(refAddr固定・Postfixでは未反映・outer Postfixでは反映確認・その直後にMOD自身が書き戻し)を、単一の説明で統合できる。

## Rejected Explanations

- [REJECTED] `stock.skill` getterがwrapper instance内のcached array wrapperを返す説明(静的解析で否定: getter/indexerは常にライブメモリを読む)。
- [REJECTED] `Il2CppStructArray<int>`がmanaged-side copied bufferを読む説明。
- [REJECTED] `this[int]`が以前取得した値をキャッシュして返す説明。
- [REJECTED] `.Pointer`がmanaged wrapper、proxy、trampoline、一時bufferを指すという説明(native IL2CPP object pointerであることを確認済み)。
- [REJECTED] managed wrapper instanceが異なるだけで、同じnative arrayの同じindexから異なる値が返るという説明。
- [REJECTED]「別の`pCurrentStock`実体を見ていた」という説明(`sameStockPtr`/`sameSkillArrayPtr`の実測で完全に否定)。
- [REJECTED] `fixed(ref __0)`で取得したアドレスを、native `rstOverWriteSkill`の実書き込み先アドレスとみなす検証方法自体(`refAddr`固定値により、この検証方法はネイティブの実ストレージアドレス確認には使えないと判断)。
- [REJECTED]「`rstOverWriteSkill`のPostfixが早すぎるだけで、`rstUpdateSeqSkillPowerUp`終了時には必ず反映される」という単純化された説明(反映は確認できたが、その直後にMOD自身の`TryConvertReplacementToAddition`が書き戻すため、後続の別観測点では見えなくなる、というより複雑な実態だった)。

## Runtime Evidence

- 7件のOVERWRITE-ADDRESS-CHECK: `refAddr=0xBB83F6E630`(全件完全一致)、`sameAddress=False`(全件)。
- 7件のCONVERT-ENTRY-CHECK: `sameAsWorkStock=False`、`sameAsCurrentStock=True`(全件一致)。`TryConvertReplacementToAddition`に渡される`stock`は一貫して`pCurrentStock`と同一。
- 8件のOVERWRITE-VALUE-CHECK: `currentMatchesArg=False workMatchesArg=False`(全件)。
- 8件のsequence-change(array-pointer identity check付き): `sameStockPtr=True sameSkillArrayPtr=True`(全件)、`after`にmutated値が正しく反映。

## Static Evidence

- `rstOverWriteSkill`ネイティブ実体: GameAssembly.dll RVA `0x2285ff0`、`*slot = skill`の単純代入。
- `rstOverWriteSkill`のC# ILメソッド: Assembly-CSharp.dll RVA `0x1b6644`、Fat header、CodeSize=77、`miIL=True`。標準IL命令セットにない`0xFE 0x1C`等のバイト列を含む(Il2CppInterop生成コードの特殊マーシャリングと推定、未デコード)。
- `Il2CppInterop.Runtime.dll` v1.4.5.0: `datUnitWork_s.skill`は`Il2CppStructArray<int>`。getter実装(native field offset読み取り→`new Il2CppStructArray<int>(arrayPointer)`)、indexer実装(`array.Pointer + 4*IntPtr.Size + index*sizeof(int)`への直接読み書き)を確認済み。managed側キャッシュ皆無。

## Current Model

`rstOverWriteSkill`のC#メソッド境界(Harmonyがパッチを当てる対象)は、Il2CppInteropが生成した「ネイティブ呼び出しを仲介するマネージドILメソッド」である可能性が高く、そのため`ref int`引数はHarmonyLib/Il2CppInteropの内部ブリッジ機構を経由している。この機構が、native実引数への「copy-back」をいつ行うか(Harmony Postfixの前か後か)が、今回の一連の矛盾する観測結果全ての根本原因であるという説明が、現時点で最も整合的なモデルである。

## Unresolved

- [UNRESOLVED] Il2CppInterop/HarmonyLibの`ref`引数copy-backが、Harmony Postfixの実行に対して前か後か。
- [UNRESOLVED] `rstOverWriteSkill`のC# ILメソッド本体(77バイト)の正確な命令デコード(専用ILデコーダが必要、今回のツールセットでは未達成)。

## Next Minimal Test

**問い(1点に限定)**: `Il2CppInterop/HarmonyLibのref argument copy-backは、Harmony Postfixの実行に対して前か後か。`

これはGameAssembly.dllの解析だけでは決着せず、以下のいずれかが必要:
1. HarmonyLib(0Harmony.dll)自体のソース/デコンパイル確認(native detour patchの実装、特に`ref`/`out`パラメータの扱い)
2. Il2CppInteropが生成するILメソッド(`rstOverWriteSkill`本体、RVA `0x1b6644`)を専用ILデコーダ(dnlib等)で正確にデコードする

## Implementation Consequence

- **今安全に変更してよいこと**: なし。
- **まだ変更すべきでないこと**: `rstOverWriteSkill`の抑止(Stage 2 overwrite suppression)。書き込み先の実体性・タイミングが未確定のまま抑止を実装すると、意図しない副作用を生むリスクが高い。
