# Hidden New Skill Entry Investigation (master-archive.md Section 22 continuation)

## 目的

「捨てるスキルを選んでください」フローで表示される、Learn-As-New(mutation)で習得したスキルの行が、カーソル移動・説明表示・決定操作は機能するのに、選択ハイライト枠だけ表示されない現象の native 側原因を特定する。

**モデル修正(2026-09-14、User指摘)**: 当初は「所持8スキルとは別の、9番目の特殊(synthetic)スロット」というmaster-archive.md由来の前提で調査していたが誤り。実際には**通常の8行(`obtainedText[0..7]`)のうち1行が、bridgeフロー中に一時的にpending/new skill表示へcontent差し替えされている**構造である(観測上はindex7が差し替えられていたが、これは配列上「たまたま最後の行」というだけで、9番目の特殊indexではない)。したがって調査対象は「なぜindex7が特別扱いされるか」ではなく、**「どのタイミングで`obtainedText[index]`を差し替え、対応する`skillCurObj[index]`をON/OFFしているか、その分岐条件は何か」**である。

## 背景

master-archive.md Section 22(2026-08-30)で最初に報告。当時はQueue/Inline architecture(v2.1)。V3 zero-base移行後、`HiddenSlotArrayObserver.cs`/`HiddenSlotPresentationPoc.cs`/`SkillCursorFieldTrace.cs`で継続調査していたが、`stock.skill[8]`書き込みPoCは効果なし(REJECTED)、`CursorPos`系フィールドも native/bridge間で有意差なし(REJECTED)。native側の描画関数(`cmpDrawSkillList`等)を追っていたが2026-09-13〜14セッションで完全にREJECTされた(下記参照)。

## CONFIRMED(2026-09-13〜14, runtime + static)

### `cmpDrawSkillList` / `cmpSkillNameCostDraw` / `cmpMisc.cmpMakeStrCol` はこの画面の描画経路ではない

- 3関数ともHarmony patchが正常適用(`prefixes=1`確認済み)。
- forget UIのseq21/22ウィンドウが確実に開いている状態(`SKILLCURSOR-FIELD-TRACE`/`HIDDEN-SLOT-ARRAY-CHECK`が正常発火)でも、seqゲート無しの無条件呼び出しカウンタが1度も増加しなかった(`SKILLDRAWLIST-UNGATED-HEARTBEAT`等が0件)。
- ユーザーによる実機目視確認: ハイピクシー側・フロスト側とも、この間スキルリスト自体は画面に表示されていた。
- 結論: この画面の実描画は旧来のnative "cmp" static関数群を一切経由していない。HDリマスター化にあたりUnity側UI(TextMeshProUGUI/Canvas)への移行が行われたと考えられる。

### 実描画クラスは `statusUI`(Assembly-CSharp, global namespace, MonoBehaviour)

- シーン上のGameObject `Canvas_UI/campUIBase/statusUI(Clone)`に付いているコンポーネント。
- 関連フィールド(cpp2il stub確認済み、offsetはIL2CPPインスタンスレイアウト):
  - `TextMeshProUGUI[] obtainedText`(0xB0) / `GameObject[] obtainedObj`(0xB8) — 所持スキル8行の表示(`sskill_obtained01`〜`08`に対応)。
  - `GameObject[] skillCurObj`(0x128) — 16要素。**選択ハイライトカーソルの実体**(下記参照)。
  - `GameObject[] skill_base`(0x1A0) — 8要素。行背景/枠候補(未検証)。
  - `GameObject[] update_skill`(0x160) — 8要素。「更新済み」インジケータ候補(未検証)。
  - `GameObject[] awaitObj`(0xC8) / `GameObject[] await2Obj`(0xD0) — 各8要素。習得予定プレビュー欄(`sskill_await2_01`〜`03`はawait2Obj経由と推定)。
  - `Animator skillFull`(0x118) — 「習得枠が一杯です」演出。
  - `TextMeshProUGUI skillHelp`(0x120)、`GameObject skill_select_parts`(0x168)、`GameObject menu_skill`(0x170)、`GameObject menu_cursur`(0x188、原文ママ) — 選択メニュー関連、未解析。

### `skillCurObj[i].activeSelf` ⇔ `obtainedText[i]`の`<material="TMC21">`タグは完全に相関する

