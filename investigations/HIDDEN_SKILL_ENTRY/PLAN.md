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

## REJECTED(2026-09-14、上記`cmpMenuCursor`本命説)

`cmpMenuCursor`(VA `0x1826207F0`)を「ハイライトON呼び出しの本命候補」として追っていたが、User指摘により再検証した結果REJECTEDとなった。`SkillCurObjNativeCallerProbe`(当時: `GameObject.SetActive`自身の入口にhardware breakpoint)が捕捉していた`returnAddress=0x182620AFD`は、`.analysis/disasm_cmpmenucursor_0x1826207f0.py`での再逆アセンブルの結果、`cmpMenuCursor`の範囲外(`cmpMenuCursor`自体は`0x18262089F`の`ret`で終了)であり、実際には別関数`cmpUpdate.cmpSetupObject`(VA `0x182620A80`)内、`call 0x182842eb0`(=本物の`GameObject.SetActive`)の直後の命令だったと判明した。`cmpSetupObject`は`GameObject.SetActive`の薄いラッパー(現在値と希望値が同じならno-op)であり、`GameObject.SetActive`自身の入口にbreakpointを置く限り、捕捉できる呼び出し元は常に`cmpSetupObject`1箇所に収束してしまう(1階層深い本当の呼び出し元は見えない)、という構造的な限界だったことが分かった。`cmpMenuCursor`が本当に無関係なのか、あるいは正しい経路の一部なのかは、この時点では未確定のまま次のステップへ進んだ。

## CONFIRMED(2026-09-14、根本原因確定 — User承認済みCanonical State)

`cmpSetupObject`(VA `0x182620A80`)自身の入口へbreakpointを移して(`SkillCurObjNativeCallerProbe.cs`改修)実機テストしたところ、通常行0〜7の`cmpSetupObject(true)`呼び出しが**全て同一の呼び出し元**(staticVa `0x1822D9C6B`)に収束することを確認した。この呼び出し元を含む関数(VA `0x1822D97C0`〜、`.analysis/disasm_0x1822d9c6b_context.py`/`disasm_0x1822d9c6b_funcstart.py`)を逆アセンブルした結果、以下のゲートを発見した(byte-exact確認済み):

```
0x1822D9AF3  movzx ecx, byte ptr [rax+0x14]   ; CursorPos.Shift
0x1822D9AF7  movsx eax, word ptr [rax+0x12]   ; CursorPos.Index(sign-extend)
0x1822D9AFB  add   ecx, eax                    ; target = Shift + Index
0x1822D9AFD  cmp   ecx, ebx                     ; target vs ebx(このループ回で検討中の行)
0x1822D9AFF  jne   <この行へのcmpSetupObject(true)呼び出しをスキップ>
```

`ebx`は`0`から`loopUpper`(`=[r15+0x48]`、実測`8`)未満までしか回らない。この`target == ebx`ゲートを`HighlightTargetGateTrace.cs`(`0x1822D9AFD`へのhardware breakpoint)で直接計測した結果、**hidden entry滞在中は`target=8; loopUpper=8; ebx=0〜7全てmatch=False`**を実機で確認した。同時刻の既存trace(`SkillCursorFieldTrace.cs`)も同一frameで`CursorPos.Index=0; CursorPos.Shift=8`(=`target`と完全一致)を独立に記録しており、さらに別の既存trace(`HIDDEN-SLOT-ARRAY-CHECK`)でも同一frameで`cursor=8`のとき`selectSkillID`が所持8スキル配列(`skill=[...]`、8要素)の外側の値(pending new skillのID)を指すことを確認した。

**結論(CONFIRMED — static disassembly + 独立した2系統のruntime計測 + 実機症状が一致)**: hidden entry滞在時、`CursorPos.Index=0`/`CursorPos.Shift=8`、したがってハイライト判定用の`target`(`=Shift+Index`)は`8`になる。一方、ハイライト描画側ループは`ebx=0..7`(`loopUpper=8`)しか走らないため`target == ebx`が一度も成立せず、`cmpSetupObject(skillCurObj[ebx], true)`がどの行に対しても呼ばれない。その結果、論理選択・説明文表示・決定操作は機能するが、選択ハイライトだけが表示されない。

### `CursorPos.Shift=8`のwriter(2026-09-14、CONFIRMED)

`CursorPosShiftWriteWatchTrace.cs`(hardware write breakpoint、`CursorPos+0x14`監視)で実機捕捉した。

- native自身のforget flow開始時(seq=8、bridgeActive=False): VA `0x182288AC1`(`mov byte ptr [rcx+0x14], 8`)で**無条件に**書き込まれる。直前に`call 0x1822eefa0`(`SkillCursorFieldTrace.cs`のコメントで既に「pure UI、副作用なし」と分類されていた関数)を呼んでいる。
- AddNewブリッジ経路(seq=21、bridgeActive=True): 大きなdispatch関数(`cmpUpdateSkillSelect`と推定)内、VA `0x182289313`(`mov byte ptr [rax+0x14], 8`)で、入力方向コード(レジスタ`bx`)と別のレジスタ`dil`の値次第で**条件付きに**書き込まれる。同じ関数内に`Shift=0`へのリセット(VA `0x1822892DE`)、`Shift+=4`/`Shift-=4`の分岐も存在し、4刻みのページ送り/折り返しロジックの一部と見られる。
- 通常のカーソル移動(0〜7の増減)は別のwriter(VA `0x1822EDE25`ほか)が担当し、こちらは`8`を一切生成しない。

