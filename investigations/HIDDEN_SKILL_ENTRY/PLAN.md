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

## 次回再開地点(2026-09-14、git保全時点、User記録)

### 次回最優先タスク

User実機テストから再開する。

- High Pixie成功ケース: hidden entry上で数秒待つ。
- Frost失敗ケース: hidden entry上で数秒待つ。

その後、`Latest.log`の以下を比較する:

- `R13WRITEWATCH-FETCH`: `unit` / `bridgeActive` / `byteAtFetch`
- `R13WRITEWATCH-WRITE`: old/new値 / writer VA / 発生有無
- `R13WRITEWATCH-LOOPREAD`: final `loopBound`

### 判定ツリー

**Case 1**: High PixieがFETCH時点で1/2、FrostがFETCH時点で0、かつWRITEが観測されない
→ 差は`r13`取得時点で既に成立している(取得後に壊されているのではない)。
→ 次はstatic slot `0x182E46A50`が参照する候補/pendingリストを誰が構築・更新しているかを追う。

**Case 2**: FETCH時点では両者同値だが、その後WRITEが発生する
→ writer VAを本命としてstatic解析する。
→ High Pixieでは誰が1/2を作り、Frostでは誰が(あるいは何が)0へ変更するかを追う。

**Case 3**: High Pixieのみ0→1/2のwriterがあり、Frostでは同じwriterが一度も来ない
→ 必要な候補リスト構築処理がFrostで発生していない可能性が高い。
→ そのwriterを含む初期化処理全体を解析する。

**重要**: まだ修正PoCには進まない。特に、`[r13+0x10]`だけを直接1へ書く修正は行わない。countだけでなく候補要素本体の初期化も必要な可能性が高いため、writer/構築処理の意味を確認してから修正方針を決める。

## NEXT(2026-09-14更新、writer特定build投入済み)

1. **(次の本命、runtime、User担当)** `R13FetchAndWriteWatchTrace`をHigh Pixie・Frostそれぞれのhidden entry滞在中に実機投入し、上記判定ツリーに従って`R13WRITEWATCH-FETCH`/`R13WRITEWATCH-WRITE`/`R13WRITEWATCH-LOOPREAD`を比較する。
2. writerが特定できたら、AddNewブリッジ側とnative自身の通常forget flow(bridgeActive=False)の双方でこの値がどう決まるかを比較し、修正PoCを検討する(User承認前提、まだ着手しない)。
3. `CursorPos.InvisibleListNums`の実際の役割・更新タイミングを確認し、stale値仮説を検証する(優先度低)。
4. await系(息吹の具足)ケースでも同じ計測を行い、同じ分岐点で説明できるか確認する。
5. 修正方針はUser承認後に着手する。

## CONFIRMED(2026-09-15、実機ログ解析 — writer関数の特定と判定ツリー確定)

前回セッションで実装・deploy済みの`R13FetchAndWriteWatchTrace`のログ(`Latest.log`、`07:50:24〜`、High Pixie=unit59、Frost=unit60、`bridgeActive=True`かつ`target=8`のみ集計)を解析した。

### FETCH/WRITE/LOOPREAD集計

```
FETCH byteAtFetch: unit59=0(215/215)、unit60=0(206/206)  ← 両者とも完全同値
LOOPREAD loopBound: unit59=1(20件)/2(195件)、unit60=0(205件)/1(1件、後述)
WRITE件数: unit59=625件、unit60=207件
```

WRITEのwriterRipRuntime(→静的VA変換、`moduleBase=0x7FFF68480000`実測値を使用)は両者とも同じ2箇所に収束した:

```
writer1 = 0x1822804BD (byteAfterWrite=0、毎フレーム無条件、両者とも100%出現)
writer2 = 0x1822805FF (byteAfterWrite=1→2、増分。unit59は215件中215件出現・うち195件は2回目まで到達。unit60は206件中たった1件のみ)
```

**判定**: FETCH時点は両者完全同値(0)であり、その後WRITEで分岐する。**判定ツリー上はCase 2**(FETCH時点で差がない→writer VAを特定)に該当。ただし実際の分岐パターンはCase 3の記述(「High Pixieのみ増分writerが来る、Frostでは来ない」)にほぼ一致する。unit60の1/206件(frame=7159、`seq=22`、境界フレーム)は、前回のgate cascade調査で既に記録済みの「seq21→22遷移境界での取りこぼしノイズ」と同型であり、安定した成功例とは解釈しない。

### writer関数の特定: `rstcalc.rstCreateBeforeSkillList`(VA `0x182280460`〜`0x182280820`)

IL2CPPメタデータの管理メソッドテーブルを機械的に走査した結果(`.analysis/scratch_find_func_start_r13writers.py`)、writer1・writer2とも**同一関数**`rstcalc.rstCreateBeforeSkillList`の内部にあることが確定した(writer1はエントリから`0x5D`バイト、writer2は`0x19F`バイト)。この関数は本investigationの前段(line 127付近、2026-09-14)で「`cmpDrawStatusComEx2`が呼んでいる、pending候補を含むskill listの構築に関わる関数」として既に言及されていたものと一致する。

`0x182280460`〜`0x182280820`を全体逆アセンブルした(`.analysis/disasm_rstcreatebeforeskilllist_full.py`)。関数シグネチャは概ね`rstCreateBeforeSkillList(byte modeFlag /*rcx→r14*/, StockObj* pStock /*rdx→r15*/, ushort levelParam /*r8w→r12d*/, ListObj* outList /*r9→rsi*/)`(static/非仮想)。