- 実機ログ(`STATUSUI-ARRAY-TRACE`)にて、index 0〜6の範囲で「materialがTMC21になる」「`skillCurObj[i].activeSelf==True`になる」が常に同時に観測された(21サンプル中、不一致ゼロ)。
- 通常時のmaterialは`TMC00`。
- これにより`skillCurObj[i]`が選択ハイライトの実体であるとSTRONGLY SUPPORTED(直接のUI要素の視覚確認との突合せは未実施、下記の「フロストのハイライト欠落」観測が間接的な裏付け)。

### フロストのケースで「差し替え行」だけハイライトが発生しない(CONFIRMED runtime + 実機目視)

- フロストの forget フロー中(`bridgeActive=True`、seq21/22)、`obtainedText[7]`(表示上の`obtained08`)の内容が「息吹の具足」→(会話進行後)「マハジオ」へと差し替えられるのを確認。
- この区間、他のindex(5, 6)は`skillCurObj[i].activeSelf`/TMC21が正常にON/OFFを繰り返した(=カーソル移動として自然に動作)。
- **index 7(差し替えられた行)だけは、観測された全サンプルを通じて一度も`skillCurObj[7].activeSelf==True`にならなかった。**
- ユーザーに実機で「その行にカーソルを合わせたときハイライト枠は見えたか」と確認したところ、**「(それがhidden entryの意味なら)見えない」と回答**。runtimeログと実機目視が一致した。
- **これはmaster-archive.md Section 22の「entry exists but highlight not rendered」の再現であり、V3 zero-base移行後も同一現象が残存していることが確定した。**

### 参考: ハイピクシー側では差し替え行でもハイライトが観測された1件

- ハイピクシー側の同種区間(frame=17115、index7="おねだり"への差し替え)では、`skillCurObj[7].activeSelf==True`が1回観測された。
- ただしサンプリングが15フレームに1回・変化時のみのため、フロスト側で真にゼロ回だったのか、単に取りこぼしただけなのかは、この差だけでは判別できない(下記UNRESOLVED参照)。

## モデル再修正(2026-09-14、User指摘、スクリーンショット実証)

実機スクリーンショット(`09-14-26-005701.png`, High Pixie)を直接確認した結果、**`obtainedText[7]`のcontent差し替え仮説そのものが的外れだった**ことが判明した。

画面構成は実際には:
- 左列4個+中央列4個=通常所持8スキル(`obtainedText[0..7]`)
- **右側に完全に独立した3列目**が存在し、そこに「メディア」(ハイライト枠あり、緑〜ティール背景)と「？」(未確定候補)が表示されている

この3列目こそが本来探していた「hidden/pending entry」表示であり、`obtainedText[7]`の差し替えではない。フロストのスクリーンショット(`09-14-26-005753.png`)にはこの3列目自体が存在しない(所持8スキルの2列表示のみ)。

**加えて、`SkillCurObjSetActiveTrace`(event-driven)の実機ログにより、前回セクションの「`skillCurObj[7]`が一度もTrueにならない」という結論はREJECTEDとなった。** 15フレーム間隔のpollingでは`skillCurObj[0]`→`[4]`→`[5]`→`[6]`→`[7]`と自然にカーソルが移動しblinkする様子(True/False交互、約30件)が実際には観測されており、`skillCurObj[7]`(=通常8枠の8番目のハイライト)は正常に機能している。以前の「フロストでindex7だけ光らない」という観測は、15フレーム間隔pollingがblink周期とエイリアシングを起こした結果の見せかけだった可能性が高い。

**次の焦点**: `statusUI.awaitText[]` / `awaitObj[]`(3列目候補、`await2Obj`とは別フィールド)が本命。このフィールドの`active`状態・テキスト内容・(Animatorコンポーネントが乗っているため)Animator再生状態を直接読むtraceを追加した(`StatusUiArrayFieldTrace.cs`拡張)。

## CONFIRMED(static、IL2CPPメタデータ解析、2026-09-14)

### `statusUI`自身は`Awake`/`OnDisable`/`.ctor`の3メソッドしか持たない

- `global-metadata.dat`を直接パースして`statusUI`のTypeDefinitionを確認(methodStart=30973, method_count=3)。
- 3メソッドとも`skillCurObj`等のフィールドを更新するような処理ではありえない(`Awake`/`OnDisable`はライフサイクルフック、`.ctor`は生成時)。
- 結論: `obtainedText[i]`の差し替えや`skillCurObj[i].SetActive(...)`は、**`statusUI`とは別のクラスが、publicフィールドを外部から直接操作することで行われている**。`statusUI`側の逆解析だけでは書き込み元は見つからない。