両経路とも「8」は偶然でも壊れた値でもなく、明示的に埋め込まれた定数である。

### スコープ確定(2026-09-14、CONFIRMED、User訂正済み)

nativeの通常Power-Upは上書き方式であるため、`Shift=8`の状態を「9番目の選択肢」としてプレイヤーに操作させる状況が発生しない。一方AddNewブリッジでは、その内部状態(`Shift=8`)をユーザー操作可能なUIまで持ち込んでしまうため、通常のハイライト描画ループ(`ebx=0..7`)との不整合が可視化される。**したがって本現象はAddNewブリッジ経路に固有**であり、native自身の`Shift=8`書き込みを「単なる初期化上の副産物」と断定することはまだしない(native側で実害のある可視状態に到達しないことまでがCONFIRMED)。

### `SelectSkillID`の解決元(2026-09-14、訂正)

`HIDDEN-SLOT-ARRAY-CHECK`で観測していた「cursor=8のときselectSkillIDがpending skillを指す」という挙動は、`AddNewHighlightCorrection.cs`(このMOD自身のコード)が`FullCapacityAddNewBridgeState.Active`時のみ行っている補正であり、native自身の挙動として確認されたものではなかった。native自身がcursor=8時に`SelectSkillID`をどう扱うかは、上記スコープ確定によりそもそも調査不要と判断した(nativeの通常経路ではcursor=8がプレイヤー操作可能な状態まで到達しないため)。

`GBWK.SelectSkillID`の生オフセットは`SelectSkillIdOffsetProbe.cs`(値一致スキャン、5サンプルで収束)により`GBWK+0x9C`とCONFIRMED。`SelectSkillIdWriteWatchTrace.cs`(hardware write breakpoint)も実装済みだが、上記の理由により今回のnative側テストでは意味がないと判断し、実機データは未取得。

### 未解決の重要な手がかり(2026-09-14、次の本命)

`SkillCurObjNativeCallerProbe`の実機ログで、通常行0〜7の`cmpSetupObject(true)`呼び出しは全て`0x1822D9C6B`に収束する一方、**index=8だけは別のcaller(`0x1822DA5FB`)から`value=True`が観測された**(前回セッション、`SKILLCUROBJ-NATIVE-CALLER-HIT; index=8; ... staticVa=0x1822DA5FB`)。ゲーム側に「9番目/pending用の別表示経路」が本来存在する可能性があり、次の調査対象。この経路が

- `Shift==8`を条件にしているか
- await/pending state(`awaitObj`/`await2Obj`)を参照しているか
- `skillCurObj[8]`を何の条件でONにするか

を静的解析で特定できれば、MOD側で`skillCurObj[7]`等を無理やり光らせるのではなく、**ゲームが本来持つ9番目/pending用表示経路をAddNewブリッジから正しく使う**修正が可能になる。

## UNRESOLVED(旧、2026-09-14以前・参考記録)

- `cmpUpdateSkillSelect`が`cmpMenuCursor`をどう呼んでいるか(呼び出し元での`idx`/`CursorList`の決定ロジック)は未解析(上記REJECTEDの通り、`cmpMenuCursor`自体が本命かどうかは未確定のまま保留)。
- ダツエバでの検証結果(未実施)。

## CONFIRMED(2026-09-14、High Pixie成功ケース vs Frost失敗ケース比較)

`0x1822DA5FB`(index=8への`cmpSetupObject(true)`呼び出し元)を静的解析した結果、`0x1822DA3FB`〜開始の関数内に`target==8`専用の描画・ハイライト経路が実在することを確認した(`cmpDrawSkillのような専用9番目slot presentation path`)。`Hidden9thSlotPathTrace.cs`(hardware execute breakpoint、`0x1822DA48C`=分岐エントリ/`0x1822DA5FB`=呼び出し完了)で実機比較した結果:

- **High Pixie成功ケース**: `target==8`専用pathが繰り返し発火。`BRANCH-ENTRY`→`CALL-DONE`が対になって発火し続けた。`skillCurObj[8]`は`activeSelf=True`/`activeInHierarchy=True`、`await2_01`のテキストも`TMC21`(ハイライト材質)で正しく表示された。
- **Frost失敗ケース**: `seq=21`、`bridgeActive=True`、hidden entry表示中(`CursorPos.Shift=8`/`target=8`は維持)でも、`BRANCH-ENTRY`は0件。`skillCurObj[8]`は`activeSelf=False`/`activeInHierarchy=False`のまま。

