# NocturneModernGameplay — Skill Mutation / Learn-As-New 解析ログ

SMT3 Nocturne HD Remaster (smt3hd) 向けMelonLoader MODのうち、
`NocturneModernGameplay`のSkillMutation / Learn-As-New機能について、
これまでの深い調査で確定した事実・仮説・未解決課題をまとめたドキュメントです。
新しく作業を始める前に必ず読んでください。

## プロジェクト概要

- 対象: SMT3 Nocturne HD Remaster (Steam版) の IL2CPP ビルド
- ツール: MelonLoader + HarmonyLib、`GameAssembly.dll` / `global-metadata.dat` を
  Python (`pefile` + `capstone` + カスタムmetadataパーサー) で直接静的解析
- 主要ファイル: `SkillMutationLearnAsNew.cs`, `SkillMutationTelemetry.cs`,
  `SkillMutationAlways.cs`, `GameplayFeatureRegistry.cs`, `GuiMetadataBridge.cs`,
  `ModMain.cs`
- ビルド環境: Windows + dotnet SDK (Codexが実行)。net6ベースのMelonLoader/
  Il2CppInteropアセンブリのため、Linuxサンドボックス上の古いmono/mcsではビルド
  不可(確認済み)。

## 機能の目的

通常のレベルアップで「スキル変化(Mutation)」が発生した際、バニラは元スキルを
上書きしてしまう。これを「元スキルを保持したまま、新スキルを追加習得する」
(Learn-As-New) 挙動に変える。8枠満杯の場合は忘れるスキル選択UIを合成的に
起動し、プレイヤーに選ばせる。

## ネイティブ構造(確定済みオフセット)

すべて `GameAssembly.dll` を静的解析して確定。

### GBWK (rstinit.GBWK, 型は rstData_t)

- `rstinit`クラスの最初の静的フィールド。ネイティブでは
  `[class静的キャッシュ][+0xb8][0]` という間接参照チェーンで取得される。
- **固定の単一グローバルインスタンスではない**。`rstCalc`冒頭で
  「result2処理コンテキストのハンドル」から`+0x50`経由で実体を取得し、
  型チェック(`as rstData_t`相当)を経てGBWKへ再代入している。
  → 忘れるUIサイクル完了(`seq:21→22→8`)のタイミングなどで**GBWKインスタンス
  自体が世代交代する**ことを実測で確認済み。
  → ただし新旧GBWK間で `pCurrentStock` / `WorkStock` / `TargetIndex` /
  `TargetCnt` / `PUpSkillIndex` / `PUpSkillID` / `+0x80` / `+0x88` は正しく
  継承されていた(3件実測、部分継承の証拠なし)。**この枝は正常仕様として
  クローズ済み。**

| オフセット | 内容 |
|---|---|
| `+0x4a` | 全体カウンタ(byte)。`rstSetCurrentDevil`/`rstCalcSeqDevilLevelUp`のループで参照。16以上で候補探索終了 |
| `+0x4c` | `PUpSkillIndex`(処理対象スロット番号) |
| `+0x4e` | `PUpSkillID`(cmbGetMutationSkillの結果) |
| `+0x60` | `WorkStock`(対象unitのdatUnitWork_tへのポインタ) |
| `+0x7E` | 進行度カーソル(byte)。`GBWK+0x80`の6要素配列を順に処理する際の位置 |
| `+0x80` | 進行度配列(byte[6]) |
| `+0x88` | UIカーソル/アクション状態オブジェクトへのポインタ。`+0x20`(cmbChkLevelUpEvent用cursor)と`+0x10`(別の書き込み用サブオブジェクト)を持つ |
| `+0x9c` | `rstUpdateSeqHeartsSkill`が使う一時skillID格納先(ハーツスキルUI用、Mutationとは無関係) |
| `+0xc8` | `rstInitSkillAct`が無条件で書く定数(30)。用途未確認 |
| SeqInfo | `Current/Next/Last/Change/Flag/MesFlag/Timer` を持つ構造体。`GBWK+0x10`がこのオブジェクトへのポインタ |

### datUnitWork_t (stock)

