# NocturneModernGameplay Skill Mutation Research Master Archive

**Scope**: This archive covers the "Skill Mutation: Learn As New Skill" feature investigation for `NocturneModernGameplay` (SMT3 Nocturne HD Remaster, Steam, IL2CPP build). It is a full historical record, not a curated summary. Rejected hypotheses, superseded conclusions, and failed validation methods are preserved deliberately, per the archival directive that produced this document.

**Primary sources**: This chat's full investigation history (Claude static/native analysis, Codex build/validation reports, real-machine logs), plus two standalone research notes produced near the end of the investigation (`2026-08-30_skill-mutation-native-write-timing.md`, `2026-08-30_skill-mutation-learn-as-new-flow.md`), which are absorbed into this archive as Sections 9 and 13 respectively, with the wording corrections noted during review applied.

**Evidence states used throughout**: `CONFIRMED` / `HYPOTHESIS` / `REJECTED` / `UNRESOLVED`. No other state labels are used.

---

## 1. Scope and Purpose

The mod modifies vanilla SMT3HD Skill Mutation behavior. In vanilla, when a demon's level-up triggers a "Skill Mutation" (skill change), the game overwrites the existing skill in place. The mod's goal ("Learn As New Skill") is to preserve the original skill and add the mutated skill as a new entry instead:

- If the demon has an open skill slot (fewer than 8 skills), the mutated skill is simply added.
- If all 8 slots are full, a synthetic "forget skill" UI is triggered, letting the player choose which skill to discard to make room, using the same native UI flow as a normal manual skill-forget action.

This is inherently risky because it inserts MOD-driven state transitions into native systems that were not designed for external interruption, and because SMT3HD's battle-result screen manages multiple demons (multi-demon level-up) using shared/global native state (`GBWK`). Most of this archive documents the discovery, mischaracterization, and eventual correct characterization of exactly how that shared state works, and how the mod safely interacts with it.

A secondary feature, "Repeatable Skill Power-Up" (clearing `stock+0x10` bit6 at a confirmed native completion boundary), was developed alongside this investigation and is documented in Section 15.

---

## 2. Executive Current Understanding

As of the end of this investigation:

- The mod's **v2.1 Global Pending Queue architecture** (Capture → MOD owns → Drain → Restore → Verify → Native owns) is implemented and has been confirmed stable across multiple real-machine test sessions, including 2-demon-simultaneous scenarios.
- A major bug — **native re-entry into the same demon's level-up lifecycle after synthetic completion** — was found and fixed. Root cause: `rstUpdateSeqDestroyConfirm`'s Postfix never called the transaction-completion check, so the `seq=8/last=21` boundary (the moment a forget-confirm resolves) passed unchecked, and native advanced to `seq=9` (a fresh Mutation calculation) before the MOD-side transaction was ever marked complete. This is believed to be the root cause behind several previously-separate-looking symptoms: the "349 zombie" repeated-candidate loop, HP fluctuation, and repeated skill-change animations.
- Two problems remain deliberately unresolved and open (see Section 20):
  - **Investigation A**: The exact timing of `ref int` argument copy-back between native and managed code for `rstOverWriteSkill`, which is why the skill array does not appear to reflect the mutated value immediately at that method's Postfix.
  - Per-unit "Inline Gate" (Design B) is not implemented; it remains a UX-improvement research track, explicitly separated from bug-fix work (see Section 8.7).
- `cancel`/`completed` outcomes for queued Learn-As-New transactions are now understood to reflect **player choice during the forget UI** (does the mutated skill survive to the end of the synthetic flow), not native write success/failure. This was initially conflated and later separated (Section 13).
- A single static field slot that "looked like GBWK" by pattern (`[static][+0xb8][0]`) was discovered to actually be **two distinct static slots** (real GBWK vs. a separate "TARGET" context) at different absolute addresses; multiple earlier findings had to be re-examined once this was confirmed (Section 5.3).

### 2.1 Update (2026-08-30, latest real-machine video review)

New real-machine video evidence, reviewed after the Master Archive above was first compiled, adds the following without changing any of the findings above:

- The Queue architecture's **internal correctness** (original-skill preservation, mutated-skill addition, forget UI, final-add redirect, `HasSkill`-based completion, multi-demon ownership/restore) remains `CONFIRMED` and unchanged.
- New evidence shows that, independent of internal data correctness, the Queue architecture produces a **user-visible presentation/lifecycle mismatch** — see the new Section 7.5. The design judgment reached from this evidence is: Queue is functionally validated but produces user-visible lifecycle/presentation mismatch, and is now classified as a **validated functional prototype** rather than the final presentation-quality implementation. This is a `Correction` to the framing (not the content) of Section 2's earlier "v2.1 is stable" statement — the underlying mechanics are still confirmed stable; what changed is the *design conclusion drawn from that stability* (see Section 8.8).
- A new design direction, **Inline Transaction Architecture**, is now the prioritized target for the final implementation (Section 8.8). This is explicitly **not** the same proposal as the earlier, rejected-as-tested Inline Gate V1/V2 experiments (Section 8.7, Section 16 items 6–7) — see Section 8.8 for the distinction.
- A new, independent finding — **Hidden New Skill Entry** — was identified in the Frost-type (no-remaining-native-learn-skill) case (Section 22).
- A new core design problem — **Native Replacement Suppression** — was formalized as a target for the Inline Transaction Architecture direction (Section 23), explicitly still gated on Investigation A's `UNRESOLVED` status (Section 9).

---

## 3. Investigation Timeline

1. **Baseline feature review**: `SkillMutationAlways` (forces `dil=0`/mutation-eligible rolls) and `SkillMutationLearnAsNew` (the preserve-original-skill feature) established as the two features under study.
2. **bit6 discovery**: `stock+0x10` bit6 found to gate the *ordinary* skill power-up roll (`dil=0` branch of `rstCalcSkillPowerUpCore`), set once and never cleared except in one gated spot. Initially conflated with Mutation reliability; later separated (Section 15).
3. **HandledSlots redesign**: legacy `_mutationHandled` (single global bool) replaced with `HashSet<(IntPtr Stock, int Slot)>` to fix a suspected but not directly logged "one demon blocks another's Mutation" bug from earlier symptom reports.
4. **Problem confirmed via control-flow trace**: `HandledSlots.Contains==true` blocks only the MOD's own conversion (`TryConvertReplacementToAddition`), never the native `rstOverWriteSkill` call. Confirmed via disassembly of `rstUpdateSeqSkillPowerUp`'s Harmony Prefix return value handling.
5. **Candidate global-state problem found**: `_candidate`/`_candidateIndex`/`_originalSkill`/`_mutatedSkill` were single global fields; `RecordMutationResult` never updated `_candidateIndex`, allowing stock/index to originate from different units. Redesigned to `Dictionary<(IntPtr Stock, int Index), CandidateInfo>` with explicit 0/1/2+-match resolution rules.
6. **Pending scheduler stuck bug found**: `FinishActiveAndContinueQueue(fromCalc=true)` skipped `StartNext()` even when `Pending.Count > 0`; the only fallback (`TryStartQueuedAtResultBoundary`, gated on `seq==11 && TargetIndex==TargetCnt+1`) fires only once per full result-lifecycle ("all demons done"), observed to fire in only 3 of 53 `rstUpdate` Postfix calls in one session.
7. **Native Context Snapshot/Restore protocol (v2.1) designed and implemented**: Capture (`pCurrentStock`/`WorkStock`/`Target*`/`SeqInfo`) once at the `seq==11` boundary, drain the whole Pending queue under MOD ownership without re-capturing, restore all fields at the end with pointer/target/seq verification, fail-closed on mismatch.
8. **rstCalc re-entrancy hazard found and fixed**: synthetic completion originating from `rstChkAddSkill` (`fromCalc=true`) can occur while still inside `rstCalc`'s own call stack; directly calling `StartNext`/`RestoreNativeContext` from there was deferred via `_drainContinuationRequested`, consumed safely from `rstUpdate`'s Postfix (`ContinueOwnedDrainIfRequested`).
9. **Cross-unit display investigation**: `rstUpdateSeqDestroySkill`'s skill-name display path confirmed via static analysis to be a direct `WorkStock.skill[cursor]` read (function `0x182285c40`, no intermediate cache). Once telemetry was scoped to *confirmed* `rstUpdateSeqDestroySkill`/`rstUpdateSeqDestroyConfirm` callers (via synchronous scope flags), `currentUnit != workUnit` mismatches dropped to 0/12 observed cases. **This branch is closed as normal behavior** (Section 7.1).
10. **Experimental Inline repro (bit6 clear/restore hack, `ExperimentalInlineRepro.cs`)**: attempted on a separate branch to test whether native's own per-unit gate (bit6) could be reused as an inline-processing signal. Uncovered a **real bug**: `StartExperimentalInline`'s call into `StartPreparedForgetFlow` set `GBWK.WorkStock` but never `GBWK.pCurrentStock`, directly reproducing a "two Frosts" display corruption. Strongest direct evidence found for a genuine ownership bug pattern — but it was in *experimental* code, never merged into production.
11. **349 "zombie" loop identified and root-caused**: `rstChkAddSkill`'s underlying candidate selector (`0x196542270`) is fully stateless — its only filter is `HasSkill(candidate)==false` — so an unaddable candidate is re-offered indefinitely across native calc cycles. Confirmed via real-machine timestamps: recurrences ~7.6s / ~1089 frames apart, i.e. **separate native calc cycles**, not a single-call internal loop.
12. **Design A vs Design B evaluation**: static analysis of `rstSetCurrentDevil`/`rstCalcSeqDevilLevelUp` established that next-unit transition logic exists **duplicated** in two places, both gated by a jump-table dispatch on `SeqInfo+0x11` (confirmed identical to the managed `seq.Current` property). Index 6 of that table is the only path reaching `rstCalcSeqDevilLevelUp`.
13. **Inline Gate V2 experiment**: a minimal, scoped Harmony Prefix on `rstCalcSeqDevilLevelUp`, gated to fire only when `GBWK.pCurrentStock` matched the *currently active* Learn-As-New transaction's stock. Real-machine result: **the gate never fired** even during successful and cancelled transactions — because `rstCalcSeqDevilLevelUp` is never called at all while `_active != null`.
14. **"Post-level-up event" investigation (seq 11–14)**: static jump-table decode showed `seq=11` performs a ~1/3 native RNG roll; on success it sets `GBWK+0x7a` (event-type reservation) and separately computes `WorkStock+0x7c` (a per-unit message/SE-selection value). `seq=12` consumes `GBWK+0x7a` to enqueue a native event/message-queue entry. Confirmed no calls into `rstCalcSkillPowerUpCore`/`cmbGetMutationSkill` in this path — **not** a re-Mutation. A real-machine comparison suggested `GBWK+0x7a` may be lost across a GBWK regeneration while `WorkStock+0x7c` survives. **Remains UNRESOLVED**.
15. **GBWK vs TARGET static-slot confusion discovered and corrected** — the single most important methodological correction in this investigation. Absolute-address resolution of RIP-relative static-field loads proved **two separate static slots exist**: the real GBWK singleton at `0x182e464b8`, and a second "TARGET" context at `0x182e4ed30`, both referenced heavily inside `rstSetCurrentDevil` and `rstCalcSeqDevilLevelUp`.
16. **`GBWK+0x58` "candidate demon list" hypothesis rejected**: telemetry consistently returned `len=0` — a real, structurally-valid but always-empty array via the real-GBWK slot.
17. **`WorkStock` vs `pCurrentStock` provenance resolved**: both `rstSetCurrentDevil` and `rstCalcSeqDevilLevelUp` fetch the candidate demon via the **TARGET** slot, while the final `WorkStock` assignment happens via the **real GBWK** slot. Now `CONFIRMED` as the structural reason `WorkStock`/`pCurrentStock` are legitimately different native objects by design.
18. **Repeatable Skill Power-Up feature designed and implemented** (Section 15): bit6 cleared only at a statically-confirmed native completion boundary, operating on the Prefix-captured `oldStock` only. A `unit==0` guard was added after real telemetry showed the feature clearing bit6 on a native placeholder stock.
19. **349-zombie loop-detection/abort mechanism designed and implemented**: detection continues even while `_active != null` (corrected after review). Abort never touches `_active`/`Pending` directly from the detection call site; it raises a flag consumed at the confirmed `rstCalc` return boundary, finalized from `rstUpdate`'s Postfix. A `DrainClosing` guard prevents new conversions from starting mid-abort.
20. **`rstOverWriteSkill` write-target investigation (Investigation A)**: `fixed(ref __0)` physical-address experiment attempted, then rejected (constant `refAddr` across 7 distinct real-machine cases). Replaced with value-based confirmation, which revealed **Pattern C**: at `rstOverWriteSkill`'s own Postfix, neither `pCurrentStock` nor `WorkStock`'s skill array reflects the mutated value yet. Remains `UNRESOLVED` (Section 9).
21. **`MUTATION-PROBE sequence-change` vs. paired-snapshot discrepancy resolved via execution-order analysis**: `ObserveMutationSequence` runs, then calls `TryConvertReplacementToAddition`, which writes `stock.skill[index] = originalSkill` as part of its own design. Only *after* that does the paired-snapshot logger re-fetch `GBWK.pCurrentStock` live. Array-pointer-identity telemetry returned `True`/`True` in all 8 sampled cases — **ruling out "different stock entity"**.
22. **Il2CppInterop array-semantics static analysis performed**: confirmed `datUnitWork_t.skill` is `Il2CppStructArray<int>`, getter/indexer always read live native memory with no managed-side cache. Definitively **rejected** the "stale managed wrapper cache" hypothesis.
23. **Learn-As-New cancel/completed semantics resolved**: `HasSkill(item.Stock, item.MutatedSkill)` is the sole basis for `_activeCancelled`. `OverrideQueuedLearnSkill` is the actual final-commit mechanism. A 5-round real-machine A/B test (rounds 1-4: discard mutated skill; round 5: keep it) produced `cancelled` in all 4 discard cases and `completed` in the 1 keep case.
24. **Master Archive compiled** (this document, first version).
25. **2026-08-30, latest real-machine video review**: Queue flow functional correctness reconfirmed (no change to prior CONFIRMED findings). New user-visible presentation mismatch confirmed, evaluated against pointer/stock/`HasSkill`/completion evidence and judged more consistent with a presentation/lifecycle timing mismatch model than a data-corruption model (Section 7.5). Hidden New Skill Entry confirmed in the Frost-type (no-remaining-native-learn-skill) case, using the High Pixie-type (still-has-native-learn-skill) case as a comparison baseline (Section 22). Queue architecture reclassified as a validated functional prototype rather than the final presentation-quality implementation. Final UX direction shifted toward a newly-proposed Inline Transaction Architecture (Section 8.8), explicitly distinguished from the earlier, rejected-as-tested Inline Gate V1/V2 experiments (Section 8.7). Native Replacement Suppression added as a new core design problem for that direction (Section 23), still gated on Investigation A (Section 9).