- `mov byte ptr [rsi+0x10], 0`(`0x1822804B9`、writer1の実アドレスはこの1命令前): `outList.count`(=我々が追跡している`[r13+0x10]`と同一フィールド)を毎回0にリセットしてからループ開始。
- `ebx=0..0x18(24)`のループで、`pStock`の"curriculum"的な配列(`[rdi+0x18]`、要素はポインタ配列、strideは8)を24スロットまで走査する。
- ループ本体でのゲート:
  1. **tag一致**: `byte[entry+0x11] == 6`(`word[r15+0x88]!=0`の場合)または`== 1`(それ以外)。不一致ならこのebxはスキップ。
  2. **レベル閾値**: `sbyte[entry+0x10] > word[r15+0x24] + levelParam` (`ebp > ecx`)。満たさなければスキップ。
  3. **所持済みチェック**: `call 0x182410660(pStock=r15, skillId=word[entry+0x12], flag=0)`の戻り値(AL)が**負(sign bit=1)**であることが必要。`>=0`ならスキップ。
- 上記3ゲート全通過時のみ、`outList.byteArray[count]=entry[+0x10]`(`0x1822805EB`)と`outList.wordArray[count]=skillId`(`0x182280611`)を書き込み、`outList.count++`(writer2の実アドレス`0x1822805FC`)。
- **なお`bx(=[r15+0x14]、curriculum残数)==0`の場合は上記ループ自体に入らず別分岐(`0x182280641`〜、固定8スロット・skillID`0x165`固定検索)へ飛ぶが、今回のログでは両unitともこの別分岐のwriteは一度も観測されなかった(`writerRipRuntime`は終始上記2値のみ)。したがって「curriculum残数が0だから」という説明(第三比較ケースの仮説)は今回のHigh Pixie/Frostの差の直接原因ではない**。両者ともcurriculumは残っているが、ループ内の24候補のうち何個が3ゲートを通過するかが異なっている。

### 次のruntime観測点(実装・build・deploy済み、User実機テスト待ち)

`R13FetchAndWriteWatchTrace.cs`にDR3(4本目のhardware execute breakpoint、既存DR0/DR1/DR2はそのまま維持)を追加した。

- 観測点: `0x1822805CC`(`test al,al`、ゲート3=所持済みチェックの`call`直後)。この地点に到達した時点で既にゲート1(tag)・ゲート2(レベル閾値)は通過済みであることが確定している。
- 記録内容: `ebx`(候補スロット番号0〜23)、`skillId`(`r14w`、候補スキルID)、`gate3Al`(所持済みチェックの生の戻り値、符号付き)、`appended`(`gate3Al<0`から導出、実際にリストへ追加されたか)。
- ログ行: `R13WRITEWATCH-GATE3; frame=...; seq=...; bridgeActive=...; unit=...; target=...; ebx=...; skillId=...; gate3Al=...; appended=True/False.`

これにより次のテストで判別できること:
- Frostで`R13WRITEWATCH-GATE3`が**1件も出ない** → 24候補全てゲート1(tag)またはゲート2(レベル閾値)で落ちている(=Frostのcurriculumにはそもそも「該当tagかつ現在レベルを超える」候補が無い)。
- Frostで`R13WRITEWATCH-GATE3`は出るが**`gate3Al`が常に`>=0`(`appended=False`)** → ゲート1・2は通過するが、ゲート3(所持済みチェック)で毎回弾かれている(=候補スキルを全て「既に持っている」と判定されている)。
- 上記いずれの`skillId`がHigh Pixie側の実際に追加された候補(既存`R13WRITEWATCH-WRITE`ログと相関、`writer2`発火時の値)と一致・不一致するかも比較する。

Clean build: error 0、warning 9(既存のみ、今回の変更と無関係)。Deploy先: `Mods\NocturneModernGameplay.dll`。SHA-256(source/deploy一致確認済み): `cf16b83ecd1c4aaece1c8d007b54ccb3477404577eb5b8bb1d88851fc980830e`。

**まだ修正PoCには進んでいない**。`[r13+0x10]`(=`outList.count`)を直接書き換える修正、および候補要素本体(`outList.byteArray`/`wordArray`)を伴わない修正はいずれも見送っている。

### 次回のUser実機テスト依頼内容

High Pixie成功ケース・Frost失敗ケースそれぞれでhidden entry上に数秒滞在し、`Latest.log`の`R13WRITEWATCH-GATE3`行(`bridgeActive=True`、`unit=59`/`60`、`target=8`で絞り込み)を比較する。

## CONFIRMED(2026-09-15、実機ログ解析 — ゲート3は原因ではないと確定)

上記で実装・deploy済みの`R13WRITEWATCH-GATE3`ログ(`Latest.log`、`08:11:20〜`)を解析した結果:

```
bridgeActive=True、target=8でのGATE3ヒット数:
  unit=59(High Pixie): 332件、全件appended=True(gate3Al=-1)
  unit=60(Frost)      : 1件のみ(frame=8109、seq=22、境界フレーム、appended=True)
```

High Pixie側は候補スロットが2つに安定している:

```
ebx=10 -> skillId=353 (156件、全てappended=True)
ebx=12 -> skillId=72  (175件、全てappended=True)
ebx=6  -> skillId=349 (1件のみ、遷移フレームと推定)
```