`Hidden9thGateCascadeTrace.cs`(hardware execute breakpoint x4、`0x1822DA3FC`カスケード開始点〜`0x1822DA46C`gate6通過点)による追加計測(ログ: `investigations/HIDDEN_SKILL_ENTRY/logs/Latest-frost-gatecascade-20260914-131317.log`)では、Frostのhidden UI表示中(frame 7616〜8469付近)、`0x1822DA3FC`以降のcascade checkpointがほぼ発火しなかった(`bridgeActive=True`期間のヒットは実質1件のみ、seq21→22遷移直前の境界ケース)。

**ただしここから「`0x1822DA3FC`を含む関数自体が呼ばれていない」と断定してはいけない。`0x1822DA3FC`が本当にfunction entryそのものかは未確認であり、関数には入っているがそれより前のpre-gateで別経路へ抜けている可能性が残っている。**

## CONFIRMED(static disassembly、2026-09-14、Case A/B切り分け完了)

IL2CPPメタデータの`Assembly-CSharp.dll` `CodeGenModule.methodPointers`テーブルを全件走査し(`.analysis/scratch_find_func_start_0x1822da3fc.py`)、`0x1822DA3FC`以下・以上で最も近い管理メソッドVAを機械的に特定した。

- `0x1822DA3FC`以下で最も近い管理メソッドVA: `0x1822D97C0` = `cmpDrawStatus.cmpDrawSkill`(距離`0xC3C`バイト)。
- `0x1822DA3FC`以上で最も近い管理メソッドVA: `0x1822DB3A0` = `cmpDrawStatus.cmpDrawStatusComEx2`(距離`0xFA4`バイト)。

`0x1822D97C0`から`0x1822DB3A0`までの領域(`0x1BE0`バイト)をbyte-levelで走査し(`.analysis/scratch_verify_cmpdrawskill_contains_target.py`)、`ret`直後に3バイト以上の`0xCC`(int3)paddingが続く箇所(=関数境界)が一つも無いことを確認した。`0x1822D97C0`自体は標準的なMSVC/IL2CPPプロローグ(パラメータspill → `push rdi/r12/r13/r15` → `sub rsp,0x98`)を持つ正規の関数エントリである。

**結論(CONFIRMED — static disassembly)**: `0x1822DA3FC`は独立した別関数ではなく、`cmpDrawSkill`本体(エントリ`0x1822D97C0`)の内部、エントリから`0xC3C`バイト先に位置する。`cmpDrawSkill`は通常行0〜7のハイライトループ(`0x1822D9AFD`の`target==ebx`ゲート含む)と全く同じ関数であり、この`0x1822D9AFD`はFrost hidden entry滞在中も`HighlightTargetGateTrace`で発火が直接確認済み(`target=8; loopUpper=8; ebx=0〜7全てmatch=False`、既存CONFIRMED runtime)。`0x1822D9AFD`は関数エントリから`0x33D`バイトの位置にあり、同じ連続した1関数内で`0x1822DA3FC`より手前にあるため、`0x1822D9AFD`の発火は`cmpDrawSkill`のエントリ自体が毎フレーム実行されていることの間接証明になる。

**→ Case A確定(関数自体は呼ばれている。`0x1822D9AFD`と`0x1822DA3FC`の間のどこかにpre-gateが存在し、そこでFrostのみ経路が外れる)。Case B(関数自体が呼ばれていない)は棄却する。**

### 新知見: `0x1822DA3FC`直前のゲート構造(static disassembly、`.analysis/scratch_disasm_span_0x1822d9afd_to_0x1822da3fc.py`)

`0x1822D9AFD`から`0x1822DA3FC`まで通しで逆アセンブルした結果、以下の構造を確認した(byte-exact):

- `0x1822D9AFD`(`target==ebx`不一致)で`0x1822D9C7C`へ分岐した後、`r14b`(3値、`[rsp+0x100]`から一度だけ読み込まれるbyteパラメータ)による3-way switch(`test r14b,r14b` → `sub 1` → `cmp 1`)で経路が分かれるが、最終的にどの経路も`0x1822DA051`の共通ループへ合流する。
- `0x1822DA051`ループ: `r15d`(ループindex、`0`から`ebx=[r13+0x10]`未満まで)。`r13`はこの関数の別のパラメータ由来のポインタ(`[rsp+0xE0]`)。ループ内で`statusUI`とは別の"source object"風chain(`[rip+X][+0xb8][+8]`、既知の`GBWK+0xb8`系パターンと同型)経由で`+0xc8`/`+0xd0`/`+0x128`/`+0x130`オフセットの配列へ`cmpSetupObject(obj,set)`(=`skillCurObj`等のSetActiveラッパー、既知VA`0x182620a80`)を複数回呼んでいる。`+0xc8`/`+0xd0`は`statusUI.awaitObj`/`await2Obj`と同一offsetであり、この番号が一致するのは偶然ではなく、このループ自体が「awaitObj/await2Obj/9番目slot」presentationの本体である可能性が高い(未確定、次回要検証)。
- `0x1822DA3FC`直前の即時ゲート列(`0x1822DA3AC`〜`0x1822DA3FC`):
  1. `cmp word ptr [rax + r14*2 + 0x20], 0x165` / `jne` — 候補配列の要素(word、stride 2)がskill ID `0x165`(=357)と一致するかの分岐。不一致なら次のチェックへ、一致した場合は追加関数(`0x18203f220`)を呼び、`al==0`なら`0x1822DA852`へ大きく迂回する。
  2. `test bpl,bpl` / `js 0x1822DA40D` — `bpl`が負なら`0x1822DA3FC`をスキップ。
  3. `cmp sil,1` / `jne 0x1822DA40D` — `sil != 1`なら`0x1822DA3FC`をスキップ。
  4. `cmp bpl,8` / `jne 0x1822DA40D` — `bpl != 8`なら`0x1822DA3FC`をスキップ。
  5. 上記全て通過した場合のみ、IL2CPP診断ログガード定型句(`[rcx+0x12f]`のbit2 test等、他箇所でも頻出する既知パターン)を経て`0x1822DA3FC`(`test r12,r12`)へ到達する。