| オフセット | 内容 |
|---|---|
| `+0x10` bit6 (0x40) | 「スキルパワーアップ判定 dil=0 経路を処理済み」フラグ。`datUnitWork_t.Clear()`でゼロ初期化されるため生得的属性ではない。過去の未クリーンアップが残留する可能性がある |
| `+0x14` | word。`rstRndGetPowerUpSkill`等で使われる種族/テーブルID系のキー |
| `+0x24` | byte。レベル/条件キー。`cmbChkLevelUpEvent`が習得表とのマッチングに使う |
| `+0x32`, `+0x34` | ushort。「次の通常習得スキル」候補フィールド。`rstAddSkill`と`rstCalc`内の候補選択ロジックが参照。**`+0x34`の生成元(`0x196542270`)はステートレスで「一度提示したら消化する」機構を持たないことを確認済み**。`+0x32`の書き込み元は未特定 |
| `+0x48` | スキル所持数 |
| `+0x4a` | スロットカーソル。**用途注意**: `rstCalcSeqDevilLevelUp`/`rstSetCurrentDevil`双方の「次デーモン探索ループ」でも同名オフセットが登場するが、こちらは4バイトストライドの別配列(デーモンリスト)を指しており、意味が異なる。stock自身の`+0x4a`はスキルスロット処理カーソルと推定 |
| `+0x50` | スキル配列本体(2バイト/要素、8スロット) |

### 主要ネイティブ関数の役割(確定)

- `rstCalcSkillPowerUpCore`: 乱数ロール(dil=0: 通常パワーアップ/dil=1:
  cmbGetMutationSkill経路)。`0x18227e40c`のbit6テストが`0x18227e4bc`(three-site
  patchが効く箇所)より**手前**にあるため、three-site patchは`bit6=False`の
  unitにのみ効く非対称なパッチだった(確定)。
- `cmbGetMutationSkill`: 24要素の固定候補テーブルを全走査するだけの完全な
  ステートレス関数。カーソルや処理済みフラグは一切持たない。
- `cmbChkLevelUpEvent`: 同じく完全ステートレス。「候補プール」を返すだけで、
  「未処理のものだけ」を返す機能はない。
- `rstOverWriteSkill`: `*slot = skill` の無条件直接書き込み。唯一確認された
  呼び出し元(`rstUpdateSeqSkillPowerUp`)が事前に境界チェックしている。
- `rstSetCurrentDevil` (`public static sbyte`, 引数なし) /
  `rstCalcSeqDevilLevelUp` (`public static int`, 引数なし): 次デーモンへの
  遷移ロジックが**独立して2箇所に重複実装**されている。両方とも
  `0x182281680`(経験値/ステータス反映、戻り値は常に1固定=判定ではなく副作用
  処理)と`0x1822800b0`(6要素ループ判定、GBWK+0x80と対応する可能性)という
  共通ヘルパーを呼ぶが、**stock+0x4aのインクリメントはヘルパー呼び出しより前
  に発生する**ため、ヘルパー単体をゲートしても候補を消費してしまい安全な
  一時停止にならない。
  → `rstCalcSeqDevilLevelUp`は`rstCalc`から1箇所だけ呼ばれ、**戻り値が非ゼロ
  ならcaller(rstCalc)が即座に関数全体を終了する**という正規の早期return経路
  を持つことを確認済み(Design Bの重要な足がかり)。
  → `rstSetCurrentDevil`は静的な`call`命令がバイナリ全体で見つからず、関数
  ポインタ経由の間接呼び出しと推定される。呼び出し文脈は未確認。
- `datSkillName.Get(int, int)`: 表示名を返す汎用ディスパッチャ。forget UI
  (`rstUpdateSeqDestroySkill`/`rstUpdateSeqDestroyConfirm`)以外にも、Mutation
  演出メッセージ(`rstCalc`)、通常Lvアップ(`rstUpdateSeqDefaultSkill`)、
  ハーツスキルUI(`rstUpdateSeqHeartsSkill`/`rstStandbyHeartsSkillPowerUp`)
  など多くの箇所から呼ばれる。forget UI由来だけを識別するには
  `InsideDestroySkillScope`/`InsideDestroyConfirmScope`という同期スコープ
  フラグ方式を導入済み(`caller=`は確定情報、`routeLikely=`はseq値による
  補助推定)。

## MOD側で確定した問題と対応状況

### A. 旧 `_mutationHandled`(単一グローバルbool) — 修正済み

`BeginMutationCandidate()`が候補発見のたびに無条件でfalseへリセットする設計
だったため、別unitのMutation成立を誤ってブロックし得た。「一昨日: ハイピクシー
側でMutation不成立、フロスト側のみ成立」という実機症状の**有力仮説**
(未実測で確証はない)。→ `HandledSlots: HashSet<(IntPtr Stock, int Slot)>`
へ置き換え済み。**この変更は正しかったので戻さない。**