**結論**: Frostはgate3(所持済みチェック)に**ほぼ到達すらしていない**(332対1、しかもその1件も既知の境界ノイズと同型)。到達した場合は両unitとも常に`appended=True`(gate3は一度も候補を弾いていない)。したがって**gate3(所持済みチェック)はFrost失敗の原因ではない**。真の分岐点はgate1(tag一致)かgate2(レベル閾値)のどちらか、24候補中のどのebxでも通過していないことにある。

## 実装・build・deploy(2026-09-15、CurriculumGateChainTrace — gate1/gate2切り分け)

`R13FetchAndWriteWatchTrace`のFETCH/WRITE(dynamic write-watch)機構は「writerは誰か」という当初の問いに既に答え終えたため撤去し、代わりに`CurriculumGateChainTrace.cs`(新規)を実装した。3本とも固定アドレスのhardware execute breakpointのみ(dynamic reprogramming不要、DR0-DR2使用、DR3空き):

- **LoopReadVa**(`0x1822DA05E`、既存踏襲): 最終`loopBound`のクロスチェック用に維持。
- **Gate1PassVa**(`0x1822805B4`、新規、`cmp ebp,ecx`): gate1(tag一致)を通過した直後の地点。到達した時点でtagは既にmatch済みであることが確定する。この瞬間`rdx`=候補curriculumEntryへのポインタであることを利用し、`ebx`/`skillId`(`[rdx+0x12]`)/`tag`(`[rdx+0x11]`)/`levelThresh`(`[rdx+0x10]`)/`levelBase`(その時のecx)を直接メモリから読み取って記録する。
- **GateCheckVa**(`0x1822805CC`、既存踏襲): gate3(所持済みチェック)の結果、そのまま維持。

判定方法(オフラインで`ebx`ごとに集計): 24候補(`ebx=0..23`)のうち、
- どちらのログにも出ないebx → gate1(tag)で落ちている。
- `Gate1Pass`には出るが`GateCheck`には出ないebx → gate1通過・gate2(レベル閾値)で落ちている。
- 両方に出るebx → gate1・gate2通過、gate3結果は`appended`フィールド通り(前セクションの通り常にTrue)。

ModMain.csの呼び出しを`R13FetchAndWriteWatchTrace.Tick()/FlushPendingLogs()`から`CurriculumGateChainTrace.Tick()/FlushPendingLogs()`へ切り替えた(同一DR0-DR3を奪い合うため同時稼働不可、これまでと同じ制約)。`OnDeinitializeMelon`に`CurriculumGateChainTrace.Uninstall()`を追加。

Clean build: error 0、warning 9(既存のみ)。Deploy先: `Mods\NocturneModernGameplay.dll`。SHA-256(source/deploy一致確認済み): `28ad7dfc4793b1bbceabb04b6523621badb2179199bfdad2d8ddf1b51ffc7161`。

ログ確認方法: `CURRICULUMGATE-INSTALLED`(導入確認)、`CURRICULUMGATE-GATE1PASS; frame=...; seq=...; bridgeActive=...; unit=...; target=...; ebx=...; skillId=...; tag=...; levelThresh=...; levelBase=...;`、`CURRICULUMGATE-GATE3; ...(既存と同形式)`、`CURRICULUMGATE-LOOPREAD; ...(既存と同形式)`を探す。`bridgeActive=True`かつ`target=8`かつ`unit=59`/`60`で絞り込み、`ebx`ごとにHigh Pixie/Frostの`GATE1PASS`出現有無を比較する。

**まだ修正PoCには進んでいない。**

### 次回のUser実機テスト依頼内容

High Pixie成功ケース・Frost失敗ケースそれぞれでhidden entry上に数秒滞在する。`CURRICULUMGATE-GATE1PASS`行がFrost側で1件でも出るか(→gate1は通る、gate2で落ちている)、それとも皆無か(→gate1で落ちている)を確認する。

## CONFIRMED(2026-09-15、実機ログ解析 — 根本原因確定: `pStock[+0x88]`によるcurriculumサブリスト分岐)

`CurriculumGateChainTrace`の実機ログ(`Latest.log`、`08:22:25〜`)を解析した。

### gate1(tag)は両unitとも通っている — gate1は原因ではない

```
GATE1PASSヒット数(bridgeActive=True、target=8):
  unit=59(High Pixie): 947件
  unit=60(Frost)      : 1238件  ← Frostの方がむしろ多い
```

**gate1(tag一致)はFrostでも普通に通過している。gate1はFrost失敗の原因ではない。**

### 決定的な違い: `[r15+0x88]`(pStockのフィールド)の値そのものが違う

`ebx`/`skillId`/`tag`/`levelThresh`/`levelBase`の組み合わせを集計した結果:

```
High Pixie(unit=59)の安定候補(165〜188サンプル、tag=6のグループ):
  ebx=3  skillId=47  tag=6 levelThresh=11
  ebx=6  skillId=44  tag=6 levelThresh=12
  ebx=8  skillId=396 tag=6 levelThresh=13
  ebx=10 skillId=353 tag=6 levelThresh=14
  ebx=12 skillId=72  tag=6 levelThresh=15
  (levelBase=13または14。levelThresh > levelBaseを満たすのはebx=10・12のみ → gate3へ到達 → 既存GATE3ログと一致)
  ※tag=1のグループもebx=0〜6で各1件だけ出現(単発の遷移フレームと推定)

Frost(unit=60)の安定候補(156〜176サンプル、**全てtag=1**):
  ebx=0 skillId=7   tag=1 levelThresh=0
  ebx=1 skillId=412 tag=1 levelThresh=0
  ebx=2 skillId=301 tag=1 levelThresh=0
  ebx=3 skillId=10  tag=1 levelThresh=8
  ebx=4 skillId=400 tag=1 levelThresh=9
  ebx=5 skillId=180 tag=1 levelThresh=10
  ebx=6 skillId=349 tag=1 levelThresh=11
  (levelBase=11または12。levelThresh > levelBaseを満たすものが1件も無い → 全滅)
  ※tag=6のグループはFrostでは一度も出現しなかった
```