### `bpl`/`sil`/`r14b`の出自(static disassembly、`.analysis/scratch_disasm_prologue_ebp_esi_origin.py`)

`0x1822D9949`〜`0x1822D9959`で、関数の**スタック渡しパラメータ**から一度だけ読み込まれていることを確認した:

```
0x1822D9949  movzx ebp, byte ptr [rsp+0xE8]
0x1822D9951  movzx esi, byte ptr [rsp+0xF0]
0x1822D9959  movzx r14d, byte ptr [rsp+0x100]
```

3つともbyteサイズで、`cmpDrawSkill`のシグネチャ末尾(`CursorMode`/`NextSkillColor`/`DrawMode`/`Style`等の列挙型引数群、`01_CURRENT_STATE.md`記載のシグネチャ参照)のいずれかに対応すると推定される(未確定、パラメータ名との1対1対応は未特定)。

**重要な留保(CONFIRMEDへ未昇格の理由)**: `ebp`/`esi`/`r14d`は`CursorPos`構造体経由の毎回dereference(`target=Shift+Index`のような)ではなく、関数呼び出し時に一度だけ渡される引数値である。したがって`bpl==8`ゲートが「Frostでは不成立・High Pixieでは成立」という**呼び出し元側の値の違い**によるものなのか、あるいは常に固定値でありこのゲート自体はFrost/High Pixie間で差が無く別の分岐(`r14b`の3-way switchや`ebx=[r13+0x10]`のループ境界)が真の分岐点なのかは、**呼び出し元(`cmpDrawStatusComEx2`と推定)がこれらの引数に何を渡しているかを未解析のため、現時点では未確定**。

### 次の焦点(更新)

Case A/Bの切り分けは完了した(Case A確定)。次はCase A内部でのpre-gate特定に進む。優先順位:

1. `cmpDrawSkill`の呼び出し元(`cmpDrawStatusComEx2`、既知)が、スタック引数`[rsp+0xE8]`/`[rsp+0xF0]`/`[rsp+0x100]`(= 関数内`ebp`/`esi`/`r14d`)に何を渡しているかを静的解析する。固定値なら`bpl==8`ゲートはFrost/High Pixie間で無差別と判断し、`r14b`の3-way switchまたは`ebx=[r13+0x10]`ループ境界(`r13`の実体特定含む)を次の焦点にする。
2. `r13`(`[rsp+0xE0]`由来のポインタ、`0x1822DA051`ループの走査対象)の実体を特定する。`+0x10`(loop bound)と`+0x20+i*2`(ループ内でaccessされるword、`0x1822DA070`の`[r13+0x10]`および後続の`[rax+r14*2+0x20]`等、複数の別objectとの混同に注意)の意味論。
3. 上記1・2が判明してから、runtime hardware breakpointで`bpl`/`sil`/`r14b`実測値、または`ebx=[r13+0x10]`実測値をFrost hidden-entry滞在中とHigh Pixie成功時で比較する(次回実機テスト)。

**重要**: まだ修正PoCには進まない。上記の静的解析(呼び出し元の引数値、`r13`実体)を先に完了させること。

## 次回再開地点(2026-09-14)

現在の重要CONFIRMED:

