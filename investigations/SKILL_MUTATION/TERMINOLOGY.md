# Skill Mutation V3 Terminology

更新日: 2026-09-02

| 用語 | 定義 | Evidence上の注意 |
|---|---|---|
| Skill Mutation | nativeが元skillから変化候補を生成するイベント。 | 画面表示だけでは判定しない。 |
| ordinary Skill Power-Up | Mutationとは別の通常Skill Power-Up経路。 | `PUpSkillResult=1`の表示がスキル変化に見える場合がある。 |
| Mutation Logic Success | `cmbGetMutationSkill`等のnative logicが非0の変化先を返した状態。 | presentation successを含意しない。 |
| Mutation Detection | V3がMutation成立を`V3-MUTATION-DETECTED`として観測したこと。 | user観測とは別Evidence。 |
| replacement | native replacementがlive skill arrayに現れた状態。 | `V3-REPLACEMENT-VISIBLE`の`VISIBLE`は画面表示を意味しない。 |
| presentation state | presentation lifecycleに関係するsource objectおよび`State_182e31630`の観測状態。 | state成立と画面表示は分離可能。 |
| presentation success | ユーザーが実機画面で対象イベントの表示を確認したこと。 | 対応runtime eventとの紐付けが必要。 |
| presentation failure | Mutation Detectionとreplacementが成立したのに、対応する画面表示が欠落した状態。 | 単なる表示なしやMutation未検出と混同しない。 |
| Mutation not detected | 対象時間窓に`V3-MUTATION-DETECTED`が存在しない状態。 | presentation failureではない。 |
| pCurrentStock ownership | real GBWK `+0x60`が示す現在のstock ownership。 | pointer identityと内容を区別する。 |
| Pending / Queue | legacy Learn-As-Newで用いた遅延処理モデル、またはruntime上で観測される遅延状態。 | legacy実装は現行V3へ戻さない。runtime観測語として使う場合は明示する。 |
| `seq=9` | Skill Power-Up/Mutation lifecycle上のsequence state。 | 意味を追加推測せずCase内の観測値として記録する。 |
| `seq=10` | 同上。 | 呼び出し順序とframeを併記する。 |
| `seq=11` | 同上。 | presentation成否を値だけで断定しない。 |
| `seq=21` | full-slot forget flow入口側として確認されたsequence state。 | legacy queue ownershipと同一視しない。 |
| `seq=22` | forget flow中のsequence state。 | nativeの`21 -> 22 -> 8`として扱う。 |
| `PUpSkillResult` | real GBWK `+0x4B`のmanaged表現。 | raw byte `rawGbwk4b`およびsigned解釈と照合する。`1`はordinary Power-Up系として観測。 |

## 誤った同値関係

```text
「画面にスキル変化表示が出た」 != 「Mutationだった」
```

`PUpSkillResult=1`のordinary Skill Power-Up presentationが存在するため。

```text
「画面に表示がなかった」 != 「Mutation不成立」
```

Mutation Logicとreplacementが成立したままpresentationだけ欠落した`HP-MUT-FAIL-001`が存在するため。
