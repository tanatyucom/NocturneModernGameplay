# Skill Mutation V3 Stale Claims Review

更新日: 2026-09-02

既存記録は削除・改変せず、現行V3のpresentation評価へそのまま流用できない記述だけを候補として整理する。

| File | Section | Original claim | Why it may be stale | Suggested correction |
|---|---|---|---|---|
| `docs/research/skill-mutation/master-archive.md` | 5-Round Real-Machine Comparison / Runtime Case Studies | `Successful Mutation`、`queued-full-route completed`等を成功Caseとして記録 | legacy Queue/Learn-As-New transactionの完了評価であり、現行V3のpresentation successを証明しない。 | 履歴は維持し、「Queue transaction resultでありpresentation成否とは別」と読む。必要なら追記のみ行う。 |
| `docs/research/skill-mutation/SkillMutationV3_DesignSpec.md` | PoC1 Behavior | active sourceを`src/SkillMutationV3.cs`と記載 | 現在のactive pathは`src/SkillMutationV3/SkillMutationV3.cs`。 | 将来Canonical更新時にpathだけ最小修正する。 |
| `README.md` | 現在地 / 主要ソース | `SkillMutationLearnAsNew.cs`、`SkillMutationTelemetry.cs`を主要ソースとして案内 | 現在はlegacy隔離済みのzero-base V3であり、現行active sourceの案内として古い。 | legacy研究履歴と現行active sourceを分けて案内する。 |

## 検索結果

- Repository内に文字列としての`10/10 success`または`presentation success`の具体的集計は見つからなかった。
- `5/5 sampled cases`等はnative ownershipに関する限定Evidenceであり、画面presentation成功率として再解釈しない。
- 過去の画面観測を一括してMutation成功と数え直す前に、`PUpSkillResult=1`のordinary Power-Upを除外する必要がある。

このレビューは修正指示ではなく、Canonical変更候補の一覧である。