## UNRESOLVED

- フロスト側で`skillCurObj[7]`が本当に一度もactiveにならないのか、サンプリング粒度の限界で取りこぼしているだけなのか(実機目視では「見えない」と一致しているため、限界に見えるが、native側でどの分岐が`skillCurObj[i]`をsetしているかまでは未特定)。
- `skillCurObj[i]`を実際にset(`GameObject.SetActive`呼び出し)している外部クラス・メソッドが未特定。
- なぜ「差し替え行」だけこの更新ロジックから漏れるのか(差し替えが`obtainedText[i].text`の直接書き換えのみで行われ、`skillCurObj[i]`を連動して更新するはずの本来の「カーソル位置→対象skill特定」ロジックが、差し替え後のtextではなく元のslotのskill IDを参照し続けている、等の仮説はあるが未検証)。
- `skill_base[i]`/`update_skill[i]`の役割(全サンプルで終始False、今回の観測範囲では一度も活性化しなかった)。
- ハイピクシー側での1回のみの`True`観測の意味(真の差なのか、単なる取りこぼしの偶然か)。

## 第三比較ケース(User提案、2026-09-14)

判定力の高い第三ケースとして、以下の条件を満たす仲魔(ダツエバ、鬼女)での比較を予定:

- LvUpで残っていたLvUp習得候補をすべて消化(curriculum枯渇、Frostと同条件)
- その直後にSkill Power-Up AddNewで8枠目が埋まる(Power-Up経由、Frost/High Pixieいずれとも異なる契機)

判定ロジック:
- ダツエバでも3列目(await)が出ない → **curriculum(LvUp候補)残数**が可視化条件の本命
- ダツエバで3列目が出る → Frost固有の別条件が存在する

## CONFIRMED(runtime、2026-09-14、重要な矛盾を発見)

`awaitText[]`/`awaitObj[]`のtrace実装後、実機テストで以下が判明した:

- **フロスト(unit=60)のforgetフロー中(seq21/22、bridgeActive=True含む)、`awaitObj[0].activeSelf`は一貫して`True`、`awaitText[0]`の内容も「息吹の具足」で正しい**(material TMC14→TMC21の変化もハイピクシーと同じパターンで観測された)。
- しかし同じタイミング帯のフロストのスクリーンショット(`09-14-26-005753.png`)には3列目(await候補列)自体が画面に見えない。
- つまり**`activeSelf=True`かつテキストも正しいのに、画面には表示されていない**という矛盾が生じている。これは「論理的には有効だが実際には描画されない」という、このinvestigation全体で追ってきた症状のクラスに一致する。

**解釈の候補**:
1. `activeSelf`だけでは分からない要因(祖先GameObjectの`activeInHierarchy`が実はfalse、または祖先の`CanvasGroup.alpha`が0など)で見た目上非表示になっている。
2. スクリーンショットの撮影タイミングがログのサンプリング区間とずれており、実際には別の瞬間(await解決後)を写している可能性。

`activeInHierarchy`と祖先6階層以内の`CanvasGroup.alpha`を追加で読むtraceを実装・deploy済み(未テスト)。これで解釈候補1と2を切り分けられる。

## CONFIRMED(runtime、2026-09-14、機序の特定)

`activeSelf`/`activeInHierarchy`/`CanvasGroup.alpha`は全て正常だった(前セクション)。RectTransform/CanvasRenderer/Maskの幾何情報もHigh PixieとFrostで完全一致だった。それでも「息吹の具足」のawaitフローでは画面に出ないという矛盾が残っていたが、**新しい事例(静天の会心→会心のordinary Power-Up AddNew)を精査した結果、機序が判明した。**

この事例では:
- `awaitObj[]`/`await2Obj[]`は一切使われない(全スロット`active=False`のまま)。代わりに`obtainedText[7]`(表示上`obtained08`)の内容が直接「会心」に差し替えられる。
- `SkillCurObjSetActiveTrace`(event-driven)により、確認ダイアログ「＞会心をあきらめますか？」が表示される直前まで、`skillCurObj[7]`はカーソル移動に伴い正常にblink(True/False交互)していたことを確認(frame 12869-12903)。
- **ダイアログ表示開始後、`skillCurObj[7]`へのSetActive呼び出しが一切発生しなくなる**(次の`obtainedText[7]`差し替え(frame 13095)を含め、frame 13200まで皆無)。
- `obtainedText[7]`の差し替えが起きた瞬間(frame 13095)、`curObjActive`は`False`のまま。