**結論(CONFIRMED)**: `rstCreateBeforeSkillList`内のゲート1分岐(`word[r15+0x88]!=0`なら`tag==6`グループ、`==0`なら`tag==1`グループを走査)は、High Pixieでは**安定して`[r15+0x88]!=0`**(tag=6グループ = より高いlevelThreshold(11〜15)を持つ「今後習得予定」の候補群)を走査しているのに対し、Frostでは**安定して`[r15+0x88]==0`**(tag=1グループ = levelThresholdが0または現在レベル以下(0,0,0,8,9,10,11)の「既に到達済み/通過済み」候補群)を走査している。tag=1グループのlevelThresholdは構造的に全て`levelBase`以下であるため、gate2(`levelThresh > levelBase`)を1件も満たせない。

**これがFrost失敗の根本原因である**: gate1・gate3はボトルネックではなく、`[r15+0x88]`という1つのフラグ/フィールドの値の違いが、走査対象のcurriculumサブリストそのものを「今後習得予定(tag=6)」から「既に通過済み(tag=1)」へ切り替えてしまい、結果としてgate2で全滅する。

**なお、`HIDDEN9THGATE-HIT`ログ(2026-09-14、`Hidden9thGateCascadeTrace`、checkpoint3「gate6Pass(field0x88!=null)」)に同じ`0x88`という数値が登場するが、これは別のオブジェクト(`cmpDrawSkill`側のr13相当)に対する既存の別チェックであり、今回の`pStock[+0x88]`と同一フィールドかどうかは未確認。安易に同一視しないこと。**

### 未解決(次の焦点)

`pStock(r15)+0x88`が具体的に何を表すフィールドか(型・意味論)が未特定。仮説:
- 「まだ消化していない自然LvUp候補が残っているか」を示すフラグ/カウント/ポインタ。
- AddNewブリッジが一時的に`pStock`の内部状態を書き換えており、Frostの場合はブリッジ処理の過程でこのフィールドがクリアされてしまっている可能性。

次の調査候補(未着手、User承認前提):
1. `[r15+0x88]`自体の値(0/非0、または実際のポインタ/カウント値)をHigh Pixie/Frostで直接ログするtraceを追加する(rstCreateBeforeSkillListの冒頭、`0x182280500`付近に1点)。
2. `[r15+0x88]`のwriter(誰がいつこのフィールドを設定・クリアするか)を特定する。native自身の通常forget flow(`bridgeActive=False`)とAddNewブリッジ経路の双方で比較する。
3. `pStock`構造体自体の型・他フィールドとの対応(IL2CPPメタデータでの型名解決)を試みる。

**まだ修正PoCには進んでいない。**

## 実装・build・deploy(2026-09-15、User仮説の検証 — `[r15+0x88]`==`hensinmae`確認用プローブ)

User指摘: 本プロジェクトが既に持っている`datUnitWork_s`(cpp2il実機dump、`.analysis/cpp2il_cs/DiffableCs/Assembly-CSharp/newdata_H/datUnitWork_s.cs`)のフィールド対応を確認したところ、以下が確定した(cpp2ilメタデータそのものを直接参照、静的に100%確実):

```csharp
public uint flag;          // 0x10
public ushort id;          // 0x14
...
public ushort level;       // 0x24
...
public int skillcnt;       // 0x48  (01_CURRENT_STATE.mdで既に別investigationからCONFIRMED済み)
public Int32[] skill;      // 0x50
...
public ushort hensinmae;   // 0x88  ← 今回の[r15+0x88]と一致する可能性
public UInt16[] getdevilhearts; // 0x90
public uint hensinmaeexp;  // 0x98
```

**`r15`がpStock(`datUnitWork_s*`)であることの裏付け(静的、cpp2ilメタデータ照合)**:
- `rstCreateBeforeSkillList`内で`[r15+0x14]`(前セクションで「curriculum件数」と暫定していた値)は実は`movzx`(ushort読み)であり、`datUnitWork_s.id`(ushort、0x14)と型・オフセットとも完全一致。
- `[r15+0x24]`(levelBaseとして使っていた値)も`movzx`(ushort読み)であり、`datUnitWork_s.level`(ushort、0x24)と完全一致。
- `[r15+0x88]`も同じく`movzx`(ushort読み)であり、`datUnitWork_s.hensinmae`(ushort、0x88)と型・オフセットが完全一致。
- 3つの独立したushortフィールドが全て一致しており、`r15 == pStock`である可能性が非常に高い。