1. hidden entry時の論理カーソル: `CursorPos.Index=0`、`CursorPos.Shift=8`、`target=Shift+Index=8`。
2. 通常skill highlight: `0x1822D97C0`〜の処理で`target==ebx`を判定し、`ebx=0..7`のみ走査。`target=8`では通常0..7 highlightは成立しない。
3. ただしゲーム側には`target==8`専用pathが別に存在する。`0x1822DA483`: `cmp ecx,8` — `target==8`なら専用ブロックへ入り、最終的に`cmpSetupObject(skillCurObj[8], true)`を呼ぶ。runtimeでも`index=8`/`caller=0x1822DA5FB`を捕捉済み。
4. High Pixie成功ケース: `target==8`専用pathが繰り返し発火。`BRANCH-ENTRY`→`CALL-DONE`が対になって発火。`skillCurObj[8]`: `activeSelf=True`/`activeInHierarchy=True`。`await2_01`も`TMC21`で正しくハイライト表示。
5. Frost失敗ケース: `seq=21`、`bridgeActive=True`、hidden entry表示中でも`CursorPos.Shift=8`/`target=8`は維持。しかし`target==8`専用pathの`BRANCH-ENTRY`は0件。`skillCurObj[8]`: `activeSelf=False`/`activeInHierarchy=False`。
6. 最新Frost gate cascadeログ: `investigations/HIDDEN_SKILL_ENTRY/logs/Latest-frost-gatecascade-20260914-131317.log`。Frostのhidden UI表示中(frame 7616〜8469付近)、`0x1822DA3FC`以降のcascade checkpointはほぼ発火せず。`bridgeActive=True`期間のヒットは実質1件のみ。**ただしここから「containing function自体が呼ばれていない」と断定してはいけない。`0x1822DA3FC`がfunction entryそのものか未確認。関数には入っているが、それより前のpre-gateで別経路へ抜けている可能性が残っている。**

### 次回の最優先タスク(2026-09-14更新、Case A/B切り分け完了済み)

**Case A/Bの切り分けは静的解析で完了した(上記「CONFIRMED(static disassembly、2026-09-14、Case A/B切り分け完了)」参照、Case A確定)。** `0x1822DA3FC`は`cmpDrawSkill`本体(エントリ`0x1822D97C0`)の内部にあり、独立した別関数ではない。したがって当初計画していた「DR0=function entry / DR1=0x1822DA3FC」のruntime比較は、function entry側は`0x1822D9AFD`(既存`HighlightTargetGateTrace`)の発火で既に間接証明済みのため、優先度を下げる。

(このタスクは下記「CONFIRMED(static disassembly、2026-09-14、呼び出し元チェーン全解析 — `bpl`/`sil`ゲートの出自を`fclChkMessage`まで特定)」で完了した。)

## CONFIRMED(static disassembly、2026-09-14、呼び出し元チェーン全解析 — `bpl`/`sil`ゲートの出自を`fclChkMessage`まで特定)

`cmpDrawSkill`呼び出し元を1段ずつ逆アセンブルで遡り(直接call xref scan、各関数のプロローグからスタックフレームsize差分を計算して引数slotを対応付け)、以下の呼び出しチェーンをbyte-exactで確定した:

```text
rstdraw.rstDraw (VA 0x182282B70、switch dispatch on [source object+0x10]+0x11)
  ├─ seq=21 → rstDrawSeqDestroySkill(pStock, param2=1)   VA 0x1822830F6→0x182283102
  └─ seq=22 → rstDrawSeqDestroySkill(pStock, param2=0)   VA 0x182283109→0x182283115
       ↓ (VA 0x182282900のrstDrawSeqDevilStatusSkillPUpとは別、rstDrawSeqDestroySkill = VA 0x1822824D0)
rstdraw.rstDrawSeqDestroySkill
  → cmpDrawStatus.cmpDrawStatusComEx(引数, param3=sil, param4=r15b)   call site VA 0x18228289D
    → cmpDrawStatus.cmpDrawStatusComEx2(引数, arg5=bpl, arg6=sil, arg12=r14d)  call site VA 0x1822DBCF7
      → cmpDrawStatus.cmpDrawSkill(...)  call site VA 0x1822DB846
```

各段でスタック渡し引数のみが未確定のまま伝播しており(comEx/comEx2とも当該slotへの自前write無し、純粋pass-through)、`cmpDrawSkill`内の`bpl`/`sil`/`r14d`はそれぞれ以下に帰着する:

- **`sil`(= `cmpDrawSkill`内、`0x1822DA3D2`の`cmp sil,1`ゲート)** ← `cmpDrawStatusComEx`の`param4`(`r9b`) ← `rstDrawSeqDestroySkill`**自身の`param2`**(`rstDraw`のjump tableからそのまま渡る定数、`seq21`なら`1`、`seq22`なら`0`)。
- **`bpl`(= `cmpDrawSkill`内、`0x1822DA3D8`の`cmp bpl,8`ゲート)** ← `cmpDrawStatusComEx`の`param3`(`r8b`) ← `rstDrawSeqDestroySkill`**自身のローカル変数`sil`**(このローカルは`rstDrawSeqDestroySkill`独自の一時変数で、上記チェーン外部の`cmpDrawSkill`側`sil`とは別物。以下参照)。

### `rstDrawSeqDestroySkill`内部ロジック(CONFIRMED static disassembly、byte-exact)

```csharp
// 疑似コード。VAは全てrstDrawSeqDestroySkill(0x1822824D0)本体内。
int cursorIndex = cmpMisc.cmpGetCursorIndex(rbx);   // call VA 0x18228255E, 実名確認済み(IL2CPPメタデータ完全一致)
byte sil;
if (param2 == 0) {                                   // r15b、自身のparam2(dl)。seq22で0、seq21で1
    sil = 0xFF;                                       // VA 0x18228256A、無条件強制
} else {
    bool bMsg = fclMisc.fclChkMessage(0);              // call VA 0x18228258F、実名確認済み
    bool suppress = (bMsg != 0);                       // call VA 0x182225BA = 0x18138BE10、下記参照
    if (suppress) {
        sil = 0xFF;                                     // VA 0x1822825C3
    } else {
        sil = (byte)cursorIndex;                         // VA 0x1822825C8以降、esiをそのままsilとして使用
    }
}
// sil はこの後 comEx の param3(r8b) として渡る → 最終的に cmpDrawSkill の bpl になる
```