**解釈(修正版、2026-09-14、実機動画による複数フレーム確認後)**: 当初「確認ダイアログ表示中はカーソルblink更新ループが停止する」という仮説を立てたが、ffmpegで抽出した実機動画のフレーム(1秒間隔、t=26〜29秒、計4フレーム)を直接確認した結果、**ダイアログが表示される前(t=26、説明文は既に「会心」の効果「クリティカルの発生確率アップ」を表示している時点)から、ハイライトが一度も出ていない**ことを確認した。つまり「ダイアログがハイライトを止めている」のではなく、**カーソルが「会心」(=hidden entry)の位置に到達した時点で、そもそも`skillCurObj[]`側の対応するインデックスを解決できず、ハイライトが一度もONにならない**、という説明の方が実態に近い。

`SkillCurObjSetActiveTrace`側の観測(frame 12903以降、index=7へのSetActive呼び出しが一切発生しない)とも整合する: ダイアログが出るより前の時点で、既にindex=7への呼び出し自体が止まっている。

## CONFIRMED(static disassembly、2026-09-14、生きている描画チェーンの特定)

IL2CPPメタデータを直接パースし、`GameObject.SetActive`の実VA(0x182842EB0)への全直接call(1027箇所)を洗い出した上で、以下を確認した:

- `cmpStatus`クラス(`_statusUIScr`フィールドで`statusUI`インスタンスを保持)の`cmpUpdateStatus` → `cmpDrawStatus.cmpDrawStatusCom` → `cmpDrawStatusComEx` → `cmpDrawStatusComEx2`(いずれも`CursorPos`/`CursorMode`引数を持つ) → `cmpDrawStatus.cmpDrawSkill(X, Y, pBaseCol, pStock, pSkillInfo, CursorPos, CursorMode, NextSkillColor, DrawMode, Style)`という描画チェーンが生存していることを確認した(`cmpDrawSkill`が`datSkillName.Get`を呼んでおり、これは本investigationの前段階で既に補正パッチ済みと記録されていた関数と一致)。
- `cmpDrawStatusComEx2`は`rstcalc.rstCreateBeforeSkillList`を呼んでいる。この関数は本プロジェクトの過去investigation(`cmbChkSkillOwner`/`isPromotionCandidate`調査)で既に発見・言及されていたもので、pending候補を含むskill listの構築に関わる。
- 一方、`cmpStatus.cmpDrawObjUI`/`cmpSetupObjUI`(名前上の有力候補だった)は、実際には`skillCurObj`と無関係な「タイトルアイコン切り替え」用の共通ロジックだったことが判明し、REJECTした。
- **上記チェーンの関数範囲(`cmpDrawStatus`クラス全体、`cmpStatus`クラスの該当メソッド含む)には、`skillCurObj`と目される直接SetActive呼び出しが1件も見つからなかった。**

## CONFIRMED(runtime実測、2026-09-14): `skillCurObj`のfield offsetはcpp2il報告通り正確

`StatusUiFieldOffsetProbe.cs`(`ModMain.OnUpdate`駆動に変更後)により実測。全フィールドがcpp2ilの報告値と完全一致:

```
skillCurObj=0x128, obtainedText=0xB0, obtainedObj=0xB8, skill_base=0x1A0,
update_skill=0x160, awaitObj=0xC8, awaitText=0xC0, await2Obj=0xD0
```

→ offset起因の誤りは完全に否定。正確なoffsetでも1027箇所のcall siteに`cmpDrawStatus`/`cmpStatus`関連の直接callerは1件も無いことを再確認(`cmpSetupObjUI`のみヒットしたが、これは無関係な別目的でのoffset一致と既に判明済み)。

**結論**: `skillCurObj[i].SetActive()`は間接呼び出し(vtable/デリゲート/関数ポインタ)経由と判断。

## 実装: ネイティブ呼び出し元のハードウェアブレークポイント捕捉

vtable総当たりではなく、`PowerUpMutationBit6RawProbe.cs`と同じhardware breakpoint + VEH機構を`GameObject.SetActive`の実VA(0x182842EB0)1点に適用する`SkillCurObjNativeCallerProbe.cs`を実装した。