---

## 4. Native Function Map

All addresses are `moduleBase + RVA` against `GameAssembly.dll`; treat as build-specific, not fixed across game/mod versions.

| Function | Class | Role | Evidence |
|---|---|---|---|
| `rstCalcCore.cmbGetMutationSkill` | `rstCalcCore` | Rolls/returns the Mutation result for a given (stock, original skill) pair, on the `dil=1` branch of `rstCalcSkillPowerUpCore`. `argStockPtr` confirmed to be the **actual `pCurrentStock`**. | Static disasm + Prefix telemetry |
| `rstcalc.rstCalcSkillPowerUpCore` | `rstcalc` | Native dil=0/dil=1 dice roll. `dil=0` checks/sets `stock+0x10` bit6; `dil=1` (Mutation) does **not** reference bit6 — confirmed by cases where `bit6=True` and `result=2` occurred together. | Static disasm + `CORE-TRACE` |
| `rstcalc.rstChkAddSkill` | `rstcalc` | Attempts to add a skill to a stock. Underlying candidate selector (`0x196542270`) confirmed fully stateless — root cause of the 349-zombie loop. | Static disasm |
| `rstcalc.rstCalcSeqDevilLevelUp` | `rstcalc` | Per-unit level-up calc loop; contains an **inline duplicate** of the next-unit-transition logic also in `rstSetCurrentDevil`. Reached only when `SeqInfo+0x11==6`. Alternates between real-GBWK and TARGET slots. `WorkStock+0x4a` cursor increment happens *inside* this function's loop only. Returning non-zero causes `rstCalc` to take a confirmed early-return path. | Full static disasm; `DEVIL-TRANSITION-TRACE` |
| `rstcalc.rstSetCurrentDevil` | `rstcalc` | `public static sbyte rstSetCurrentDevil()`. Same next-unit-transition pattern as the inline copy. **No static caller found** in the 4 analyzed classes; real-machine telemetry recorded **0 calls** across a full 2-demon session. | Full static disasm; slot verification; 0 real-machine hits |
| `rstcalc.rstCalc` | `rstcalc` | Top-level per-frame calc dispatcher. `SeqInfo+0x11`-indexed (0–24) jump table; owns the real-GBWK static-slot re-assignment logic, confirmed plain pointer reassignment with **no field-copy logic**. 970-instruction body. Hooked (Postfix) as the 349-zombie abort's confirmed calc-return boundary (`0x18227f767–778`). | Full static disasm; jump-table decode |
| `rstupdate.rstOverWriteSkill` | `rstupdate` | Native body: `*slot = skill` (RVA `0x2285ff0`). C# managed method is a normal IL body, `miIL=True`, RVA `0x1b6644`, 77-byte Fat-format body, not fully decoded. Exact `ref int` copy-back timing UNRESOLVED (Section 9). | Static disasm; IL header decode; telemetry |
| `rstupdate.rstAddSkill` | `rstupdate` | Ordinary skill-add path; hooked for write-stage telemetry. | Telemetry only |
| `rstupdate.rstUpdateSeqSkillPowerUp` | `rstupdate` | Update-phase Mutation write path; calls `rstOverWriteSkill`. Hooked for `ObserveMutationSequence` then `ObservePairedSkillSnapshot` — execution order resolves Section 10. | Full static/native + code review |
| `rstupdate.rstUpdateSeqDefaultSkill` | `rstupdate` | 8-slot-full inline compaction + `rstAddSkill()` + `GBWK+0x7E=1` notification. Operates at "1 skill" granularity; **rejected** as a Repeatable-Skill-Power-Up boundary candidate. | Static disasm |
| `rstupdate.rstUpdateSeqDestroySkill` | `rstupdate` | Forget-skill UI native update. Skill-name display confirmed direct `WorkStock.skill[cursor]` read via `0x182285c40`. | Full static disasm; scoped telemetry |
| `rstupdate.rstUpdateSeqDestroyConfirm` | `rstupdate` | Forget-confirm UI native update. **Critical finding**: Postfix never invoked the completion check prior to the fix, leaving `seq=8/last=21` unchecked. Now fixed with one added call. | Code review; before/after comparison |
| `fclCombineCalcCore.cmbAddSkill` | `fclCombineCalcCore` | Native "commit a new skill" call. Hooked (Prefix) by `OverrideQueuedLearnSkill`, the actual final-commit redirect mechanism. | Code review |
| `rstinit.rstCreateTargetList` | `rstinit` | Builds a per-result-lifecycle candidate list from the **TARGET** slot (not real GBWK). Eligibility check `0x1822840e0` confirmed **no side effects** (pure boolean). | Full static disasm; slot verification |
| `rstinit.rstResult2ProcessStart` / `rstinit.rstDestroyResult2` | `rstinit` | Singleton lifecycle pair for the real-GBWK object: Start generates a new `rstData_t` only if the static slot is null; Destroy explicitly nulls it. No caller found in the 4 analyzed classes. | Full static disasm |

---

## 5. Data Structures and Pointer Provenance

### 5.1 `GBWK` (native type `rstData_t`)

`GBWK` is `rstinit`'s first static field. Confirmed fields (offsets relative to the `rstData_t` object pointer):

| Offset | Field | Notes |
|---|---|---|
| `+0x4a` | overall-attempt counter (byte) | Loop-exhaustion guard (≥16 → give up). |
| `+0x4c` | `PUpSkillIndex` | Slot index under active power-up/mutation consideration. |
| `+0x4e` | `PUpSkillID` | `cmbGetMutationSkill` result cache. |
| `+0x56` | event-type code (byte), from `rstCalcEventInfo` | Investigated and **rejected** as a Design-B wait-state gate (Section 16 item 8). |
| `+0x58` | via **TARGET slot**: pointer array of party stock pointers. Via real-GBWK slot: always `len=0`, a rejected reading (Section 16 item 5). | See Section 5.3–5.4. |
| `+0x60` | `WorkStock` (real-GBWK slot reading) | See Section 5.4. Via TARGET slot in `rstCreateTargetList`: an unrelated int index array. |
| `+0x68` | per-level-up scratch buffer used by `rstInitWorkStock` | Not the WorkStock field (Section 5.5). |
| `+0x7a` | (word) 1-in-3 event-type reservation, from the seq=11 RNG roll | See Section 6. |
| `+0x7c` | on `WorkStock` (unit), not GBWK — per-unit message/SE-selection value | See Section 6. |
| `+0x7E` | 6-element progress-array cursor (byte) | Used across forget-UI and default-skill flows. |
| `+0x80` | 6-element progress array | |
| `+0x88` | UI cursor/action-state object pointer | Consumed by `0x1822ed1c0` (cursor→index) in `rstUpdateSeqDestroySkill`'s display path. |
| `+0x10` sub-struct `SeqInfo`, `+0x11` byte | `SeqInfo.Current`, the `rstCalc` jump-table dispatch value | **CONFIRMED identical** to managed `seq.Current` (direct runtime comparison, all values equal). |

### 5.2 GBWK lifecycle (singleton generation/destruction)

- `rstResult2ProcessStart`: allocates a new `rstData_t` only if the static slot is currently null; sets a state flag to `1`; idempotent otherwise.
- `rstDestroyResult2`: nulls the slot, sets the state flag to `2`.
- `rstCalc`'s own entry logic performs `GBWK = (rstData_t)someHandle.Target` every call, confirmed via disassembly to be a **plain pointer reassignment with no field-copy logic** (Section 16, superseding an earlier theory of per-cycle field re-copying).
- **REJECTED (superseded)**: "GBWK regenerates on every `seq:21→22→8` forget-UI cycle." Real-machine comparison across 3 observed `gbwkPtr` changes showed all key fields (`currentStockPtr`/`workStockPtr`/`TargetIndex`/`TargetCnt`/`PUpSkillIndex`/`PUpSkillID`/`+0x80`/`+0x88`) identical before and after — not explained by a copy-less pointer swap unless the "new" object is effectively the same live context re-resolved. Left as an open tension (Section 20 item 3).

