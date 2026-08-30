# Investigation: Learn-As-New Synthetic Flow — cancel/completed Determination

## Question
`queued learn cancelled` / `queued-full-route completed` の分岐は、native mutation write の成否を表しているのか、それとも別の何かに基づくのか。

## Baseline
- Game: SMT3 Nocturne HD Remaster (Steam, IL2CPP build)
- Mod: NocturneModernGameplay, SkillMutation: Learn-As-New feature
- Repro: 2体同時レベルアップ×5回ロード比較。1〜4回目はMutationで得た新スキルをそのまま忘れる、5回目のみ新スキルを保持する、という条件を意図的に変えて実施。

## Known Facts

- [CONFIRMED] `HasSkill(stock, skill)`の実装は、`stock.skillcnt`と`stock.skill.Length`の小さい方を上限に、その瞬間のskill配列を線形走査するだけの単純な関数。native write timingとは無関係に、呼ばれた瞬間の配列内容だけを見る。
  ```csharp
  private static bool HasSkill(Il2Cppnewdata_H.datUnitWork_t stock, ushort skill)
  {
      int count = Math.Min(stock.skillcnt, stock.skill.Length);
      for (int i = 0; i < count; i++) if (unchecked((ushort)stock.skill[i]) == skill) return true;
      return false;
  }
  ```
- [CONFIRMED] `_activeCancelled`は複数の完了判定経路(`TryCompletePendingQueuedReturn`相当、`ObserveCancelledLearnExit`、タイムアウト経路等)から、共通して`!HasSkill(item.Stock, item.MutatedSkill)`という条件でセットされる。
- [CONFIRMED] `CompleteActive()`は、`_activeCancelled`が立っていれば`queued learn cancelled`として即終了、そうでなければ再度`HasSkill(item.Stock, item.MutatedSkill)`を確認し、trueなら`queued-full-route completed`へ進む。
- [CONFIRMED] `OverrideQueuedLearnSkill(ref ushort skill, datUnitWork_t stock)`という関数が、`fclCombineCalcCore.cmbAddSkill`のPrefixから呼ばれ、ネイティブが確定しようとしている新規追加スキルの値を、`_active.MutatedSkill`へ強制的に書き換える。これがLearn-As-Newの「元スキルを保持したまま変異後スキルを新規追加する」機能の最終確定ステップである。
  ```csharp
  internal static void OverrideQueuedLearnSkill(ref ushort skill, Il2Cppnewdata_H.datUnitWork_t stock)
  {
      if (_active == null || _completionObserved || stock == null ||
          stock.Pointer == IntPtr.Zero || _active.Stock.Pointer != stock.Pointer) return;
      if (skill == _active.MutatedSkill) return;
      ushort replaced = skill;
      skill = _active.MutatedSkill;
      MelonLogger.Msg("... final-add redirected; from={replaced} to={skill}.");
  }
  ```

## Hypotheses (→ 今回 CONFIRMED に昇格)

- [CONFIRMED] 5回比較の結果と完全に一致:
  - 1〜4回目(忘れるUIで、新しく覚えた変異後スキル自体を忘却対象に選ぶ)→ 最終的に`HasSkill(item.Stock, item.MutatedSkill)=False` → `queued learn cancelled`
  - 5回目(変異後スキルを保持する側を選ぶ)→ `OverrideQueuedLearnSkill`により新規追加スロットが`mutatedSkill`で確定 → `HasSkill=True` → `queued-full-route completed`

## Rejected Explanations

- [REJECTED]「`queued learn cancelled`はnative mutationの書き込み失敗(Investigation A参照)を意味する」— cancel/completedの分岐は、native write timingとは独立した、synthetic forget flow完了後のプレイヤー選択結果(最終的にmutated skillが実際のskill配列に存在するか)にのみ依存する。Investigation Aで確認された「rstOverWriteSkill直後は未反映に見える」という現象と、cancel/completed分岐は別レイヤーの話であり、混同すべきではない。

## Runtime Evidence

5回ロード比較(2026-08-30):
| 回 | unit | original→mutated | 最終選択 | 結果 |
|---|---|---|---|---|
| 1 | 59 | 36→385 | 変異後スキルを忘れる | `queued learn cancelled` |
| 1 | 60 | 301→67 | 変異後スキルを忘れる | `queued learn cancelled` |
| 2 | 59 | 36→28 | 変異後スキルを忘れる | `queued learn cancelled` |
| 2 | 60 | 1→45 | 変異後スキルを忘れる | `queued learn cancelled` |
| 3 | 60 | 7→45 | 変異後スキルを忘れる | `queued learn cancelled` |
| 4 | 60 | 13→387 | 変異後スキルを忘れる | `queued learn cancelled` |
| 5 | 59 | 36→392 | 変異後スキルを忘れる | `queued learn cancelled` |
| 5 | 60 | 7→387 | **変異後スキルを保持** | **`queued-full-route completed`** |

全8件が仮説通りの結果と一致。

## Static Evidence

- `HasSkill`: `SkillMutationLearnAsNew.cs` 内、線形走査のみ。
- `_activeCancelled`/`_completionObserved`の全セット箇所: 複数の完了経路(通常完了、タイムアウト、キャンセル観測)全てで、最終判定は`HasSkill`の結果に帰着する。
- `OverrideQueuedLearnSkill`: `fclCombineCalcCore.cmbAddSkill`のPrefixから呼ばれ、Learn-As-New機能の最終確定ステップを担う。

## Current Model

Learn-As-Newのcancel/completed判定は、native側の書き込みタイミング(Investigation A)とは完全に独立した、MOD自身の設計通りのロジックである。「synthetic forget flow完了時点で、変異後スキルが実際にそのunitのskill配列に存在するか」だけを見ており、これは意図された仕様として正しく機能している。

## Unresolved

なし(このInvestigationの範囲内では全て解決)。

## Next Minimal Test

不要。このInvestigationは完了と見なす。

## Implementation Consequence

- **安全に変更可能なこと**: なし(現状のcancel/completed判定ロジックは仕様通り正しく機能していることが確認できたため、変更の必要性自体がない)。
- **まだ変更すべきでないこと**: cancel/completed判定ロジック自体。触る理由がない。