- `cmpMisc.cmpGetCursorIndex(_ptr)`: VA `0x1822ED1C0`、IL2CPPメタデータで実名確認済み(offset+0、`_ptr`の1パラメータ)。
- `fclMisc.fclChkMessage`: VA `0x182169960`、IL2CPPメタデータで実名確認済み(offset+0)。引数`0`(定数)。
- `0x18138BE10`(旧称"secondary flag-check"): int3paddingで関数境界確認済み(`0x18138BE0E`直前まで`int3`×2、直後`0x18138BE16`以降`int3`×10)。**中身は`test cl,cl; setne al; ret`の3命令のみ**。IL2CPPメタデータの管理メソッド境界とは一致せず(nearest matchは`ComputeStringHash`内部+巨大offsetで無関係)、CRT/codegenの汎用bool正規化thunkと判断する。**独自の判定ロジックは一切持たない(`return (cl != 0)`のみ)**。したがって呼び出し元の分岐は実質的に`fclChkMessage(0) != 0`そのものであり、`0x18138BE10`自体は分岐要因から除外できる。

**結論(CONFIRMED — static disassembly)**: `seq21`(通常の選択中、プレイヤーが実際に操作する場面)においても、`bpl==8`ゲート(hidden entryの9番目専用presentation path起動条件)へ到達できるかどうかは、最終的に**`fclMisc.fclChkMessage(0)`の戻り値のみ**で決まる。`0!=0`(=falsy)なら実カーソルインデックスがそのまま`bpl`まで伝播し(hidden entry滞在中なら`cmpGetCursorIndex()==8`のはずで`bpl==8`ゲート成立)、非0(truthy)なら`sil`(rstDrawSeqDestroySkill内ローカル)が無条件`0xFF`に潰され、`cmpDrawSkill`側`bpl`も`0xFF`となり`bpl==8`ゲートは絶対に成立しない。

**留保**: `fclChkMessage`という名前から「メッセージウィンドウ表示中判定」という意味を推定しているが、この関数自体の内部実装は未解析(呼び出し先の中身は見ていない)。名前と引数`0`からの意味論的解釈であり、CONFIRMEDとするのは「これが分岐を最終的に決めている」という構造的事実までである。「メッセージウィンドウ表示中だから無効化される」という意味論的解釈はSTRONGLY SUPPORTEDまでに留め、CONFIRMEDへは昇格しない。

### 次のruntime観測点(確定、最小構成)

```text
観測対象: fclMisc.fclChkMessage(0) の戻り値(AL)
観測地点: rstDrawSeqDestroySkill内、call 0x182169960 の直後(VA 0x182282594、または movzx ebx,al 実行後のVA 0x18228259B)
観測条件: seq==21 かつ カーソルがhidden entry上(既存SkillCursorFieldTrace等で判定可能)

期待される決定打:
  High Pixie hidden entry: fclChkMessage(0) == 0  → sil=cursorIndex(=8) → 専用path成立
  Frost hidden entry:      fclChkMessage(0) != 0  → sil=0xFF           → 専用path不成立
```

## REJECTED(2026-09-14、実機runtime、`fclChkMessage`仮説)

`FclChkMessageResultTrace`による実機観測(`unit=59`/`unit=60`、AddNewブリッジ中の`bridgeActive=True`区間、計421ヒット)の結果:

- `unit=59`(High Pixie想定): `bridgeActive=True`中の193ヒット全てで`fclChkMessageResult=0`。
- `unit=60`(Frost想定): `bridgeActive=True`中の228ヒット全てで`fclChkMessageResult=0`。
- `cursorIndex`は412サンプルで`8`(hidden entry上)、9サンプルで`4`(移動中)だったが、`fclChkMessageResult`はどちらのunit・どちらのcursorIndexでも例外なく`0`(suppressされない側)だった。

**結論(REJECTED)**: `fclChkMessage(0)`の戻り値はHigh Pixie/Frost間で差が無い(両方とも`0`=非suppress)。もし本仮説が正しければ両者とも`sil=cursorIndex=8`となり`bpl==8`ゲートを通過できるはずだが、既存CONFIRMED runtime(`Hidden9thSlotPathTrace`)ではHigh Pixieのみ`target==8`専用path(`0x1822DA48C`)へ繰り返し到達し、Frostはゼロ回だった。この矛盾により、**`fclChkMessage`ゲートはFrost失敗の直接原因ではないと結論する**。Frost/High Pixieの真の分岐点は、`0x1822DA3FC`(gates 1-2通過点)から`target==8`専用block(`0x1822DA483`)までの間で、まだ詳細に追っていない区間(`0x1822DA051`ループの走査対象`r13`とその`+0x10`(loop bound)、`0x1822DA3AC`のskill ID `0x165`比較、`0x1822DA032`の`r14b`3-way switch)のどこかにある可能性が高い。