### 5.3 The GBWK/TARGET static-slot confusion (major correction)

**Original understanding**: any instruction sequence matching `mov reg, [rip+disp]` → `[reg+0xb8]` → `[reg2]` was treated as "fetching GBWK," based on one confirmed site.

**New evidence**: RIP-relative loads resolve relative to each instruction's own address; a similar `disp32` in a different function is not guaranteed to resolve to the same absolute address. Absolute-address computation gave:

| Site | Resolved static slot address |
|---|---|
| `rstCalc`'s own GBWK-assignment instruction (`0x18227e7af`) | `0x182e464b8` |
| `rstCalcSeqDevilLevelUp`'s first static load (`0x18227d2d0`) | `0x182e464b8` (**matches**) |
| `rstCreateTargetList`'s static load (`0x1822842b6`) | `0x182e4ed30` (**different — "TARGET"**) |

An exhaustive scan across the 4 classes showed **nearly every major `rst*` function references both slots**, including `rstSetCurrentDevil` and `rstCalcSeqDevilLevelUp` themselves.

**Correction applied**: from this point forward, every pointer/field claim in this archive is labeled with which slot it was verified against; no "same GBWK" claim is accepted without an explicit absolute-address check.

### 5.4 `WorkStock` vs `pCurrentStock` — resolved provenance

Absolute-address tracing of `rstSetCurrentDevil` and the inline duplicate inside `rstCalcSeqDevilLevelUp` produced an identical pattern in both:

```
[real-GBWK slot] → GBWK+0x4a check, loop-cursor management
[TARGET slot]     → candidate demon fetch (party index array, then +0x58 pointer array)
[TARGET slot]     → eligibility checks (0x182281680, 0x1822800b0 — both confirmed real side effects)
[real-GBWK slot]  → GBWK.WorkStock = candidate   ← the only WorkStock write in either function
```

**CONFIRMED**: the candidate demon that ultimately becomes `WorkStock` is fetched from the **TARGET** context, not the real-GBWK context that also holds `pCurrentStock`. This is the structural reason the two pointers can legitimately differ during multi-demon transitions — by design, not corruption.

**Caveat (UNRESOLVED)**: this explains *how* they can differ, but not whether/when native expects `WorkStock`'s mutations to reconcile back onto `pCurrentStock` (Section 20 item 3 relates).

### 5.5 Rejected data-structure hypotheses

- **REJECTED**: `GBWK+0x58` (real-GBWK slot) is the candidate-demon list. Always `len=0`.
- **REJECTED**: `rstInitWorkStock` copies `pCurrentStock` into `WorkStock`. Disassembly showed it reads the *existing* `WorkStock`, generates an unrelated scratch object, and populates `GBWK+0x68` — no `WorkStock` assignment occurs.
- **REJECTED**: `rstSetDestroySkillMaxHpMp`'s `datUnitWork_t.Copy()` call is a "WorkStock becomes scratch copy" mechanism. The copy target is used only for HP/MP delta pre-computation; the value written back to `GBWK.WorkStock` is the same pointer read at function entry (a no-op round-trip).

---

## 6. Mutation Native Pipeline

Confirmed end-to-end control flow for a single Mutation occurrence:

```
rstCalc (per-frame dispatcher)
  -> SeqInfo+0x11 jump table
     -> index==6 -> rstCalcSeqDevilLevelUp
         -> (TARGET slot) next-candidate fetch, eligibility checks
         -> (real-GBWK slot) WorkStock = candidate
  -> rstCalcSkillPowerUpCore
     -> dil roll (0=ordinary Power-Up [bit6-gated], 1=Mutation [not bit6-gated])
     -> dil==1 -> cmbGetMutationSkill(argStockPtr=pCurrentStock, originalSkill)
         -> returns mutatedSkill (0 = no mutation this roll)
  -> rstUpdateSeqSkillPowerUp
     -> rstOverWriteSkill(ref slot, mutatedSkill)   [native: *slot = skill]
     -> MOD: ObserveMutationSequence
         -> detects skill-array change via CandidateInfo.Stock
         -> TryConvertReplacementToAddition(stock, index, original, mutated)
             -> HandledSlots.Contains check (blocks re-conversion, not native write)
             -> stock.skill[index] = originalSkill   (MOD restores original)
             -> 8 slots full? -> Pending.Enqueue(...) : immediate rstAddSkill path
```

**Independently-confirmed 1/3-chance side branch** (post-level-up event, seq 11–14), which does **not** intersect the above pipeline (no calls into `rstCalcSkillPowerUpCore`/`cmbGetMutationSkill`):

```
seq=11: RNG roll (~1/3 chance)
  hit  -> seq=12: GBWK+0x7a event-type reservation set; WorkStock+0x7c message/SE value computed
       -> seq=13: if GBWK+0x7a != 0, jump directly to common post-sequence continuation (skip 14)
  miss -> GBWK+0x7a = 0; return
seq=14: (reached only on miss, or after seq=13 falls through) trivial increment, no meaningful work
```

Native `rstCalcSeqDevilLevelUp`'s epilogue/return (`0x18227f767–778`) is the confirmed boundary at which a full calc-phase pass for the current frame is over — used by the 349-zombie abort mechanism (Section 8.5).

---

## 7. Multi-Demon Failure History

### 7.1 Cross-unit UI display corruption — CLOSED, normal behavior

**Original symptom reports**: skill names, HP values, and level-up messages appearing to belong to the wrong demon while another demon's result screen was active.

**Investigation path**: static confirmation the forget-UI display path is a direct, uncached read, ruling out a UI-side caching layer. Follow-up telemetry initially showed large numbers of `currentUnit != workUnit` mismatches.

**Correction**: once telemetry was scoped to only confirmed callers of the forget-UI/forget-confirm functions, mismatches dropped to **0 out of 12 confirmed-caller observations**. Prior mismatches traced to the telemetry hook being called from many unrelated native paths (Mutation-name announcement, Hearts-skill UI, ordinary level-up display).

**Final Evidence State**: `CONFIRMED` — the forget-UI display path itself has no observed cross-unit corruption. This branch is closed.

### 7.2 "Two Frosts" / WorkStock ownership corruption — found in experimental branch, not shipped v2.1

**Bug found**: `StartExperimentalInline`'s call chain set `GBWK.WorkStock` but never `GBWK.pCurrentStock`, unlike production `StartNext`, which sets both. Directly observed to cause a demon's model/skills to visually merge with another demon's.

**Resolution**: bug in **experimental-only** code, never merged into production v2.1. Preserved as the strongest direct evidence that mishandled `pCurrentStock`/`WorkStock` desynchronization produces exactly the corruption originally feared — validating the caution applied throughout the rest of the investigation.

### 7.3 "349 zombie" repeated-candidate loop — root-caused, mitigated

See Section 8.5. Root cause: `rstChkAddSkill`'s candidate selector is fully stateless, so an unaddable candidate is re-offered indefinitely across separate native calc cycles (confirmed ~7.6s apart), each recurrence potentially rolling a fresh Mutation on a different slot.

### 7.4 HP fluctuation / repeated skill-change animation — traced to the completion-check gap

**Resolution path**: cross-referencing an old log against a new log with the identical failure pattern established that immediately after a forget-confirm resolved to `seq=8/last=21`, the very next same-frame event was `rstCalcSkillPowerUpCore` entering at `seq=9` — native had already begun a fresh Mutation calculation before the MOD checked whether the just-finished transaction could complete.

**Fix**: `rstUpdateSeqDestroyConfirm`'s Postfix now also calls the completion check at the exact `seq=8/last=21` boundary.

**Verification**: post-fix sessions showed `loop-detected-abort-requested`=0 and `Native Context captured→restored` with `match=True` across multiple 2-demon sessions.

### 7.5 Presentation Lifecycle Mismatch (New Finding, 2026-08-30)

**Question**: When the Queue architecture transitions from processing one demon (e.g. Frost) to the next (e.g. High Pixie), do all user-visible presentation components (name, 3D model, status values, skill list, learn/forget result text) update together?

**CONFIRMED**:
- Name, status values, and the skill list are observed to switch over to the next demon at some point during the transition.
- The 3D model is observed, in at least one recorded case, to remain showing the previous demon for a period after the other components have already switched.
- The visual association of the learn/forget result text (i.e., which demon it is describing) is not always visually unambiguous to the viewer during this window.

**Framing note (explicit, per review)**: this finding should **not** be limited to "a 3D model refresh bug." It is recorded here under the broader name **Presentation Lifecycle Mismatch**, covering all of the presentation components listed above, not only the 3D model.

**HYPOTHESIS**: Because the Queue architecture reconstructs the synthetic forget/learn flow from outside native's own per-demon result lifecycle (Section 8.1), the individual presentation components — which in native's own unmodified flow are presumably updated together as part of a single native lifecycle transition — may instead be updated at separate, not-fully-synchronized points when driven through the Queue's capture/drain/restore sequence.

**Evidence-based classification (per review, explicit)**: given the pointer-identity, stock-identity, and `HasSkill`/completion evidence already established elsewhere in this archive (Sections 5.4, 8.3, 8.4, 10, 13 — all consistently showing correct underlying data once execution order is accounted for), this finding is judged, on current evidence, to be better explained by a **presentation/lifecycle timing mismatch** than by an underlying stock-data corruption. This is **not** classified as `CONFIRMED data corruption` — see Section 18's new Evidence Table rows.

**Relationship to Section 7.2** ("Two Frosts"): Section 7.2 remains a separate, closed finding — a specific pointer-ownership bug in *experimental-only* code (`ExperimentalInlineRepro.cs`), never merged into production. This new Section 7.5 finding was observed in the **shipped v2.1 Queue architecture**, not the experimental branch, and its underlying pointer/stock data has not been shown to be incorrect (unlike the confirmed pointer-ownership bug in 7.2). The two are recorded separately and should not be conflated.

**UNRESOLVED**: which exact native lifecycle events are responsible for updating each of name/model/status/skill-list/result-text respectively, and which of those events the Queue's synthetic flow does or does not trigger in the same order/timing as native's own unmodified per-demon transition. See Remaining Questions item 7 (Section 20).

---

## 8. Queue / Ownership Architecture (v2.1)

### 8.1 Overview

```
seq==11 boundary (only statically-verified safe handoff point, gated on TargetIndex==TargetCnt+1)
    -> CaptureNativeContext(): pCurrentStock, WorkStock, TargetPos/Index/Cnt, full SeqInfo
    -> MOD owns (StartNext sets both pCurrentStock AND WorkStock)
    -> Drain (FIFO; fromCalc=true completions defer via _drainContinuationRequested)
    -> RestoreNativeContext(): stock ownership -> Target fields -> SeqInfo fields, then verification
    -> match=True -> native resumes normally
    -> match=False -> fail-closed: _drainSnapshot retained, ERROR logged, no further automatic action
```

### 8.2 `rstCalc` re-entrancy avoidance