**Userの仮説**: `hensinmae`(変身前)は「合体で進化する前の元の種族ID」を保持するフィールドであり、ハイピクシー(妖精系から変化した悪魔)は`hensinmae != 0`、フロスト(通常個体)は`hensinmae == 0`という構造的な違いがあるはず。これがtag=6/tag=1分岐の実体であれば、**「Frostが壊れている」のではなく「native側がそもそも変化元情報(hensinmae)の有無でcurriculum参照先を切り替える仕様であり、High Pixieは変化悪魔だからたまたま9番目枠のnative表示経路に乗れていた」**という解釈になる。この場合、`[r15+0x88]`のwriterを追ってMOD側で非0にする方向(hensinmaeは悪魔固有状態であり改変対象ではない)には進まない。

### 実装

`CurriculumGateChainTrace.cs`に4本目のhardware execute breakpoint(`StockFieldProbeVa`、`0x182280480`、関数冒頭`r15`確定直後)を追加した。`[r15+0x14]`(id)/`[r15+0x24]`(level)/`[r15+0x48]`(skillcnt)/`[r15+0x88]`(hensinmae候補)を1回のヒットで同時記録し、既存の`_cachedUnit`(managed側`GBWK.pCurrentStock.id`)と併記してログ出力する。

ログ行: `CURRICULUMGATE-STOCKFIELDS; frame=...; seq=...; bridgeActive=...; unit=...; target=...; r15=0x...; stockId=...; stockLevel=...; stockSkillCnt=...; stockHensinmae=....`

確認予定:
1. `stockId`が既存の`unit`(managed `stock.id`)と一致するか(`r15==pStock`の直接runtime裏付け)。
2. `stockSkillCnt`が妥当な値(既存の所持スキル数、0〜8程度)か。
3. High Pixie(`stockHensinmae`)とFrost(`stockHensinmae`)の生値を比較(Frostは0、High Pixieは非0の具体的な種族ID相当の値であることを期待)。

Clean build: error 0、warning 9(既存のみ)。Deploy先: `Mods\NocturneModernGameplay.dll`。SHA-256(source/deploy一致確認済み): `eda951997c598c4ed586ec936bdf39a43aa3270a90d88c29db5bc24de3b55364`。

**まだ修正PoCには進んでいない。`[r15+0x88]`(hensinmae候補)のwriterを非0化する方向へは進まない(User指摘通り、確定するまで検討しない)。**

### 次回のUser実機テスト依頼内容

High Pixie成功ケース・Frost失敗ケースそれぞれでhidden entry上に数秒滞在する。`CURRICULUMGATE-STOCKFIELDS`行の`stockId`/`stockLevel`/`stockSkillCnt`/`stockHensinmae`を比較し、上記3点を確認する。

## CONFIRMED(2026-09-15、実機テストで`hensinmae`仮説が的中)

`CURRICULUMGATE-STOCKFIELDS`ログ(`08:34:15〜`)を解析した:

```
High Pixie(unit=59): stockId=59(190件、managed側unitと完全一致) stockLevel=13 stockSkillCnt=8 stockHensinmae=61
Frost(unit=60)      : stockId=60(187件、managed側unitと完全一致) stockLevel=11 stockSkillCnt=8 stockHensinmae=0
```

**確定**: `stockId`がmanaged側`unit`と1件のブレもなく完全一致 → `r15 == pStock(datUnitWork_s*)`はもはや推測ではなく直接runtime裏付け済み。`stockHensinmae`はHigh Pixie=61(非0)、Frost=0で安定。**User仮説「`[r15+0x88]`=`hensinmae`」的中**。

**結論の反転**: 「Frostが壊れている」のではなく、「High Pixieは変化悪魔(`hensinmae!=0`)だから、nativeが元々持つ`tag=6`側(今後習得予定)curriculumグループを見られ、たまたま9番目presentation pathが成立していた」。Frost(通常個体、`hensinmae==0`)は構造的に`tag=1`側(現在レベル以下の消化済み候補)しか見えず、gate2で必ず全滅する。`hensinmae`は悪魔固有データであり、writer追跡・改変は行わない(User指摘通り)。

**次の本命(User指定)**: `hensinmae`ではなく、「AddNew bridgeがpending targetをnativeの9番目presentation pathへどう供給するか」の設計フェーズへ移行。方針候補は2つ、Userの第一候補は「native `outList`を再利用し、bridge側から正しく1件だけ供給してnative側の描画・ハイライト・入力に任せる」。ただし`outList.count=1`だけ書く修正は禁止(候補要素本体の初期化も必要)。

## CONFIRMED(2026-09-15、静的解析 — `outList`構造の完全把握、修正設計フェーズ着手)

Userの指示で、書き込み側(`rstCreateBeforeSkillList`)に加えて消費側(`cmpDrawSkill`)の`outList`(`r13`)アクセスを全数洗い出した(`.analysis/disasm_cmpdrawskill_outlist_consumer.py`で範囲disasm、`.analysis/disasm_cmpdrawskill_full_r13_refs.py`で`cmpDrawSkill`全体(`0x1822D97C0`〜`0x1822DB3A0`、1663命令)を走査して`r13`を含む命令のみ抽出)。

### 1. `outList+0x18`(byte[]、levelThresh/type)の用途

**`cmpDrawSkill`全体(1663命令)を走査した結果、`r13`への参照は13箇所のみ、そのうち`[r13+0x18]`への参照は0件だった。** `cmpDrawSkill`はこの screen の唯一生存する描画チェーンであることが既にCONFIRMED済み(前セクション、`cmpDrawStatus`/`cmpStatus`クラス全体の逆アセンブル済み)なので、**「hidden/9th-slot presentation」に限って言えば`outList+0x18`(levelThresh/typeバイト)は表示に一切使われていない**と高い確度で言える。