`FclChkMessageResultTrace`自体は今回の観測目的を達成した(read-only、副作用なし)ため、次のtraceに置き換える前提で無効化してよい。

## 実装・build・deploy(2026-09-14、このセッション、r13/`[r13+0x10]`観測)

`r13`の出自静的解析(上記REJECTEDセクション末尾参照)を踏まえ、`fclChkMessage`系traceを置き換える形で以下を実装した。

- `src/SkillMutationV3/CmpDrawSkillR13LoopBoundTrace.cs`(新規、read-only): `cmpDrawSkill`内、候補スキャンループ入口の`test r13,r13`直後(VA`0x1822DA05E`)に1点だけhardware execute breakpointを置き、`r13`(ポインタ値)と`[r13+0x10]`(loop bound byte、`r13`が非nullの場合のみ安全にguardして読む)をログ出力する。`target`(`CursorPos.Shift+CursorPos.Index`、managed context側でTick()ごとにキャッシュ)も併記する。GameAssembly.dllへの書き込みは無し。
- `src/ModMain.cs`: `FclChkMessageResultTrace.Tick()/FlushPendingLogs()`呼び出しを`CmpDrawSkillR13LoopBoundTrace`に差し替え(同一DR0-DR3を奪い合うため同時稼働不可)。`OnDeinitializeMelon`に`CmpDrawSkillR13LoopBoundTrace.Uninstall()`を追加(`FclChkMessageResultTrace.Uninstall()`は残置、既存状態のクリーンアップ用)。
- Clean build(`bin`/`obj`削除 → `dotnet build`): error 0、warning 9(全て既存warning、今回変更と無関係)。
- Deploy先: `C:\Program Files (x86)\Steam\steamapps\common\smt3hd\Mods\NocturneModernGameplay.dll`。SHA-256(source/deploy一致確認済み): `e604e3a72521e6eecd3adcacaf04f1619471896138ffff5844fc159252232fdb`。

ログ確認方法: MelonLoaderの`Latest.log`で`R13LOOPBOUND-INSTALLED`(導入確認)、`R13LOOPBOUND-HIT; frame=...; seq=...; bridgeActive=...; unit=...; target=...; r13=...; loopBound=...;`(各ヒット)を探す。`seq=21`かつ`target=8`(hidden entry上)の行の`r13`(null/非null)と`loopBound`がHigh Pixie/Frostで異なるかを比較する。

## CONFIRMED(2026-09-14、実機runtime — 決定的な分岐点を特定)

`CmpDrawSkillR13LoopBoundTrace`による実機観測(`bridgeActive=True`区間、`unit=59`242ヒット・`unit=60`238ヒット、全て`target=8`)の結果:

- `unit=59`(High Pixie想定): `r13`は常に非null(毎フレーム新規取得されるため値自体は毎回異なるが、nullは一度も無し)。`loopBound`(`[r13+0x10]`)は`1`または`2`(0は一度も無し)。
- `unit=60`(Frost想定): `r13`は常に非null(unit=59と同様)。`loopBound`は**例外なく全238ヒットで`0`**。

**結論(CONFIRMED runtime)**: `cmpDrawSkill`内の候補スキャンループ(`0x1822DA070`〜`0x1822DA089`: `ebx=[r13+0x10]; cmp eax,ebx; jge <skip全体>`、`eax`はループ内で不変の`0`)は、`loopBound<=0`のとき最初の比較で即座にループ全体を抜ける構造である(static disassembly、既確認)。Frostは`loopBound`が常に`0`であるため、このループ本体(`r15d`走査、skill ID `0x165`比較、最終的に到達する`target==8`専用block`0x1822DA483`を含む)に一度も入らない。High Pixieは`loopBound`が`1`/`2`であるため入り、`target==8`専用presentation pathへ到達できる。

**これが「Frostだけhidden entryのハイライトが表示されない」という当初症状の直接原因(native側の分岐点)として確定した。** `r13`自体の正体(IL2CPPのstatic-field-fetch idiomで毎フレーム新規取得される、GBWK/ACTIONと同一static clusterの`0x182E46A50`経由のオブジェクト)は未特定のままだが、その`+0x10`が「(pending/候補)要素数」的なカウントフィールドであることは、この挙動から強くSTRONGLY SUPPORTEDされる(名前・型は依然UNRESOLVED)。

### 訂正(User指摘、2026-09-14)

上記の「native自身のforget flowでは1以上、AddNewブリッジでは0のまま」という解釈は、今回のデータからは言えない。**High Pixie・Frostとも今回観測したのはAddNewブリッジ経路(`bridgeActive=True`)のみ**であり、確定している差はあくまで「High Pixie bridge = 1/2」対「Frost bridge = 0」である。native自身の通常forget flow(`bridgeActive=False`)との比較は行っていない。