- VEHハンドラ内でRCX(`this`)を`skillCurObj[0..7]`の8ポインタと線形比較(不一致時は即resume、ログ・IL2CPPアクセス・allocation無し)
- 一致時のみ、RSPから直接リターンアドレス(=呼び出し元)とRDX(SetActiveのbool引数)を読み取り記録
- 最大16ヒットまたは3600フレーム(約60秒)で自動uninstall、常駐時間を制限
- `ModMain.OnUpdate`から`Tick()`/`FlushPendingLogs()`を呼ぶ形(rstUpdateには依存しない)

## CONFIRMED(2026-09-14、根本原因の構造を特定): `cmpUpdate.cmpMenuCursor`のインデックス境界チェック

`SkillCurObjNativeCallerProbe`(hardware breakpoint、`GameObject.SetActive`エントリ1点、RCXを`skillCurObj[0..7]`(実際には`[0..15]`まで監視)と比較)により、実際の呼び出し元を捕捉した。

- ヒット: `index=8`、`returnAddress`(ASLR runtime)を静的VAへ変換 → `0x182620AFD`
- 呼び出し元: `cmpUpdate.cmpSetupObject(Obj, set)`(VA 0x182620A80、汎用`Obj.SetActive(set)`風ラッパー) ← さらにその呼び出し元は `cmpUpdate.cmpMenuCursor(Int32 idx, cmpCursorInfo_t CursorObj, cmpCursorInfo_t CursorList)`(VA 0x1826207F0)

`cmpMenuCursor`の冒頭に以下のガードが存在する(byte-exact disassembly確認済み):

```
0x182620811  cmp  edi, dword ptr [r8 + 0x18]   ; idx vs CursorList.Length(IL2CPP配列header)
0x182620815  jge  0x182620890                   ; idx >= CursorList.Length なら
                                                  ; cmpSetupObject(CursorObj, true) を
                                                  ; 一切呼ばずにそのままreturn
0x182620817  xor  r8d, r8d
0x18262081A  mov  dl, 1                          ; set = true
0x18262081C  mov  rcx, rsi                        ; Obj = CursorObj
0x18262081F  call cmpSetupObject                  ; ここが実質SetActive(true)
```

**結論**: `skillCurObj[i]`のハイライトON呼び出しは、`idx >= CursorList.Length`の場合に構造的にスキップされる。ハイライトが「消える」処理が別途走るのではなく、**そもそも立てる呼び出し自体が到達しない**。hidden entry選択時、この関数に渡される`idx`が、その時点の`CursorList.Length`(pending候補リストの長さ)に対して範囲外になっている、というのが根本原因の構造。

`cmpUpdate`クラスは`cmpDrawStatus`/`cmpStatus`(描画チェーン)とは別の、入力/カーソルロジック系クラス。関連メソッド: `cmpMenuCursor`、`cmpSetupObject`、`cmpUpdateSkillSelect`(スキル選択UIの更新本体と推定、未解析)。

## UNRESOLVED

- hidden entry選択時、実際に`cmpMenuCursor`へ渡される`idx`と`CursorList.Length`の具体的な値(まだ実測していない — 今回のヒットはindex=8の通常blink、hidden entry状態そのものではなかった可能性がある)。
- `cmpUpdateSkillSelect`が`cmpMenuCursor`をどう呼んでいるか(呼び出し元での`idx`/`CursorList`の決定ロジック)は未解析。
- `awaitObj`/`await2Obj`経由のケース(息吹の具足)と、`obtainedText[7]`直接差し替えのケース(会心)が、実際に同じ根本原因で説明できるかは未検証。
- ダツエバでの検証結果(未実施)。

## NEXT

1. **(実施済み、実機テスト待ち)** `awaitText[]`/`awaitObj[]`の内容・active状態・Animator状態を`StatusUiArrayFieldTrace.cs`に追加。
2. **(実施済み、実機テスト待ち)** `GameObject.SetActive`をHarmonyでグローバルにフックし、呼び出し対象が`statusUI.skillCurObj[]`の要素かどうかをポインタ比較で判定するevent-driven trace(`SkillCurObjSetActiveTrace.cs`)を追加。
3. ダツエバ(第三ケース)でテストを実施し、native forget / High Pixie型 bridge / Frost型 bridge の3ケースで`awaitText`/`awaitObj`/`eventNums`/`eventOfs`を比較する。
4. 差分が確認できたら、`awaitObj[i]`のactive/Animator状態を実際に制御しているコード箇所を、`GameObject.SetActive`イベントtraceおよびAnimatorパラメータ変更の観測から特定する。

## 現在の優先順位

- native/Unity意味論: Claude
- 実機テスト: User
- Evidence整理: ChatGPT