`r13`(outList)への13箇所の参照内訳:
- `push/pop r13`(呼び出し規約、無関係)×2
- `lea eax,[r13+0x13b]`/`[r13+0x13c]`(別の完全に無関係な計算、`r13`はこの箇所ではレジスタとして別目的で再利用されている定数畳み込みの産物、`outList`とは無関係)×2
- `mov r13,[rsp+0xe0]`(ループ先頭でのスタックからの再読込、`outList`本体) ×1
- `test r13,r13`(既知のLoopReadVa) ×1
- `movsx ebx, byte ptr [r13+0x10]`(count読み取り、既知) ×1
- `mov rax, qword ptr [r13+0x20]`(skillId配列取得) ×4
- `lea ecx,[r12+r13]`(別の完全に無関係な計算) ×2

### 2. `outList+0x20`(skillId配列)の用途— 4箇所全て確認

- `0x1822DA395`(target==8ゲート内、既知): `word[rax+r14*2+0x20]`をスキルID`0x165`と比較するゲート。
- `0x1822DA4C5`(target==8専用presentation block内、新規確認): 配列を取得後`test [rax+0x18],0; jbe <非活性化>`(空チェック)、続けて`0x1822DA4DC`で**固定インデックス0**(`word ptr [rax+0x20]`、乗数無し)を読み**表示用アイコン/テキスト解決(`call 0x1827c0a90`)に渡す**。→ **9番目枠の表示は常に`skillIdArray[0]`のみを見る**。
- `0x1822DA650`(target!=8、通常0..N-1ループ内、既知): `word[rax+r14*2+0x20]`を現在の走査index(r14)で読み、通常枠の表示に使用。
- `0x1822DA75A`(target!=8ブロックの別config分岐、新規確認、`0x1822DA650`と対のペア): 同様に`word[rax+r14*2+0x20]`を読む。

**結論**: 9番目枠(hidden entry)の表示に必要なのは`outList.count>=1`と`outList.skillIdArray[0]`の2つだけ。`outList.byteArray`(levelThresh/type)はこの画面では未使用。

### 3. `rstCreateBeforeSkillList`が1件追加する際の完全なwrite setと順序(byte-exact、既存の全体disasmより再整理)

```
1. byteArray[count]   = bpl (levelThresh, entry+0x10の生byte)   VA 0x1822805EB
2. outList.count      = count + 1                                VA 0x1822805FC
3. wordArray[oldCount] = skillId (entry+0x12のword)               VA 0x182280611
```

**順序に注意: `count`の増分は2つの配列書き込みの"間"で発生する(先にbyteArray、次にcount++、最後にwordArray)。** 他のside fieldへの書き込みは無い(この3命令のみが候補追加時のフットプリント全て、byte-exact disassembly上で確認済み)。

### 4. 配列容量(Length)確認 — 実装・build・deploy済み、runtime確認待ち

既存の`CURRICULUMGATE-LOOPREAD`(DR0、`0x1822DA05E`、毎フレーム発火・count=0のFrostでも発火する)のhandlerを拡張し、新規のhardware breakpointを追加せずに`[r13+0x18]`(byteArrayポインタ)・`[r13+0x20]`(wordArrayポインタ)と、それぞれのIL2CPP配列Lengthヘッダ(`[ptr+0x18]`)を同時記録するようにした。

ログ行: `CURRICULUMGATE-LOOPREAD; frame=...; seq=...; bridgeActive=...; unit=...; target=...; r13=0x...; loopBound=...; byteArrayPtr=0x...; byteArrayLen=...; wordArrayPtr=0x...; wordArrayLen=....`

これによりFrost(`count=0`)でも配列自体が有効か(非null・十分なLength)を直接確認できる。

Clean build: error 0、warning 9(既存のみ)。Deploy先: `Mods\NocturneModernGameplay.dll`。SHA-256(source/deploy一致確認済み): `6fa807dba0b3b6e6ff7dbe8f49fbe5bdda78032739eb922046e9182d6314947f`。

**まだ修正PoCには進んでいない。**

### 次回のUser実機テスト依頼内容

High Pixie成功ケース・Frost失敗ケースそれぞれでhidden entry上に数秒滞在する。`CURRICULUMGATE-LOOPREAD`行の`byteArrayLen`/`wordArrayLen`を比較し、(a)Frostの`count=0`時でも配列自体は非null・十分な容量か、(b)両unitで同じ容量か、を確認する。

## CONFIRMED(2026-09-15、実機テストで容量確認完了、設計材料が出揃った)

```
High Pixie: byteArrayLen=24 wordArrayLen=24 (220件、ブレなし)
Frost     : byteArrayLen=24 wordArrayLen=24 (143件、count=0時でもブレなし)
```

Frostの`count=0`時点でも配列自体は非null・容量24で常に有効。両unitで容量完全一致。**新規IL2CPP配列確保は不要、既存配列のindex 0にそのまま書き込めば良いことが確定した。**

これで3点(`+0x18`未使用/`+0x20`はindex0固定読み/writer write-set)+配列容量の計4点が出揃い、PoC設計に必要なEvidenceが完了した。

## 実装(2026-09-15、PoC — `HiddenSlotCandidateInjectionPoc.cs`、専用ファイルとして分離)

Userの設計方針(native `outList`を再利用し、bridge側から正しく1件供給してnativeのtarget==8 presentation pathにそのまま任せる)に基づき実装した。`AddNewHighlightCorrection.cs`とは意図的に別ファイル(別問題・別native関数・別リスクプロファイル)。