### 次: `[r13+0x10]`のwriter特定(2026-09-14、実装・build・deploy済み、runtime観測待ち)

`r13`は`cmpDrawStatusComEx2`が毎回(ほぼ毎フレーム)新規に取得するオブジェクトであり(`R13LOOPBOUND-HIT`ログでunit=59/60ともpointer値が毎回異なることを確認済み。ただし両者とも同一ヒープ帯域(`0x1F1861...`〜`0x1F50EE...`)に収まっており、同種のオブジェクトであることは既存ログのみで確認できた)、静的アドレスへのhardware write breakpointは「オブジェクトが存在する前には仕掛けられない」という原理的な制約がある。

`0x1800E6930`(r13を返す呼び出し先)を静的disassemblyしたところ、`jmp 0x1800B53C0`→`jmp 0x1800B53F0`という薄いtrampolineで、その先はIL2CPPの総称的な型解決・仮想/interfaceメソッド呼び出し解決の内部機構(`0x1800ADA10`/`0x1800AB8B0`/`0x1800A1D50`等)であることを確認した。ゲーム固有ロジックではなく、これ以上深追いしても意味論的な手がかりは得にくいと判断する。

上記制約を踏まえ、以下を実装した(`src/SkillMutationV3/R13FetchAndWriteWatchTrace.cs`、read-only):

- **DR0**(fixed execute): `cmpDrawStatusComEx2`の取得直後(VA`0x1822DB41A`、`mov [rsp+0x60],rax`)。この瞬間の`r13`(=RAX)と`[r13+0x10]`の値を記録し(=最も早い時点での観測値。ここで既に1/2なら、取得呼び出し自体の内部で値が決まっていることになる)、その場で**DR1を今フレームの`[r13+0x10]`へ動的に再設定**する。
- **DR1**(dynamic write watch、1byte): 直前のDR0ヒットで設定されたアドレスへの書き込みを検知する。ヒット時、writerのRIP(runtime、ASLR未補正)と書き込み後の値を記録する。
- **DR2**(fixed execute): `cmpDrawSkill`側の最終読み取り地点(VA`0x1822DA05E`、既存の`CmpDrawSkillR13LoopBoundTrace`と同一点)。1回のテストでfetch時点・write検知・最終読み取りの3点を同時に得られる。

`src/ModMain.cs`: `CmpDrawSkillR13LoopBoundTrace.Tick()/FlushPendingLogs()`呼び出しを`R13FetchAndWriteWatchTrace`に差し替え(同一DR0-DR3を奪い合うため同時稼働不可)。`OnDeinitializeMelon`に`R13FetchAndWriteWatchTrace.Uninstall()`を追加。

Clean build: error 0、warning 9(既存のみ)。Deploy先: `Mods\NocturneModernGameplay.dll`。SHA-256(source/deploy一致確認済み): `e8fdd90f59334dadc7729150fa617cdbff3ca04d6a8ce9541d5bb41aa583c203`。

ログ確認方法: `R13WRITEWATCH-INSTALLED`(導入確認)、`R13WRITEWATCH-FETCH; ...; r13=...; byteAtFetch=...;`(取得時点の値)、`R13WRITEWATCH-WRITE; ...; addr=...; byteAfterWrite=...; writerRipRuntime=...;`(書き込み検知、あれば)、`R13WRITEWATCH-LOOPREAD; ...; loopBound=...;`(既存traceと同じ最終値)を探す。`writerRipRuntime`が得られたら、`runtimeAddr - actualModuleBase + 0x180000000`でstatic VAへ補正してから逆アセンブルする(このmodの他probeと同じ手順)。

### READY FOR PRODUCTION: NO(根本原因はCONFIRMED、修正実装はまだ)

## NEXT(2026-09-14更新、writer特定build投入済み)

1. **(次の本命、runtime、User担当)** `R13FetchAndWriteWatchTrace`をHigh Pixie・Frostそれぞれのhidden entry滞在中に実機投入し、`R13WRITEWATCH-FETCH`(最も早い時点の値)・`R13WRITEWATCH-WRITE`(writer捕捉、あれば)・`R13WRITEWATCH-LOOPREAD`(最終値)を比較する。特に、`byteAtFetch`の時点で既にHigh Pixie=1/2・Frost=0が分かれているか(=分岐が取得呼び出し自体の内部で決まっている)、それとも`R13WRITEWATCH-WRITE`が観測されるか(=取得後に何かが書き換えている)を確認する。
2. writerが特定できたら、AddNewブリッジ側とnative自身の通常forget flow(bridgeActive=False)の双方でこの値がどう決まるかを比較し、修正PoCを検討する(User承認前提、まだ着手しない)。
3. `CursorPos.InvisibleListNums`の実際の役割・更新タイミングを確認し、stale値仮説を検証する(優先度低)。
4. await系(息吹の具足)ケースでも同じ計測を行い、同じ分岐点で説明できるか確認する。
5. 修正方針はUser承認後に着手する。

## 現在の優先順位

- native/Unity意味論: Claude
- 実機テスト: User
- Evidence整理: ChatGPT