### B. HandledSlots → native overwrite 混在 — 原因確定、対策未実装

`HandledSlots`は「Learn-As-Newとしての変換を二重実行しない」ガードとしてのみ
機能し、ネイティブのMutation成立自体・`rstOverWriteSkill`による直接上書きは
一切止めない。実測で確定:

```
cmbGetMutationSkill成立 → rstCalcSkillPowerUpCore result=2
→ MOD: ObserveMutationSequence → TryConvertReplacementToAddition
→ HandledSlots.Contains==true → return(MOD処理のみ中止)
→ Prefixは無条件でtrueを返す(ネイティブ本体を許可)
→ rstUpdateSeqSkillPowerUp本体 → rstOverWriteSkill → 直接上書き
```

### C. Candidate state (`_candidate`/`_candidateIndex`/`_originalSkill`/
`_mutatedSkill`) — 設計確定、未実装

現在も単一グローバル。`RecordMutationResult`が`_candidateIndex`を更新しない
ため、「stockは別unitのものに上書きされたが、indexは前のunitのまま」という
不整合が構造的に成立し得る。clearも整合性チェックも存在しない。

**承認済み設計**:
```csharp
Dictionary<(IntPtr Stock, int Index), CandidateInfo> Candidates;
```
`RecordMutationResult`はindexを直接持たないため、該当stockの
`AwaitingResult`件数で分岐する:
- 0件 → 異常ログのみ、新規作成しない(indexが不明なまま作ることを避ける)
- 1件 → 一意に結合
- 2件以上 → 安全側に倒し処理しない、警告ログ

`ObserveMutationSequence`は「今ネイティブが処理中のstock」をキーに辞書を
引き、一致するcandidateだけを使う(現在は整合性チェックが皆無)。

### D. Global Pending Queue 滞留 — 原因確定、対策未実装

```csharp
private static void FinishActiveAndContinueQueue(bool continueQueuedLifecycle = true)
{
    _active = null;
    if (!continueQueuedLifecycle) return;   // ← Pending.Count>0でもStartNext()を呼ばない
    if (Pending.Count > 0) { StartNext(); return; }
    ResumeSettledResultBoundary();
}
```
`rstChkAddSkill`経由(`fromCalc=true`)の完了時は`continueQueuedLifecycle:false`
で呼ばれ、「後で`TryStartQueuedAtResultBoundary`が処理する」設計だが、その
条件(`seq.Current==11 && TargetIndex==TargetCnt+1`、「全デーモン完了直前」)
は実測(53回の`rstUpdate`Postfix中わずか3回)でほぼ発火しない。2件目以降の
Pendingアイテムが実機ログの最後まで滞留し続けるケースを確認済み。