**発見: 同一native関数(`rstcalc.rstCreateBeforeSkillList`)への既存Harmonyパッチが既に存在した**(`OptionFRepeatUnlimitedControl.cs`の`OptionFRepeatUnlimitedExclusionObserver`、Option F機能用)。パラメータ名`pStock`/`pInfo`での named binding はこのコードベースでは信頼できないという既存の教訓(コメント「interop assemblyの実パラメータ名は未確認」)に倣い、**位置引数(`__1`=pStock、`__3`=pInfo)方式**で実装した。Harmonyは同一メソッドへの複数`[HarmonyPatch]`クラスを問題なく併用できるため、既存パッチとは競合しない。

型は`Il2Cppnewdata_H.datUnitWork_t`/`Il2Cppresult2_H.rstSkillInfo_t`(cpp2ilの元namespace`newdata_H`/`result2_H`がinterop assemblyでは`Il2Cppnewdata_H`/`Il2Cppresult2_H`という単一identifierにフラット化される、このコードベース既存の慣習と一致)。

### 発火条件(Postfix、native実行完了後)

```
1. FullCapacityAddNewBridgeState.Active
2. GBWK.SeqInfo.Current == 21 (forget/select presentation中のみ、22は対象外)
3. FullCapacityAddNewBridgeState.Target != 0 (pending target skillId、EventParamではなくbridge state自体をsourceにした、User指摘通り)
4. pStock.id == FullCapacityAddNewBridgeState.WatchedUnit
5. pInfo.SkillCnt == 0 (native自身が既に候補を見つけている場合は一切干渉しない)
6. pInfo.TargetLevel / pInfo.SkillID とも非null、Length>=1
```

### 書き込み順序(User指摘通り、nativeと同一順序には拘らず、要素を先に確定してから最後にcountを公開)

```
1. TargetLevel[0] = 0 (プレースホルダ、cmpDrawSkillからは未読と確認済み)
2. SkillID[0] = (ushort)target
3. SkillCnt = 1  ← 最後に公開、master gateなのでこの瞬間にpresentation blockが成立する
```

### 自己クリーンアップ(実装不要、構造的に保証される)

writer1(`rstCreateBeforeSkillList`自身の`SkillCnt=0`無条件リセット、毎呼び出し・両unit100%で既にCONFIRMED済み)が、このPostfixより先に毎回走る。このPostfixは`Active`かつ`SkillCnt==0`のときだけ注入するため、bridgeが終了(`Active=false`)またはseqが21を離れた瞬間、次のnative呼び出しは自前のリセット後にこのPostfixの発火条件を満たさなくなり、注入した状態は一切残らない。別途cleanupコードは実装していない(User提案の「保証できるなら自然に上書きされる」に該当すると判断)。

Clean build: error 0、warning 9(既存のみ)。Deploy先: `Mods\NocturneModernGameplay.dll`。SHA-256(source/deploy一致確認済み): `7e30dd72d7b148443f452c607d43f6435d94bfca371d8a0152487aff82d8c564`。

**`hensinmae`・curriculum構築ロジック・native writerはいずれも未改変。この Postfix のみが新規コード。**

### 次回のUser実機テスト依頼内容(PoC検証、User提示の成功判定基準)

Frostで満杯8枠からのSkill Power-Up AddNewブリッジを発生させ、forget UIのhidden entry(9番目枠)に数秒滞在する:

```
期待されるログ: HIDDENSLOT-INJECT; frame=...; unit=60; target=<pending skill名>; ...

期待される実機挙動:
- hidden entryの名前・アイコンが見える(今までは何も見えなかった)
- カーソルをhidden entryに合わせるとハイライト枠が表示される
- その状態で決定すると、正しくtarget skillを習得する(従来の論理選択・説明文表示は既に機能していた部分と整合)
```

同時に、この変更がHigh Pixie側(元々成功していたケース)や、Frost以外の通常のPower-Up/Mutationフローに悪影響を与えていないことも確認する(`pInfo.SkillCnt!=0`のケースには一切干渉しないため、理論上は無関係のはずだが実機で確認)。

## CONFIRMED(2026-09-15、実機テストでPoC成功 — hidden entry問題は解決)

`Latest.log`(`09:28:16〜`)で確認:

```
HIDDENSLOT-INJECT; frame=29125〜29216(以降も継続); unit=60; target=299:"会心"; targetLevelLen=24; skillIdLen=24.
(567件、warning/error無し)

FULLCAP-ADDNEW-COMPLETE; unit=60; finalSkills=[7,412,301,13,1,10,180,299,...]; sourcePreserved=True;
targetPresent=True; skillcnt=8.
```

Frostのhidden entry(9番目枠)に名前・アイコン・ハイライトが表示され、決定操作で`target=299(会心)`を正しく習得(`targetPresent=True`)。同ログ内でHigh Pixie(unit=59)側の`FULLCAP-ADDNEW-COMPLETE`も正常(`targetPresent=True`)、既存の成功ケースへの回帰無し。

**READY FOR PRODUCTION: 実機1回分のconfirmationとしてはYES。** ただし以下は今後の継続確認事項として残る(即座のブロッカーではない):