A synthetic-item completion observed from `rstChkAddSkill`'s Prefix can occur while still inside a live `rstCalc` call. The fix: such completions set `_drainContinuationRequested = true` and return immediately; `rstUpdate`'s Postfix (guaranteed to run only after that frame's `rstCalc` has fully returned) calls `ContinueOwnedDrainIfRequested()`. This is **not** a new eligibility boundary — it only provides safe execution timing for a drain MOD already owns.

### 8.3 Verification and fail-closed behavior

`RestoreNativeContext` restores stock ownership, then Target fields, then SeqInfo fields, then verifies. On mismatch, restore is refused, `_drainSnapshot` retained, ERROR logged. **CONFIRMED via real-machine testing**: multiple full cycles across 2-demon sessions produced `match=True` in every case after the completion-check-gap fix.

### 8.4 Candidate state (per-`(Stock, Index)` tracking)

```csharp
Dictionary<(IntPtr Stock, int Index), CandidateInfo> Candidates;
```

- `RecordCandidate` creates/overwrites entries keyed by `(stock.Pointer, index)`, state `AwaitingResult`.
- `RecordMutationResult` resolves against `Candidates` by matching `stock.Pointer` among `AwaitingResult` entries: 0 matches → does not bind (warns); 1 match → binds; 2+ → does not bind (fail-closed, warns).

### 8.5 349-zombie loop-detection/abort mechanism

**Detection** (`ObserveNormalCandidateRecurrence`, hooked into `rstChkAddSkill`'s existing Prefix):

```csharp
if (_drainSnapshot == null || Pending.Count == 0) { reset counters; return; }
// NOT reset by _active != null - corrected after review; the real-machine
// zombie pattern showed _active persisting across every recurrence.
if (skillId == _loopDetectSkillId) count++; else { skillId; count = 1; }
if (count >= 5) requestAbort();
```

**Abort** (dedicated path, deliberately not `FailActiveSafely`):

```
requestAbort() -> _loopAbortRequested = true (no state touched yet)
    -> rstCalc's own Postfix (confirmed calc-return boundary, 0x18227f767-778)
       -> MarkDrainAbortReadyAfterCalc(): _loopAbortReadyForRestore = true
    -> rstUpdate's Postfix -> FinalizeDrainAbortIfReady()
       -> Pending.Clear() (once)
       -> if (_active == null) -> proceed to restore normally
          else -> do nothing; the in-flight transaction's own completion
                   path will find Pending empty and proceed on its own
       -> only when _drainSnapshot has actually become null does
          _loopAbortReadyForRestore clear, reopening DrainClosing
```

**`DrainClosing` guard**: while an abort is requested/pending, `TryConvertReplacementToAddition` refuses to start any *new* Learn-As-New conversion; Mutations occurring during this window fall through to plain native overwrite instead.

### 8.6 Native-overwrite suppression (Stage 1 only — not enabled)

`HandledSlots` blocks only MOD's own re-conversion, never native `rstOverWriteSkill`. A separate `OverwriteSuppressed` set was designed to eventually gate `rstOverWriteSkill` via a new Prefix, but deployed only in observation mode (`wouldSuppress=...`, never actually enforced). Across two post-fix sessions, `wouldSuppress=True` was observed **0 times**.

### 8.7 Design A vs. Design B (Per-unit Inline Gate) — status

**Design A** (Global Pending Queue) is the shipped, tested architecture.

**Design B** (per-unit inline gating) was evaluated: a minimal, correctly-scoped experimental gate (Inline Gate V2) fired **0 times** during a session with both a successful and a cancelled transaction, because `rstCalcSeqDevilLevelUp` is never called while a transaction is `_active` — native does not attempt to move to the next demon until the current one's whole result-lifecycle is over.

**Current status**: `UNRESOLVED`/deprioritized (as of the original investigation). The premise motivating the tested Inline Gate V2 experiment (native racing ahead mid-transaction, requiring a gate on `rstCalcSeqDevilLevelUp`) was not observed to occur in tested scenarios. Design B **as tested** remains a UX-improvement research track, not a bug-fix necessity.

### 8.8 Inline Transaction Architecture — New Design Direction (2026-08-30)

**Important distinction (explicit, per review)**: this is a **newly proposed** design direction, and must not be conflated with the Inline Gate V1/V2 experiments described in Section 8.7 and Section 16 items 6–7. The two test different premises:

| | Inline Gate V1/V2 (Section 8.7, prior) | Inline Transaction Architecture (this section, new) |
|---|---|---|
| Premise tested | Native attempts to advance to the next demon *while* a Learn-As-New transaction is still active, requiring a gate on `rstCalcSeqDevilLevelUp` to hold it back | The full Learn-As-New transaction (mutation, original-preservation, full-slot forget UI, player choice, mutated-skill commit or cancel, result display) can be completed *within* the current demon's own native level-up lifecycle, before native's own natural advance to the next demon occurs |
| V2 real-machine result | Gate fired 0 times — `rstCalcSeqDevilLevelUp` is never called while `_active != null` | Not yet tested |
| Status | `REJECTED` as tested (the specific premise did not occur in tested scenarios) — see Section 16 item 7 | `UNRESOLVED` — new proposal, not yet evaluated |

Per explicit instruction, **"Inline Gate V2 rejected" must not be read as "all inline designs rejected."** The V2 finding (native does not race ahead mid-transaction) is, if anything, *encouraging* for this new proposal: if native does not attempt to advance until the current demon's whole lifecycle is over, then completing the Learn-As-New transaction inside that same lifecycle (rather than deferring it via Queue) may be structurally compatible with native's own timing, rather than needing to fight against it.

**Proposed flow** (design-level only; no implementation, no new Hooks, no code changes made as part of this archive update):

```
mutation (native)
  -> original-skill preservation (MOD)
  -> full slot? -> forget UI (native, triggered inline rather than deferred to a queue)
  -> player choice
  -> mutated-skill commit, or cancel
  -> result display
  -> current demon processing complete
  -> (only then) advance to next demon
```

**Motivation**: if achievable, this would eliminate the need for the Queue's capture/drain/restore reconstruction of the synthetic flow (Section 8.1) entirely for the purpose it currently serves. It is a design hope, not a verified outcome, that this **may resolve or substantially reduce** the Presentation Lifecycle Mismatch (Section 7.5) as a side effect, since all presentation components would then be updated by native's own unmodified per-demon lifecycle rather than by a MOD-reconstructed synthetic sequence — this remains untested and is not to be treated as a predicted or confirmed resolution; Inline Transaction Architecture viability itself is `UNRESOLVED` (Section 20 item 8), and Presentation Lifecycle Mismatch's eventual resolution under this architecture is, at most, a `HYPOTHESIS`, not a `CONFIRMED` natural consequence.

**UNRESOLVED** (all, per explicit instruction — no implementation point should be assumed):
- Whether the full-slot mutation forget flow can, in fact, be completed inside the current demon's native level-up lifecycle before native's own advance to the next demon (Remaining Questions item 1, Section 20).
- What exact native sequence boundary marks "current demon result processing is fully complete" for this purpose (Remaining Questions item 2, Section 20) — note this is a related but distinct question from the `seq==11 && TargetIndex==TargetCnt+1` boundary already established for the Queue architecture (Section 8.1); it is not yet established whether that same boundary is the correct one to reuse for an inline design.
- Whether Hidden New Skill Entry (Section 22) is a Queue/synthetic-flow-specific artifact that would not recur under this architecture, or a more fundamental native rendering condition that would persist regardless.

---

## 9. `rstOverWriteSkill` Investigation (Investigation A)

### Question
What storage does `rstOverWriteSkill(ref int slot, ushort skill)` actually write to, and when does that write become visible via `pCurrentStock`/`WorkStock`'s managed skill-array accessors?

### Known Facts (CONFIRMED)
- Native body: `*slot = skill`, RVA `0x2285ff0`.
- C# `rstupdate.rstOverWriteSkill` is a normal IL method (not P/Invoke): `miIL=True`, RVA `0x1b6644`, Fat-format IL header, CodeSize=77, MaxStack=4. The raw IL byte sequence includes bytes (e.g. `0xFE 0x1C`) that the simple decoding approach used here could not fully interpret — this should be described as *a byte sequence not yet decoded by the tooling used*, not as "bytes that don't exist in standard IL" (`0xFE` is a legitimate two-byte-opcode prefix in CIL generally; this wording correction was applied during review).
- `datUnitWork_t.skill` (confirmed via `Il2CppInterop.Runtime.dll` v1.4.5.0 source, Section 11) is `Il2CppStructArray<int>`. Getter/indexer always read/write live native memory; no managed-side cache.
- `TryConvertReplacementToAddition`'s `stock` argument confirmed, across 7 real-machine cases, to always equal `GBWK.pCurrentStock` (`sameAsCurrentStock=True`, `sameAsWorkStock=False` every time).
- `OVERWRITE-VALUE-CHECK` at `rstOverWriteSkill`'s own Postfix showed, in 8/8 cases: `argSlotValueAfter` (mutated value) matched neither `pCurrentStock.skill[index]` nor `WorkStock.skill[index]` — "Pattern C".
- Shortly afterward, at `rstUpdateSeqSkillPowerUp`'s Postfix (via `ObserveMutationSequence`, through the fixed `CandidateInfo.Stock` reference), the mutated value **is** confirmed present, and array-pointer-identity telemetry returned `True`/`True` against the live `GBWK.pCurrentStock` in all 8 sampled cases at that exact moment.
- Immediately after, `TryConvertReplacementToAddition` executes `stock.skill[index] = originalSkill` as an explicit part of its own design — **CONFIRMED by source inspection**. Because the paired-snapshot logger runs *after* this restore in the same Postfix method body, its observation of "still original" is fully explained by execution order.

### Hypotheses (still open)
- **[HYPOTHESIS]** The C# `rstOverWriteSkill` method boundary Harmony patches is an Il2CppInterop-generated managed-to-native bridge, not a direct AOT-native code-entry detour; `ref int __0` may be a bridge-local temporary rather than a direct alias of the native call site's argument storage, with the actual write occurring via a copy-back step of unknown timing.
- Supporting (not conclusive) evidence: `refAddr` (physical address of `ref int __0`, via `fixed`) was **exactly identical** across 7 distinct real-machine cases (`0xBB83F6E630` every time). Ordinary x64 calling convention would not normally place seven logically-distinct arguments at one fixed absolute address.

### Rejected Explanations
- **[REJECTED]** `fixed(ref __0)`-obtained address as a proxy for native argument storage. Rejected because of the constant-`refAddr` finding — this method cannot distinguish "different native storage" from "same fixed bridge temporary," so `sameAddress=False` carries no evidential weight either way.
- **[REJECTED]** "`rstOverWriteSkill` writes to a separate scratch storage, and the value never reaches either `pCurrentStock`/`WorkStock`." Rejected because the very next downstream Postfix *does* observe the mutated value, at a pointer-verified-identical `pCurrentStock`.
- **[REJECTED]** "The Postfix is simply too early, and by `rstUpdateSeqSkillPowerUp`'s Postfix the value is reliably visible everywhere downstream" (simple version). Rejected because the paired-snapshot logger, running at the very same outer Postfix, still shows the original value. The execution-order explanation (restore-before-snapshot) supersedes this.

### Runtime Evidence
- 7x `OVERWRITE-ADDRESS-CHECK`: `refAddr=0xBB83F6E630` every case; `sameAddress=False` every case (method rejected as inconclusive).
- 7x `CONVERT-ENTRY-CHECK`: `sameAsWorkStock=False`, `sameAsCurrentStock=True` every case.
- 8x `OVERWRITE-VALUE-CHECK`: `currentMatchesArg=False`, `workMatchesArg=False` every case (Pattern C).
- 8x `sequence-change` with array-pointer-identity: `sameStockPtr=True`, `sameSkillArrayPtr=True` every case; `after=[...]` correctly reflects mutated value every case.

### Static Evidence
- `rstOverWriteSkill` native RVA `0x2285ff0` (single assignment).
- `rstOverWriteSkill` C# IL header: Fat format, CodeSize=77, MaxStack=4, RVA `0x1b6644`, undecoded byte sequence beginning `18 e0 fe1c 12000001 d9 ...`.
- `Il2CppInterop.Runtime.dll` v1.4.5.0: `datUnitWork_s.skill` getter reads native field offset each call, constructs a fresh `Il2CppStructArray<int>` wrapper around the live native array pointer every time; indexer computes `array.Pointer + 4*IntPtr.Size + index*sizeof(int)` and reads/writes that address directly. No managed-side buffering.

### Current Model
The `ref int __0` value observed in Harmony hooks is very likely exposed via an Il2CppInterop-generated bridge whose relationship to native call-site storage — and, critically, copy-back timing relative to Harmony Postfix — was not determinable via GameAssembly.dll disassembly alone. What **is** solidly established: by the next Harmony hook downstream, the mutated value reliably appears correctly, every time observed (8/8).

### Unresolved
- **[UNRESOLVED]** Exact timing of Il2CppInterop/HarmonyLib `ref`-argument copy-back relative to Harmony Postfix execution.
- **[UNRESOLVED]** Full IL-instruction-level decode of the 77-byte C# method body (would require a proper IL decoder, not attempted here).

### Next Minimal Test
Either (a) inspect HarmonyLib's own native-detour/ref-marshaling implementation, or (b) fully decode `rstOverWriteSkill`'s IL body with a proper decoder (e.g. `dnlib`). Both outside the scope of GameAssembly.dll-only analysis; not attempted.

### Implementation Consequence
`rstOverWriteSkill` write suppression (Stage 2) should **not** be implemented until this is resolved.

---

## 10. `MUTATION-PROBE sequence-change` Investigation (fully resolved)

**`ObserveMutationSequence`'s exact code**:
```csharp
internal static void ObserveMutationSequence(string method)
{
    foreach (var pair in Candidates)
    {
        CandidateInfo item = pair.Value;
        if (item.State != CandidateState.ResultRecorded || item.MutatedSkill == 0 ||
            item.Stock == null || item.Stock.Pointer == IntPtr.Zero) continue;

        string current = DescribeSkills(item.Stock);           // reads via fixed CandidateInfo.Stock reference
        if (string.Equals(current, item.LastObservedSkills, StringComparison.Ordinal)) continue;

        // [sequence-change log emitted here, "after" = current, already mutated]
        item.LastObservedSkills = current;

        SkillMutationLearnAsNew.TryConvertReplacementToAddition(
            item.Stock, item.Index, item.OriginalSkill, item.MutatedSkill);
        // ^ this restores stock.skill[index] = originalSkill as one of its
        //   first actions - confirmed by source, not inferred.

        item.LastObservedSkills = DescribeSkills(item.Stock);  // re-read post-restore
    }
}
```

`MutationPowerUpSequenceTelemetryPatch.Postfix` calls `ObserveMutationSequence` **first**, then `ObservePairedSkillSnapshot` **second**. Because the restore already happened by the time the paired-snapshot logger runs, its observation of "original" is fully explained by execution order — not a different object, a stale cache, or a different write-timing moment.

**Array-pointer-identity check** (added to test the "different stock entity" hypothesis): compares `item.Stock.Pointer`/`item.Stock.skill.Pointer` against `GBWK.pCurrentStock.Pointer`/`.skill.Pointer`, freshly re-fetched, at the instant `sequence-change` fires. Result: `sameStockPtr=True`, `sameSkillArrayPtr=True` in all 8 sampled cases — **definitively ruling out** "different native object."

**REJECTED**: "managed wrapper caching" (briefly considered before the execution-order explanation was found). Fully rejected by Il2CppInterop source analysis (Section 11).

**REJECTED**: "the two telemetry points observed two different `pCurrentStock` entities." Rejected by the same pointer-identity check.

---

## 11. Il2CppInterop Findings

Source: `Il2CppInterop.Runtime.dll` v1.4.5.0, examined specifically to settle the "managed wrapper cache" question from Section 10.

**CONFIRMED**:
- `datUnitWork_t` inherits from `datUnitWork_s`; `skill` is a property on the base class.
- `skill`'s real type is `Il2CppStructArray<int>` — not `short[]`/`ushort[]` (MOD code casts to `ushort` as an interpretation choice, not a reflection of native element width).
- The `skill` getter, on every call: fetches the native object pointer, looks up the field offset, reads the native field to get the array pointer, constructs `new Il2CppStructArray<int>(arrayPointer)`. No cached wrapper is stored anywhere.
- `Length` calls `il2cpp_array_length(Pointer)` fresh every access.
- `this[int]` computes `array.Pointer + (4*IntPtr.Size) + (index*sizeof(T))` and reads/writes that native address directly, with bounds checking but no content cache.
- `.Pointer` is obtained via `il2cpp_gchandle_get_target(myGcHandle)` — the *current* native IL2CPP object pointer, not a fixed managed-wrapper/proxy/trampoline address.
- Multiple `stock.skill` calls produce new wrapper instances each time, but as long as the underlying native field points at the same native array, every instance directly reads/writes the same native memory.

**REJECTED** (all by direct source inspection):
- Cached wrapper instance stored on `datUnitWork_t`.
- `Il2CppStructArray<int>` reading from a managed-side copied buffer.
- `this[int]` caching a previously-read value.
- `.Pointer` referring to a managed wrapper/proxy/trampoline/temporary buffer.
- Any "different managed wrapper instance ⇒ possibly different observed contents" explanation, when the underlying native object/array is confirmed identical.

---

## 12. Learn-As-New Synthetic Flow

Full confirmed pipeline, 8-slots-full case:

```
Mutation detected (sequence-change) -> TryConvertReplacementToAddition
    -> stock.skill[index] = originalSkill  (restore pre-Mutation value)
    -> stock.skillcnt >= 8?
        Yes -> Pending.Enqueue({Stock, Index, OriginalSkill, MutatedSkill})
        No  -> immediate native rstAddSkill/cmbAddSkill path (not queued),
               also passes through OverrideQueuedLearnSkill
    (queued case)
StartNext() [at seq==11 boundary] sets both pCurrentStock and WorkStock
    -> StartPreparedForgetFlow(): synthetic seq transitions replicating a manual forget-skill UI
Player interacts with the (synthetic-triggered, otherwise ordinary) forget-skill UI
Player confirms a skill to discard -> rstUpdateSeqDestroyConfirm resolves -> seq=8/last=21
    -> [FIX] completion check now runs HERE, at this exact boundary
Native proceeds to commit a new skill via fclCombineCalcCore.cmbAddSkill
    -> OverrideQueuedLearnSkill (Prefix hook) redirects the skill native intended
       to add, to _active.MutatedSkill instead ("final-add redirected; from=X to=mutated")
CompleteActive(): HasSkill(item.Stock, item.MutatedSkill)?
    True  -> "queued-full-route completed"
    False -> "queued learn cancelled"
```

### `OverrideQueuedLearnSkill` (core commit mechanism)

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

Hooked on `fclCombineCalcCore.cmbAddSkill`'s Prefix. This is the point at which "Learn As New" is realized: native believes it is adding whatever skill it originally intended, and the MOD substitutes the mutated skill instead.

---

## 13. Cancel / Completed Semantics

### Question
Does `queued learn cancelled` / `queued-full-route completed` reflect native mutation-write success/failure, or something else?

### CONFIRMED
- `HasSkill(stock, skill)` is a simple, timing-independent linear scan of the skill array at the moment called; no dependency on write timing or the Investigation-A concerns (Section 9).
- `_activeCancelled` is set to `!HasSkill(item.Stock, item.MutatedSkill)` at several distinct call sites, informally:
  - the primary completion-check path (newly wired into `rstUpdateSeqDestroyConfirm`'s Postfix per the fix in the timeline);
  - `RecoverQueuedCompletionAtResultExit` (invoked from `rstUpdate`'s Postfix as a catch-all);
  - a cancelled-learn-exit observer, invoked from the per-frame `Sample()` loop;
  - a timeout path (`MarkActiveCancelled("timeout waiting for add confirmation")`), invoked from `Sample()` when the player has waited too long outside the active learn/forget UI.

  *(Note: exact line numbers were not independently re-verified against live source for this archive; names above are drawn from prior investigation notes and should be re-confirmed before being treated as exhaustive.)*
- `CompleteActive()`: if `_activeCancelled`, logs cancelled and returns; otherwise re-checks `HasSkill` and, if true, logs completed.
- `OverrideQueuedLearnSkill` (Section 12) is the mechanism by which player choice during the forget UI translates into whether `HasSkill` will find the mutated skill afterward.

### 5-Round Real-Machine Comparison (2026-08-30)

| Round | Unit | Original->Mutated | Player's final choice | Result |
|---|---|---|---|---|
| 1 | 59 (High Pixie) | 36->385 | Discarded the newly-mutated skill | `queued learn cancelled` |
| 1 | 60 (Frost) | 301->67 | Discarded the newly-mutated skill | `queued learn cancelled` |
| 2 | 59 | 36->28 | Discarded the newly-mutated skill | `queued learn cancelled` |
| 2 | 60 | 1->45 | Discarded the newly-mutated skill | `queued learn cancelled` |
| 3 | 60 | 7->45 | Discarded the newly-mutated skill | `queued learn cancelled` |
| 4 | 60 | 13->387 | Discarded the newly-mutated skill | `queued learn cancelled` |
| 5 | 59 | 36->392 | Discarded the newly-mutated skill | `queued learn cancelled` |
| 5 | 60 | 7->387 | **Kept** the newly-mutated skill | **`queued-full-route completed`** |

Separately noted for this same session: rounds 3 and 4 showed **no** Skill Mutation occurring at all for High Pixie (a distinct failure class — Section 14.3), unrelated to the cancel/completed mechanism.

All 8 outcomes match the `HasSkill`-based model exactly: discard -> cancelled (7/7), keep -> completed (1/1).

### REJECTED
- **[REJECTED]** `queued learn cancelled` == native Mutation write failure. Fully independent of player choice vs. Investigation A's write-timing questions.

### Unresolved
None specific to this sub-investigation; considered closed.

---

## 14. Runtime Case Studies

### 14.1 High Pixie (unit=59, base skill slot 0 = skill 36)

- **Successful Mutation, immediate-add case**: `36->322`; `OVERWRITE-VALUE-CHECK` showed Pattern C, later resolved as uninteresting once the execution-order explanation was established.
- **8-slots-full, cancelled cases**: `36->385`, `36->28`, `36->392` — all cancelled because the player discarded the newly-learned skill.
- **Mutation-not-generated case** (rounds 3-4, Section 14.3): `rstCalcSkillPowerUpCore` returned `result=0`; no candidate generated at all. Distinct failure class from a cancelled queued transaction.
- **Correction**: earlier, "High Pixie sometimes doesn't get a Mutation" was informally associated with `WorkStock`/`pCurrentStock` ownership bugs. Later separated: `CORE-TRACE` showed High Pixie achieving `result=2` with `bit6=True` in the same session type that later showed `result=0` — governed by the native `dil` roll and whatever causes `result=0` outright, not by bit6 or ownership corruption.

### 14.2 Frost (unit=60)

- **Successful completion case** (5-round test, round 5): `7->387`, player kept the mutated skill -> `queued-full-route completed`. Native Context capture/restore showed `match=True`.
- **Post-level-up event partial-completion case** (pre-fix session): synthetic transaction captured native context at `seq=11`; upon restore, native proceeded directly from `seq=8` into `rstCalcSeqDevilLevelUp` and `seq=19`, never revisiting `seq=12-14` — basis for the still-UNRESOLVED "audible SE but display cut short" hypothesis (Section 6, Section 20).
- **349-zombie recurrence case** (pre-fix session): Frost repeatedly re-offered skill 349 across separate native calc cycles (~7.6s / ~1089 frames apart), each recurrence also rolling a fresh Mutation on a different slot, while `_active` remained non-null throughout.

### 14.3 High Pixie "Mutation not generated" issue — separate failure class

**CONFIRMED**: for certain level-up events, `rstCalcSkillPowerUpCore` returns `result=0` for High Pixie specifically, and no Mutation candidate is generated — no `CandidateInfo` entry, no `Pending` entry, no forget UI. `PUpSkillID` may still show a leftover non-zero value from a prior cycle while skill arrays remain unchanged.

This is explicitly **not** the same as "cancelled" (requires a Mutation to have been generated first) nor evidence of a native-write timing problem (requires a Mutation to have occurred). Root cause not statically established in this investigation.

---

## 15. bit6 Findings

**CONFIRMED**:
- `stock+0x10` bit6 (`0x40`) gates only the `dil=0` ("ordinary Skill Power-Up") branch of `rstCalcSkillPowerUpCore`; checked before the `dil` roll's Mutation branch.
- `dil=1` (Mutation) does **not** reference bit6 — confirmed by cases where `bit6=True` and `result=2` occurred together.
- Native sets bit6 on `dil=0` success but clears it in only one gated spot across the 4 analyzed classes (inside `rstUpdateSeqSkillPowerUp`, condition `GBWK+0x1c==1`); once set, persists — including, in one documented case, **across a save/load** (save/load's own internal mechanics not further root-caused).
- `datUnitWork_t.Clear()` (unrelated function) does zero the byte containing bit6, but its relationship to the normal per-level-up flow was not established.

**Correction (superseded)**: bit6 was earlier suspected to relate to "why doesn't High Pixie always get a Mutation" — corrected once the `dil=0`/`dil=1` distinction was confirmed: bit6 governs a *separate* feature entirely.

**Repeatable Skill Power-Up feature**:
- Clears bit6 only at `stockChanged` (GBWK.pCurrentStock differs between `rstCalcSeqDevilLevelUp`'s Prefix and Postfix) **or** `noMoreDemons` (`GBWK.TargetIndex` reaches sentinel `16` with the same stock).
- Operates on the **Prefix-captured** `oldStock` only, never the Postfix-time live value.
- **`unit==0` guard**: added after telemetry showed the feature clearing bit6 on a fixed stock pointer with unit ID `0`, confirmed a native placeholder distinct from real party members. This is a targeted fix from direct observation, not the earlier, deliberately-set-aside speculative "unit==0 exclusion."
- Design limitation ("v1"): clears at most once per completed result-lifecycle per unit, not once per individual level within a multi-level-up event.

---

## 16. Failed / Rejected Approaches

1. **`fixed(ref __0)` physical-address check for `rstOverWriteSkill`**
   - Original: physical address of `ref int __0` is the native call site's actual argument storage; compare to `stock.skill`'s computed element address.
   - Evidence: `refAddr` identical (`0xBB83F6E630`) across 7 distinct cases.
   - Why rejected: ordinary x64 calling convention would not place seven distinct arguments at one fixed address; strongly suggests a bridge/trampoline temporary, meaning this method cannot distinguish "different storage" from "same fixed temporary."
   - Replacement: value-based comparison (`OVERWRITE-VALUE-CHECK`).

2. **"WorkStock is a scratch/temporary copy; Mutation results only ever land there, never on persistent `pCurrentStock`"**
   - Original: based on early observations of differing pointer values.
   - Evidence: `CONVERT-ENTRY-CHECK` (7/7) showed the argument passed into `TryConvertReplacementToAddition` is always `pCurrentStock`, never `WorkStock`. Separately, `rstInitWorkStock` and `rstSetDestroySkillMaxHpMp`'s `Copy()` were individually disassembled and found not to implement this pattern.
   - Why rejected: premise doesn't match either the MOD's own code or the native functions suspected of implementing the copy.
   - Replacement: GBWK/TARGET static-slot split (Section 5.3-5.4) structurally explains the divergence.

3. **"Managed wrapper cache" explanation for the sequence-change discrepancy**
   - Original: `CandidateInfo.Stock` might hold stale cached array contents vs. a freshly-fetched `GBWK.pCurrentStock`.
   - Evidence: full Il2CppInterop source inspection confirmed no caching exists; `sameStockPtr`/`sameSkillArrayPtr` telemetry confirmed identical objects.
   - Why rejected: directly contradicted by both static source analysis and runtime pointer-identity verification.
   - Replacement: execution-order explanation (Section 10).

4. **"Different `pCurrentStock` entity" explanation for the same discrepancy**
   - Original: `item.Stock` and live `GBWK.pCurrentStock` might refer to two different units' stocks.
   - Evidence: `sameStockPtr=True` in all 8 sampled cases.
   - Why rejected: directly falsified.
   - Replacement: same as #3.

5. **`GBWK+0x58` (real-GBWK slot) as the candidate-demon list**
   - Original: based on the not-yet-corrected assumption that any `[static][+0xb8][0]+0x58`-shaped read referred to the same GBWK object seen elsewhere.
   - Evidence: telemetry consistently returned `len=0`.
   - Why rejected: falsified by the always-empty-array observation; explained by the GBWK/TARGET correction — the actual fetch logic reads `+0x58` via the TARGET slot.
   - Replacement: Section 5.4.

6. **Inline Gate V1 experiment (`ExperimentalInlineRepro.cs`)**
   - Original: reuse native's own per-unit bit6 gate as a signal to hold native's next-unit progression while a transaction completes inline.
   - Evidence: uncovered the "two Frosts" desync bug (Section 7.2), a bug in the experiment's own supporting code.
   - Why rejected (as an approach): surfaced a real ownership hazard needing fixing regardless of architecture; did not establish bit6-based gating as viable on its own.
   - Replacement: the finding directly informed the discipline applied throughout the rest of the investigation (always set both pointers together).

7. **Inline Gate V2 experiment**
   - Original: gate `rstCalcSeqDevilLevelUp`'s next-unit logic only while a matching transaction is `_active`, avoiding the broader capture/restore machinery.
   - Evidence: gate fired 0 times in a session with both successful and cancelled transactions.
   - Why rejected (as a design premise): `rstCalcSeqDevilLevelUp` is never called while `_active != null` — the scenario Design B meant to intercept does not occur in tested scenarios.
   - Replacement: Design B remains an open UX track (Section 8.7), not a bug-fix necessity.

8. **`GBWK+0x56` as a "wait state" / next-unit-transition gate**
   - Original: considered as a possible gate for Design B.
   - Evidence: the `+0x56==0` branch merges into the same continuation as the non-zero branch after clearing an unrelated cursor field.
   - Why rejected: does not function as a wait/stall signal; both branches converge and neither defers `rstCalcSeqDevilLevelUp`'s eventual invocation.
   - Replacement: none needed; the jump-table mechanism (Section 6) is the actual gate.

---

## 17. Corrections and Superseded Conclusions

| # | Original Understanding | New Evidence | Correction | Final Evidence State |
|---|---|---|---|---|
| 1 | "349 zombie" is a single-native-call internal loop inside `seq=8` | Real-machine timestamps ~7.6s / ~1089 frames apart | Recurrence spans **separate native calc cycles** | CONFIRMED (Section 7.3) |
| 2 | Loop-detection should reset its counter whenever `_active != null` | Real-machine zombie pattern showed `_active` persisting across every recurrence | Detection continues regardless of `_active`; only abort *execution* defers | CONFIRMED (Section 8.5) |
| 3 | "GBWK regenerates every forget-UI cycle, with fields re-copied" | `rstCalc`'s GBWK-assignment logic disassembled: plain pointer reassignment, no copy code exists | Left as an open tension, not fully resolved | Downgraded to open tension (Section 20 item 3) |
| 4 | Any `[static][+0xb8][0]`-shaped chain refers to the same GBWK object | Absolute-address resolution proved two distinct slots exist | All prior claims re-verified against absolute addresses | CONFIRMED as a methodological rule going forward (Section 5.3) |
| 5 | `GBWK+0x58` (real-GBWK slot) is the candidate-demon list | Telemetry: always `len=0` | Candidate fetch actually via the TARGET slot | REJECTED -> CONFIRMED replacement (Section 5.4, 16.5) |
| 6 | `WorkStock` is a scratch copy of `pCurrentStock` | `rstInitWorkStock`/`rstSetDestroySkillMaxHpMp` disassembled, found not to implement this | Divergence explained via TARGET-slot candidate fetch instead | REJECTED -> CONFIRMED replacement (Section 5.5, 16.2) |
| 7 | `rstOverWriteSkill` Postfix "unreflected" values mean a separate, never-synced storage | Next downstream Postfix reliably shows the mutated value at a pointer-verified `pCurrentStock` | Timing/execution-order explanation, not separate-storage | REJECTED (separate storage) / UNRESOLVED (exact timing) — Section 9 |
| 8 | sequence-change/paired-snapshot discrepancy = different entity or stale cache | Pointer-identity telemetry True/True; Il2CppInterop confirmed no cache | Fully explained by execution order | REJECTED (both) -> CONFIRMED replacement (Section 10) |
| 9 | High Pixie's inconsistent Mutation relates to bit6/ownership corruption | dil=0/dil=1 distinction confirmed; separate result=0-no-candidate class identified | Two separate, unrelated phenomena | CONFIRMED (bit6 distinction) / UNRESOLVED (result=0 root cause) — Section 14.3, 15 |
| 10 | `queued learn cancelled` = native write failure | HasSkill-based model confirmed via 5-round A/B test (8/8) | Reflects player choice, independent of Investigation A | REJECTED -> CONFIRMED replacement (Section 13) |
| 11 | Design A and Design B are prerequisites of each other | Both investigated in parallel; Design A's fix was independent | Architecturally independent | N/A (process note) |

---

## 18. Evidence Table

| Topic | State | Evidence | Supersedes |
|---|---|---|---|
| `TryConvertReplacementToAddition`'s `stock` identity | CONFIRMED | `CONVERT-ENTRY-CHECK`, 7/7 `sameAsCurrentStock=True` | "WorkStock scratch" hypothesis |
| `WorkStock`/`pCurrentStock` structural divergence mechanism | CONFIRMED | Absolute-address trace of `rstSetCurrentDevil`/`rstCalcSeqDevilLevelUp` | "scratch copy" hypothesis |
| GBWK vs. TARGET are distinct static slots | CONFIRMED | Absolute-address resolution: `0x182e464b8` vs `0x182e4ed30` | "same GBWK by pattern" assumption |
| `GBWK+0x58` (real-GBWK slot) is the candidate list | REJECTED | Runtime: always `len=0` | - |
| `fixed(ref)` physical-address method for `rstOverWriteSkill` | REJECTED | Constant `refAddr` across 7 cases | - |
| Managed wrapper cache exists for `skill` array | REJECTED | Il2CppInterop 1.4.5.0 source inspection | "stale wrapper" hypothesis |
| sequence-change vs. paired-snapshot discrepancy | CONFIRMED (execution order) | Code review + pointer-identity=True/True, 8/8 | "different entity"/"wrapper cache" hypotheses |
| `rstOverWriteSkill` native ref copy-back exact timing | UNRESOLVED | Pattern C observed 8/8; mechanism not determined | - |
| `queued learn cancelled`/`completed` semantics | CONFIRMED | 5-round real-machine A/B test, 8/8 matching | "native write failure" hypothesis |
| `rstSetCurrentDevil` real-machine call frequency | CONFIRMED (0 observed) | Full 2-demon session | - |
| Design B viability (Inline Gate V2 premise) | REJECTED (as tested) | Gate fired 0/session | Design B "hold native back" premise |
| Native Context Snapshot/Restore correctness (post-fix) | CONFIRMED | Multiple sessions, `match=True` every time | Pre-fix sessions with stuck restores |
| 349-zombie recurrence spans separate native calc cycles | CONFIRMED | ~7.6s / ~1089 frames apart | "single-call internal loop" hypothesis |
| bit6 gates only dil=0, not Mutation (dil=1) | CONFIRMED | `CORE-TRACE`: bit6=True co-occurring with result=2 | Earlier informal association |
| Post-level-up event (seq 11-14) is not a re-Mutation | CONFIRMED | Static: no calls into Mutation functions | - |
| Post-level-up event consumer / display-cutoff mechanism | UNRESOLVED | Consumer of GBWK+0x7a never located | - |
| bit6 persists across save/load | CONFIRMED (observed once) | One documented real-machine case | - |
| Queue functional correctness | CONFIRMED | Sections 8.1-8.6, 12, 13 | - |
| Queue preserves native-quality UX/presentation | REJECTED | Section 7.5, 2026-08-30 video review | Earlier implicit assumption that internal correctness implied presentation correctness |
| Latest visual issue (2026-08-30) is confirmed data corruption | REJECTED | Current pointer/stock/`HasSkill`/completion evidence does not support data corruption; Sections 5.4, 8.3, 8.4, 10, 13 all consistent; Section 7.5 Presentation Lifecycle Mismatch | - |
| Presentation Lifecycle Mismatch exists | CONFIRMED | Section 7.5, 2026-08-30 video review | Earlier narrower "3D model refresh" framing (not itself rejected, but broadened) |
| Frost hidden new-skill entry logically exists | CONFIRMED | Section 22, 2026-08-30 video review | - |
| Frost hidden new-skill entry rendering-suppression cause | HYPOTHESIS | Section 22 | - |
| Inline Transaction Architecture viability | UNRESOLVED | Section 8.8, new proposal not yet tested | Not a supersession of the Inline Gate V2 rejection (Section 16 item 7), which tested a different premise |
| Native Replacement Suppression implementation point | UNRESOLVED | Section 23; gated on Investigation A (Section 9) | - |

---

## 19. Current Architecture

- **Feature gate**: `SkillMutationAlways` forces `dil` toward Mutation-eligible outcomes; `SkillMutationLearnAsNew` implements preserve-original-skill behavior.
- **Candidate tracking**: `Dictionary<(IntPtr Stock, int Index), CandidateInfo>`, resolved via explicit 0/1/2+-match logic, never guessed.
- **Immediate-add path**: `TryConvertReplacementToAddition` restores original, commits via native add, `OverrideQueuedLearnSkill` ensures the mutated skill is added.
- **Queued path**: `Pending.Enqueue`; drained under Native Context Snapshot/Restore, gated exclusively on `seq==11 && TargetIndex==TargetCnt+1`; synthetic forget UI via `StartNext`/`StartPreparedForgetFlow`; completion via `HasSkill`; `rstUpdateSeqDestroyConfirm`'s Postfix now checks completion at the exact boundary (the major fix).
- **349-zombie safety net**: recurrence-counting independent of `_active`; abort deferred to confirmed `rstCalc` return boundary, finalized from `rstUpdate`'s Postfix; `DrainClosing` prevents new conversions mid-abort.
- **`rstOverWriteSkill` suppression**: Stage 1 (observation only) deployed; Stage 2 (enforcement) not implemented pending Investigation A resolution.
- **Repeatable Skill Power-Up**: independent feature, bit6-clear at `stockChanged || noMoreDemons`, `unit==0`-guarded, "v1" (per-lifecycle, not per-level).
- **Design B**: not implemented; deprioritized; open UX-improvement track, architecturally independent of the above.

### 19.1 Update (2026-08-30)

The architecture described above (Sections 8.1–8.6) remains an accurate description of the **shipped, internally-correct** v2.1 Queue implementation — nothing in this update changes any of those mechanics. The design judgment has changed, however: Queue is now classified as a **validated functional prototype** — it correctly proves the underlying mutation/preservation/completion logic works — rather than as the intended final presentation-quality implementation, due to the Presentation Lifecycle Mismatch finding (Section 7.5). The prioritized direction for the final implementation is now the Inline Transaction Architecture (Section 8.8), with Native Replacement Suppression (Section 23) as a core dependent design problem. Direct, piecemeal presentation-level patches to the current Queue implementation are **not** the currently recommended path (Section 21).

---

## 20. Remaining UNRESOLVED Questions

1. Exact native<->managed `ref`-argument copy-back timing for `rstOverWriteSkill`, relative to Harmony Postfix (Section 9).
2. Consumer/display mechanism for the post-level-up event reservation (`GBWK+0x7a`), and whether it is genuinely lost across a GBWK "regeneration" (itself unresolved — item 3) during a synthetic forget flow (Section 6, 14.2).
3. The precise relationship between `rstCalc`'s copy-less GBWK pointer reassignment and the observation that "regenerated" instances carried forward identical field values — whether "regeneration" is even the right frame for what is happening.
4. Root cause of `rstCalcSkillPowerUpCore` returning `result=0` with no candidate generated for a given level-up event (Section 14.3).
5. Whether `rstSetCurrentDevil` (0 real-machine calls, no static caller found in the 4 analyzed classes) is called via a mechanism outside those classes, is effectively dead code, or unreached under tested conditions.
6. Full exhaustive, source-line-verified classification of every `_activeCancelled`/timeout code path (Section 13 gives a partial, name-based enumeration).
7. Whether Stage-1 `OverwriteSuppressed` should ever be promoted to Stage 2 enforcement, given `wouldSuppress=True` has not recurred since the completion-check-gap fix — unclear whether the hazard is closed or merely rarer.

### 20.1 Additional questions (2026-08-30)

8. Can the full-slot mutation forget flow be completed inside the current demon's native level-up lifecycle before the game advances to the next demon? (Section 8.8)
9. What exact native sequence boundary marks "current demon result processing is fully complete," for the purpose of an inline design — and is it the same boundary already established for the Queue architecture (`seq==11 && TargetIndex==TargetCnt+1`, Section 8.1), or a different one? (Section 8.8)
10. Can native mutation candidate generation/presentation be preserved while suppressing only the replacement write? (Section 23)
11. Does Hidden New Skill Entry still occur after the Inline Transaction Architecture is adopted, or is it only a Queue/synthetic-flow artifact? (Section 22)
12. What causes High Pixie's `rstCalcSkillPowerUpCore` `result=0` in the no-candidate cases? (this restates, and keeps independent, the existing item 4 above / Section 14.3 — not a new phenomenon, listed here per explicit instruction to include it in this update's question set)
13. Investigation A: exact `rstOverWriteSkill` `ref` copy-back timing (restates item 1 above; kept for cross-reference with the new Section 23 dependency)
14. Presentation component synchronization: which native lifecycle events update name / model / status / skill list / result text respectively, and in what order/timing relative to each other? (Section 7.5)

---

## 21. Recommended Future Research

1. For Investigation A: (a) review HarmonyLib's own native-detour/ref-marshaling implementation, or (b) fully decode `rstOverWriteSkill`'s 77-byte IL body with a proper IL decoder.
2. For the post-level-up event consumer: search for readers of the structure `0x1962f8110`/`0x1962f8460` (the seq=12 enqueue functions) write into, rather than continuing to guess from the write side.
3. Design B: revisit only as a UX-improvement track if "process one demon fully before the next" pacing is still desired, with the understanding that the originally-assumed problem (native racing ahead mid-transaction) was not observed.
4. The `result=0`/no-candidate High Pixie failure class has not been static-root-caused; would need a fresh, independent investigation thread.
5. Before promoting `OverwriteSuppressed` to enforcement, gather more real-machine sessions specifically targeting the original `wouldSuppress=True` scenario under the post-fix codebase.

### 21.1 Updated priority ordering (2026-08-30)

The five items above remain valid research directions and are not withdrawn. Per the 2026-08-30 design judgment (Sections 2.1, 8.8, 19.1), the following priority ordering supersedes the informal ordering implied by the list above for the purpose of deciding what to work on next:

1. **Priority 1**: Inline Transaction Architecture feasibility (Section 8.8).
2. **Priority 2**: Current-demon lifecycle completion boundary and next-demon advance boundary (Section 20 item 9).
3. **Priority 3**: Native Replacement Suppression (Section 23).
4. **Priority 4**: Multi-demon inline real-machine validation (once a feasible inline design exists).
5. **Priority 5**: Hidden New Skill Entry (Section 22), if still present after Inline Transaction Architecture is adopted.
6. **Priority 6**: High Pixie mutation-not-generated root cause (Section 14.3) — corresponds to original item 4 above.
7. **Priority 7**: `rstOverWriteSkill` bridge/ref copy-back deep analysis (Investigation A, Section 9) — corresponds to original item 1 above — pursued only if/when needed for Native Replacement Suppression implementation.

**Explicit de-prioritization**: piecemeal presentation-level repairs to the current Queue implementation (e.g., patching the 3D-model refresh timing directly) are, as of this update, **low priority or not recommended**, since the current design judgment is to move toward Inline Transaction Architecture rather than to continue incrementally patching Queue's presentation layer.

---

## 22. Hidden New Skill Entry (New Finding, 2026-08-30)

**Also known as**: No-Native-Learn Mutation Visibility.

**Question**: Does the forget UI's presentation of a Learn-As-New mutated-skill entry differ depending on whether the demon still has a remaining native-learnable skill at that level, versus not?

**Comparison setup**:
- **High Pixie-type case**: the demon still has at least one skill remaining that native would normally learn via its own default level-up flow.
- **Frost-type case**: the demon has no remaining native-learnable skill; the only new skill in play is the Learn-As-New mutated skill.

**CONFIRMED** (Frost-type case):
- The mutated skill's new entry logically exists: forget-UI cursor navigation can reach its position, skill detail information is displayed for it, and the confirm action (discard the mutated skill itself) is reachable from it. That is, the logical entry, input handling, detail display, and confirmation path all exist and function.
- However, the new skill entry's **visual rendering at that position is not visible** — the normal highlight frame/box that would ordinarily mark a selectable entry does not appear for this entry, even though the entry is functionally selectable and interactable.

**HYPOTHESIS**: when the demon has no remaining native-learnable skill, some condition governing the new-learned-skill entry's **visual rendering only** is suppressed — i.e., the entry is not rendered, though it is not absent in the logical/functional sense. This should be described as *"entry exists but is not rendered"*, not as *"entry does not exist."*

**Candidates for further investigation** (not yet analyzed as part of this update): display count, an entry visible/active flag, normal-learnable-skill availability as a rendering precondition, `DefSkillResult`, `SelectSkillID`, `PUpSkillID`, cursor index vs. display index, and the native skill-entry rendering condition generally.

**Explicit scope note (per review)**: this finding may resolve naturally if the Inline Transaction Architecture (Section 8.8) is adopted, since the rendering condition may be tied to native's own per-demon lifecycle state in a way that the Queue's synthetic reconstruction does not fully replicate. **No direct UI-level fix to the current Queue implementation should be assumed as the appropriate response** until this is better understood; this is recorded as an independent, standalone finding rather than a mandate to patch the current version's UI.

**UNRESOLVED**: the exact native condition governing new-skill-entry visual rendering, and whether this finding persists under the Inline Transaction Architecture direction (Remaining Questions item 4, Section 20).

---

## 23. Native Replacement Suppression (New Core Design Problem, 2026-08-30)

**Current confirmed pipeline** (restated from Section 6/Section 12): native performs the `original -> mutated` replacement first (`rstOverWriteSkill`); the MOD then performs `mutated -> original` (rollback, via `TryConvertReplacementToAddition`), followed by re-adding the mutated skill as a new entry (Learn-As-New).

```
Native replacement (original -> mutated)
  -> MOD rollback (mutated -> original)
  -> Learn-As-New (mutated re-added as new entry)
```

**Proposed direction for the Inline Transaction Architecture** (Section 8.8): rather than letting native perform the replacement write and then having the MOD roll it back, the goal would be to preserve native's own mutation candidate generation and presentation/animation (since these appear to work correctly and are not implicated in any confirmed bug in this archive), while suppressing **only** the actual replacement write itself, and reusing the MOD's own add flow (or native's own add flow) to commit the mutated skill as a new entry directly — without ever performing the write-then-rollback round trip.

```
mutation decision       = native (unchanged)
mutation candidate      = native (unchanged)
mutation presentation   = native (unchanged)
actual replacement write = suppressed (new)
mutated skill add        = MOD / native add-flow reuse (unchanged mechanism, new trigger point)
```

**Explicit relationship to Investigation A (Section 9) — must not be conflated with a solved problem**: this proposed direction depends on being able to safely suppress `rstOverWriteSkill`'s actual write, at a point that reliably prevents the write from having any effect. Investigation A (Section 9) already established that the exact native↔managed `ref`-argument copy-back timing for `rstOverWriteSkill` is `UNRESOLVED`. **This archive update does not resolve that question and does not assert that a Harmony Prefix on `rstOverWriteSkill` returning early is a correct or sufficient suppression point.** The relevant Section 8.6 "Stage 1 observation only" caution (never enable Stage 2 enforcement until write timing is understood) applies equally, and more urgently, to this new proposed direction.

**UNRESOLVED** (explicit, per review — no implementation point assumed):
- Whether native mutation candidate generation/presentation can, in practice, be preserved independently of suppressing the replacement write (i.e., whether these are separable in native's own control flow, or whether the write is itself a precondition for some later presentation step).
- The correct suppression point, if suppression is pursued at all — explicitly **not** assumed to be `rstOverWriteSkill`'s Prefix, pending Investigation A.

---

## Appendix A. RVA Table

| Symbol | RVA (native, GameAssembly.dll unless noted) | Notes |
|---|---|---|
| `rstOverWriteSkill` (native body) | `0x2285ff0` | `*slot = skill` |
| `rstOverWriteSkill` (C# IL method, Assembly-CSharp.dll) | `0x1b6644` | Fat format, CodeSize=77, MaxStack=4 |
| `rstCalc`'s own GBWK-assignment instruction | `0x18227e7af` | Resolves to real-GBWK slot |
| `rstCalcSeqDevilLevelUp`'s first static load | `0x18227d2d0` | Resolves to real-GBWK slot |
| `rstCreateTargetList`'s static load | `0x1822842b6` | Resolves to TARGET slot |
| Real-GBWK static slot (absolute, this build) | `0x182e464b8` | Confirmed via >=2 independent call sites |
| TARGET static slot (absolute, this build) | `0x182e4ed30` | Confirmed via >=2 independent call sites |
| `rstCalc`'s confirmed return/epilogue boundary | `0x18227f767`-`0x18227f778` | Used as the 349-zombie abort's Postfix hook point |
| `0x182281680` | eligibility/experience-related helper | Referenced via TARGET slot; confirmed real side effects |
| `0x1822800b0` | second eligibility helper | Same call sites |
| `0x1822840e0` | `rstCreateTargetList`'s eligibility check | Confirmed **no side effects** |
| `0x196542270` | underlying `rstChkAddSkill` candidate selector | Confirmed fully stateless |
| `0x182285c40` | `rstUpdateSeqDestroySkill`'s skill-name display fetch | Confirmed direct `WorkStock.skill[cursor]` read, no cache |
| `0x182284b50` | `rstResult2ProcessStart`'s `rstData_t` allocator | |

---

## Appendix B. Pointer / Field Map

| Name | Object | Offset | Nature | Provenance |
|---|---|---|---|---|
| `pCurrentStock` | real-GBWK object | not independently re-derived by exact byte offset | `datUnitWork_t*` | Managed property `rstinit.GBWK.pCurrentStock` |
| `WorkStock` | real-GBWK object | `+0x60` | `datUnitWork_t*` | Confirmed final-assignment target in `rstSetCurrentDevil` and `rstCalcSeqDevilLevelUp` |
| `GBWK+0x4a` | real-GBWK object | `+0x4a` | byte, loop-exhaustion counter | Static disasm |
| `GBWK+0x56` | real-GBWK object | `+0x56` | byte, event-type code (rejected as a gate) | Static disasm |
| `GBWK+0x58` (real-GBWK slot) | real-GBWK object | `+0x58` | always `len=0` (rejected hypothesis) | Runtime telemetry |
| `GBWK+0x58` (TARGET slot, `rstCreateTargetList`) | TARGET object | `+0x58` | pointer array of actual party stock pointers | Static disasm |
| `GBWK+0x60` (TARGET slot, `rstCreateTargetList`) | TARGET object | `+0x60` | int array, party-order index array | Static disasm |
| `GBWK+0x68` | real-GBWK object | `+0x68` | scratch level-count buffer (`rstInitWorkStock`) | Static disasm |
| `GBWK+0x7a` | real-GBWK object | `+0x7a` | word, post-level-up event-type reservation | Static disasm + telemetry |
| `WorkStock+0x7c` | unit object (not GBWK) | `+0x7c` | per-unit message/SE-selection value | Static disasm + telemetry |
| `stock+0x10` bit6 (`0x40`) | any `datUnitWork_t` | `+0x10`, bit 6 | ordinary Skill Power-Up gate (not Mutation) | Static disasm + `CORE-TRACE` |
| `SeqInfo+0x11` | real-GBWK's `SeqInfo` sub-object (`+0x10`) | `+0x11` | jump-table dispatch, confirmed identical to managed `seq.Current` | Static disasm + telemetry |
| `stock.skill` | any `datUnitWork_t` | native field offset not independently re-derived | `Il2CppStructArray<int>` | Il2CppInterop 1.4.5.0 source |

---

## Appendix C. Important Log Cases

- Multiple `Latest.log` sessions across the investigation; the logging tool overwrites the file on each run, so filenames/timestamps for older sessions were not consistently preserved (noted explicitly by the user mid-investigation for at least one older session).
- 5-round real-machine A/B comparison session (2026-08-30): primary evidence for Section 13, reproduced in full in that section's table.
- Sessions establishing `OVERWRITE-VALUE-CHECK` Pattern C (High Pixie 36->322, Frost 301->204) and the follow-up 8-case session establishing pointer-identity=True/True.
- Session establishing `INLINE-GATE-V2` = 0 calls across a full successful+cancelled 2-demon test.
- Session establishing `DEVIL-TRANSITION-TRACE` for `rstSetCurrentDevil` = 0 calls across a full 2-demon test.
- Old-vs-new log comparison establishing the completion-check-gap bug (identical `seq=8/last=21` -> same-frame `seq=9` re-entry pattern, mutated skill `210` in the older session, `409` in the newer).
- Post-fix verification sessions: `match=True` on Native Context restore, `loop-detected-abort-requested`=0, `OVERWRITE-GATE wouldSuppress=True`=0, `BIT6-CLEAR` on `unit=0`=0.

---

## Appendix D. Important Code Paths

- `ObserveMutationSequence` - Section 10
- `DescribeSkills` / `DescribeSkillsSafe` - Section 10 (differ only in loop-bound source, `stock.skillcnt` vs. a fixed 8; not the explanation for the sequence-change discrepancy)
- `TryConvertReplacementToAddition` - Sections 8.4, 9, 10, 12
- `OverrideQueuedLearnSkill` - Section 12
- `HasSkill` - Section 13
- `CompleteActive` - Section 13
- completion-check function (informally `TryCompletePendingQueuedReturn`) - Sections 7.4, 13
- `RecoverQueuedCompletionAtResultExit` - Section 13
- `StartNext` / `StartPreparedForgetFlow` - Sections 8.1, 12
- `CaptureNativeContext` / `RestoreNativeContext` - Sections 8.1, 8.3
- `ContinueOwnedDrainIfRequested` / `OnSyntheticItemFinished` - Section 8.2
- `ObserveNormalCandidateRecurrence` / `MarkDrainAbortReadyAfterCalc` / `FinalizeDrainAbortIfReady` - Section 8.5
- `DrainClosing` - Section 8.5
- `RepeatableSkillPowerUp.CapturePrefix` / `ApplyPostfix` - Section 15
- `rstinit.rstCreateTargetList` - Section 5.3, Appendix B

---

## Final Notes on This Archive

This document does not delete or silently replace any earlier conclusion; Section 17 makes corrections traceable, Section 16 preserves rejected approaches with their reasoning intact. Where a specific claim from prior notes could not be independently re-verified while compiling this archive (e.g. exact source line numbers for `_activeCancelled` call sites, Section 13), this is explicitly flagged rather than silently asserted as fact.

**2026-08-30 update note**: this archive was updated with new findings from a 2026-08-30 real-machine video review (new Sections 2.1, 7.5, 8.8, 19.1, 20.1, 21.1, 22, 23; new Evidence Table rows in Section 18; new Timeline item 25 in Section 3). No prior content was deleted. The update reclassifies the Queue architecture's design status (validated functional prototype rather than final presentation-quality implementation) without altering any of its previously-CONFIRMED internal-correctness findings, and introduces a new proposed direction (Inline Transaction Architecture, Section 8.8) that is explicitly distinguished from, and does not supersede, the earlier rejected-as-tested Inline Gate V1/V2 experiments (Section 8.7). No MOD code changes, telemetry, hooks, commits, or pushes were made as part of this update.