**却下した対策**: `rstUpdate`Postfixで毎フレーム緩い条件("`_active==null`
なら即StartNext")を追加する案 → **危険と判明**。滞留期間中`seq`は
`8→21→22→8→21→22→...`と同一unit内の別スロット処理サイクルを繰り返して
おり、この最中に`StartNext()`で`WorkStock`を差し替えると、MOD自身が
cross-unit混線を作り出すリスクが高い。

**却下**: `rstAddSkill`Postfix / `rstUpdateSeqDefaultSkill`完了側 →
実測で「1スキル処理の完了」レベルの粒度でしかなく、「unit全体の完了」を
意味しないことを確認(`rstAddSkill`完了直後も`rstUpdateSeqDestroyConfirm`が
継続し`gbwk+0x7E`が1→3と進行していた)。

### E. Cross-unit UI表示混線 — 大部分は正常仕様としてクローズ

「フロストなのにハイピクシーのスキル一覧が出る」という実機報告を調査。
- `WorkStock.skill[cursor]`の直接参照であることを静的解析で確定
  (`0x182285c40`、余計なキャッシュ層なし)。
- `datSkillName.Get`の呼び出し元を`InsideDestroySkillScope`等で厳密に
  絞り込んだ結果、**forget UI本物の呼び出し(caller=DestroySkill/
  DestroyConfirm)ではcurrentUnit!=workUnitは0件**。以前見えていた大量の
  不一致は、A→B切替時のMutation演出メッセージ表示など、別経路をテレメトリが
  広く拾っていたことが原因だった。
- pointer不一致(`workStockPtr`が変わるがunit IDは変わらない)についても、
  A→Bデーモン切替(TargetIndex変化)とは無関係に発生することを確認。GBWK
  世代交代に伴う副産物である可能性が高く、この枝も正常仕様としてクローズ。

**現在の主戦場はB/C/D(queue/lifecycle handoffとcandidate state)であり、
UI表示そのものの混線ではない。**

## 設計方針の再検討(現在進行中、実装は未着手)

Global Pending Queue方式(Design A)は、Mutationをnativeのunit進行から切り
離して後で再注入する設計そのものが、WorkStock/CurrentStock/GBWK/TargetIndex/
Candidate/Pendingの同期問題の根になっている可能性がある、という仮説が浮上。

代替として **Per-unit Inline Gate (Design B)**: Mutation成立時にそのunitの
native progressionをその場で一時停止し、Learn-As-New処理(忘却含む)を完了
させてから、同じunitのnative lifecycleへ復帰させる方式を検討中。

### Design B の実現可能性(現時点の評価: 黄信号)

- `rstSetCurrentDevil`単独ゲートは**不十分と確定**
  (`rstCalcSeqDevilLevelUp`内に独立したinline版の遷移経路が存在するため)。
- 共通ヘルパー(`0x182281680`/`0x1822800b0`)単独のゲートも**不採用**
  (stock+0x4aのインクリメントがヘルパー呼び出しより前に発生するため、
  ヘルパーを止めても候補は既に消費されている)。
- 残る有望な道: **`rstCalcSeqDevilLevelUp`という関数全体を、Prefixで
  丸ごと1フレームだけ実行させず、`__result`を非ゼロにする**方式。これは
  caller(`rstCalc`)の正規の早期return経路に自然に合流するため、Transpiler
  不要でPrefixだけで安全に実現できる可能性が高い(重要な確定事実)。
  `rstSetCurrentDevil`側も同様の独立メソッドとして丸ごとスキップする方式が
  取れる可能性が高いが、呼び出し元の文脈確認は未完了。
- **未解決の核心的な問い**: この「非ゼロreturn」を数十〜数百フレーム連続で
  返し続けても、timeout/timer/seq自動遷移/デッドロックが起きないか。これは
  静的解析の限界に達しており、実機テレメトリでの検証が必要な段階。

### 次のアクション(このメモの時点で未完了)

1. `rstCalcSeqDevilLevelUp`/`rstSetCurrentDevil`向けの読み取り専用テレメトリ
   (`DEVIL-TRANSITION-TRACE`)は実装済み・出力済み。まだ実機で取得していない。
   → 「Learn-As-Newを発生させたい期間中、この関数が毎フレーム同じ状態
   (TargetIndex/cursor/seq)で再入されるか」を実測で確認する。
2. 上記が確認できたら、次段階として「特定条件下で1フレームだけ
   `rstCalcSeqDevilLevelUp`をスキップし、非ゼロresultを返す」という最小
   ゲート実験をビルドし、TargetIndex/cursor/seqが保持されたまま翌フレーム
   復帰できるかを検証する。
3. それが安全と確認できて初めて、Design Bへの本格移行(Candidate/Pending
   再設計を含む)を実装する。安全性が確認できなければDesign A(Global Queue)
   を維持し、Candidate複合キー化とPending handoff改善だけを実装する。

## 作業の進め方(申し送り)

- **実装は常に「読み取り専用テレメトリ→実機ログ確認→最小実装→再確認」の
  順で進める。** 一足飛びに大きな修正を入れない。
- ネイティブの挙動は「仕様」と「バグ」を安易に混同しない。開発者本人の実機
  観察が最優先の一次情報。ログから見える相関は「確定事項」と「仮説」に
  必ず分けて記録する。
- ビルドはこの解析(Linux/Python静的解析)を行った環境ではなく、
  dotnet SDKのある実機環境で行う。
- 過去に何度も「狭い/推測ベースのseq条件に依存して失敗する」パターンを
  繰り返しているため、新しい安全条件を追加する際は、必ず実測(最低でも
  該当シチュエーションのログ)で裏付けてから採用すること。

## 変更履歴

このドキュメントは調査の進行に応じて随時更新してください。大きな設計変更
(Design A/B の決定など)があった場合は、このファイルの該当セクションを
更新し、可能であれば日付とコミットハッシュを添えてください。