- 今回確認できたのはFrost(`hensinmae==0`)1体、pending target 1種類(`299:会心`)の組み合わせのみ。他ユニット・他skillでの追加確認は未実施。
- `PlaceholderTargetLevel=0`(`TargetLevel[0]`に書く値)は「`cmpDrawSkill`からは未読」という限定的な確認に基づく安全値。他の未特定consumerが存在しないかは依然UNRESOLVED(既存コメントに明記済み)。
- 長時間の連続operation(複数回のforget/select往復、キャンセル→再突入等)でのリーク・不整合は今回未検証。

## 実装(2026-09-15、production化フェーズ1 — 命名・ログ抑制・診断trace整理)

User指示に基づき以下を実施:

1. **命名**: `HiddenSlotCandidateInjectionPoc.cs` → `HiddenSlotCandidateInjection.cs`(クラス名も同様)。他の卒業済み修正(`AddNewHighlightCorrection.cs`等)と同じ「Poc」無し命名規則に統一。
2. **ログ抑制**: `HIDDENSLOT-INJECT`を毎フレーム(実機で567件/セッション観測)から**episode単位でlog-on-change**(`unit`/`target`が変化した最初の1回のみ)に変更。`FullCapacityAddNewBridgeState.Active`が外れた時点で`_lastLoggedUnit`/`_lastLoggedTarget`をリセットし、次episodeで再度ログされるようにした。
3. **`TargetLevel[0]`の扱いを固定**: `PlaceholderTargetLevel=0`を「LOCKED(production)」として名前付き定数のままコメントを強化(cmpDrawSkillからは未読と確認済み、他の未特定consumerが万一現れた場合の修正箇所を1箇所に集約)。
4. **診断trace整理**: `ModMain.cs`の`CurriculumGateChainTrace.Tick()/FlushPendingLogs()`呼び出しをコメントアウト(historical trace chain全体の説明コメントも簡潔化)。`Uninstall()`は旧buildの後片付け用に維持。ハードウェアブレークポイント系のtraceは削除ではなく無効化(将来の類似調査で再利用できるよう温存)。

Clean build: error 0、warning 9(既存のみ)。Deploy先: `Mods\NocturneModernGameplay.dll`。SHA-256(source/deploy一致確認済み): `854752206b7910e5e9daa83c5b251cc23e043975f069da8f476f0bee3c1480a4`。

### 残るproduction化タスク(User実機確認待ち)

- bridge終了/キャンセル/再突入の軽い確認(state leakが無いか)
- 別target skillでの追加確認(今回確認済みは`299:会心`1種類のみ)

これらが確認でき次第、Power-Up AddNewをproduction candidateとして確定し、Mutation AddNewへ移行する。

## CONFIRMED(2026-09-15、実機テストでproduction化タスク完了 — Power-Up AddNew production candidate確定)

`Latest.log`(`09:35〜`)で確認:

```
HIDDENSLOT-INJECT; frame=5095; unit=60; target=16:"マハジオ"; ... (このセッションで1件のみ、log-on-change動作確認)

FULLCAP-ADDNEW-CANCEL; unit=59(High Pixie); reason=target-not-present-after-forget-flow-exit
FULLCAP-ADDNEW-COMPLETE; unit=60(Frost); finalSkills=[...,16]; sourcePreserved=True; targetPresent=True; skillcnt=8
```

1. **ログ抑制確認**: 567件→1件(log-on-change正常動作)。
2. **別target skill確認**: `16:マハジオ`(前回`299:会心`)でも正しく名前表示・習得成功。
3. **キャンセル/再突入確認**: unit=59での正常キャンセル後、別unit(60)・別targetでの新規episodeが state leak無く正常完了。warning/errorはセッション通算0件。

**READY FOR PRODUCTION: YES。Power-Up AddNewをproduction candidateとして確定する。**

### 次のフェーズ

Mutation AddNewへ移行する(User方針)。Power-Up AddNew側の`HiddenSlotCandidateInjection.cs`は今後も有効なまま維持(Mutation側は別のbridge実装になる見込みのため、直接の再利用可否は別途検討)。

## 追加修正(2026-09-15、同日、Mutation AddNew検証中に発見)

`HiddenSlotCandidateInjection.cs`の当初ガード(`if (pInfo.SkillCnt != 0) return;`、native自身が候補を見つけていれば注入しない)は、hensinmae!=0のunit(High Pixie)でMutation/Power-Up AddNewのtargetがハイライトされないバグを引き起こすことが判明した。native自身が既に別の候補(curriculum由来、AddNew targetとは無関係)を見つけてしまうため、注入がスキップされ9枠目にはnative自身の候補が表示されていた。

**修正**: ガードを撤廃し、AddNewブリッジがActiveな間は`SkillID[0]`を常にブリッジのtargetで上書きする方式に変更(`cmpDrawSkill`はindex 0のみを読むためCONFIRMED済み)。`SkillCnt`はnativeが既に1以上を書いていればそのまま尊重し、0のときのみ1へ引き上げる。同時に、`FullCapacityAddNewBridgeState.Active`単独チェックだったガード条件を、`MutationAddNewBridgeState.Active`も認識するよう拡張した(詳細は`investigations/ACQUISITION_LEARNASNEW/PLAN.md`参照)。

実機確認(2026-09-15): High Pixie(unit=59)でMutation AddNew(target=32:ムド等)・Power-Up AddNew(target=39:メディア等)双方でハイライト・名前表示が正常化。既存の成功ケース(Frost、High Pixie旧経路)への回帰無し。

## 現在の優先順位

- native/Unity意味論: Claude
- 実機テスト: User
- Evidence整理: ChatGPT
