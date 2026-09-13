# NocturneModernGameplay Skill Mutation — V3 Design Specification

**Purpose**: This document consolidates every CONFIRMED native fact accumulated across the Queue (v2.1) and Inline (Phase 2 series) investigations, as a reusable specification for a ground-up V3 reimplementation. V3 carries forward *knowledge*, not *code*, from the legacy implementation. The legacy implementation (`SkillMutationLearnAsNew.cs` and friends) is preserved unchanged as reference/parts-source, not deleted.

**Guiding principle**: V3 must not import Queue-era assumptions. Every mechanism below exists because the *native game* behaves this way — not because a prior architecture needed it. Anything in the legacy code whose purpose was "compensate for Queue's own historical-replay design" (context capture/restore, `RestoreSkillProgress`, `RestoreResultTarget`, `pCurrentStock`/`WorkStock` rebinding to a *past* unit) has no place in V3 by default and must be re-justified from scratch if ever reintroduced.

---

## 1. Confirmed Native Pointer/Field Map

| Symbol | Location | Evidence |
|---|---|---|
| `pCurrentStock` | `real-GBWK + 0x60` | CONFIRMED via runtime pointer-aligned scan (Phase 1I), 83/83 observations |
| `WorkStock` | `real-GBWK + 0x68` | CONFIRMED, same scan |
| `SeqInfo.Current` | `SeqInfo object + 0x11` (`SeqInfo` itself at `GBWK + 0x10`) | CONFIRMED via intersection-narrowing runtime scan (Phase 2E), single candidate after 40+ observations |
| `SeqInfo.Last` | `SeqInfo + 0x13` | CONFIRMED, same scan |
| `SeqInfo.Next` | `SeqInfo + 0x12` | CONFIRMED, same scan |
| `SeqInfo.Change` | `SeqInfo + 0x14` | CONFIRMED, same scan |
| `SeqInfo.Flag` | `SeqInfo + 0x10` (within the SeqInfo object) | CONFIRMED, same scan |
| `DefSkillResult` | `real-GBWK + 0x3C` | CONFIRMED via intersection-narrowing runtime scan (byte/int16/int32 all converge on `0x3C`), 12/12 observations |
| Skill-candidate seed | `real-GBWK + 0x32` (word) | CONFIRMED to directly equal the skill ID subsequently passed to `rstChkAddSkill` (8/8 observations); NOT the same field as the pre-existing, differently-based `WorkStock+0x32` telemetry, which reads from a different object entirely |
| `real-GBWK + 0x34` (word) | Adjacent to the above; meaning still `UNRESOLVED` | One confirmed native write site exists (`rstCalc`, `0x18227e928`), context not fully resolved |

**Methodological note preserved from the investigation**: two fields at the *same numeric offset* (`+0x32`, `+0x58`, `+0x60`) can refer to completely different native objects depending on which static slot (`real-GBWK` vs. `TARGET` vs. `ACTION`, all distinct, absolute-address-verified slots) the base pointer resolves to. V3's own telemetry and documentation must always state which base object a given offset was read from, never just "the offset."

## 2. Confirmed Static Slots

| Slot name (informal) | Absolute address (this build) | Role |
|---|---|---|
| Real GBWK | `0x182e464b8` | The actual `rstinit.GBWK` singleton; holds `pCurrentStock`, `WorkStock`, `SeqInfo`, `DefSkillResult`, the skill-candidate seed, etc. |
| TARGET | `0x182e4ed30` | A separate static context, confirmed referenced by `rstSetCurrentDevil`/`rstCalcSeqDevilLevelUp` for next-candidate fetch; its full type/purpose remains largely `UNRESOLVED` |
| ACTION | `0x182e46930` | A third static context; `rstInitSkillAct` writes the action-type argument (e.g. `8`) into this object's `+0x28`; consumers not fully identified |

Absolute addresses are build-specific; V3 telemetry/tooling must resolve these via `moduleBase + RVA`, never hardcode.

## 3. Confirmed Mutation Pipeline (native, unmodified)

```
rstCalc (seq dispatch)
  -> seq=6: rstCalcSeqDevilLevelUp (next-candidate fetch from TARGET, WorkStock = candidate)
  -> seq=9: rstCalcSkillPowerUpCore
       -> dil=0: ordinary Skill Power-Up (gated by stock+0x10 bit6)
       -> dil=1: cmbGetMutationSkill(argStockPtr=pCurrentStock, originalSkill) -> mutatedSkill
  -> seq=10: rstUpdateSeqSkillPowerUp -> rstOverWriteSkill(ref slot, mutatedSkill)  [native: *slot = skill]
```

`cmbGetMutationSkill`'s stock argument is confirmed to be the actual `pCurrentStock`, not `WorkStock`.

## 4. Confirmed Full-Slot ("Forget Required") Native Flow

```
rstChkAddSkill(candidateSkill) -> sbyte result
    result == 2  =>  8 slots already full, forget UI required
seq=8 handler (rstCalc):
    DefSkillResult == 0  -> SeqInfo.Current++ (ordinary advance)
    DefSkillResult != 0  -> native itself calls rstInitSkillAct(8)
                             (writes ACTION+0x28=8, ACTION+0xc8=0x1e via a different call chain;
                              no SeqInfo write occurs in rstInitSkillAct's own body or its
                              confirmed tail-call chain, 3 functions deep, terminal, statically verified)
rstUpdateSeqDefaultSkill (rstUpdate's own seq=8 dispatch target, confirmed via jump-table decode):
    checks DefSkillResult == 2
    if true: several helper calls, then writes SeqInfo.Current = 21 directly (0x182288ae4)
    NOTE: confirmed via real-machine telemetry that this check/write does NOT succeed on the
    first call - native calls rstUpdateSeqDefaultSkill repeatedly (observed 20-70 times across
    multiple sessions) before the transition to seq=21 actually completes. A single direct call
    is confirmed insufficient.
seq=21/22: native forget-skill UI (rstUpdateSeqDestroySkill / rstUpdateSeqDestroyConfirm), entirely
    native's own existing UI - no synthetic reconstruction needed to enter or drive it once seq=21
    is reached
seq=22->8: forget confirmed, DefSkillResult still 2 unless explicitly cleared
```

## 5. Confirmed Normal-Flow Behavior After Forget Completion (baseline to preserve)

**CONFIRMED via direct native-only real-machine comparison** (no Inline/Queue intervention): after a forget flow completes (`seq=22->8`), if a new Mutation candidate is already available, native proceeds *in the same frame* to `seq=8->9` (Mutation calculation) and does **not** re-invoke `rstChkAddSkill` for the just-completed skill. `DefSkillResult` is `0` at this point in the confirmed-normal case.

**This is the baseline V3 must reproduce.** Any V3 mechanism that causes native to instead remain at `seq=8` re-evaluating an already-handled skill candidate is, by this baseline, a regression to be fixed - not a native quirk to route around.

## 6. Confirmed Facts About What NOT To Do (negative space)

- **REJECTED**: `rstInitSkillAct(8)` alone, called outside its native seq=8 context, transitions to seq=21. Confirmed insufficient in isolation across 2/2 real-machine PoC attempts.
- **REJECTED**: `RestoreSkillProgress` (a Queue-era snapshot-rollback to "the state at Pending.Enqueue time") is safe to run for an Inline-originated transaction. Confirmed to force native's own learn progression to restart.
- **REJECTED**: Leaving `DefSkillResult` non-zero after a transaction ends is safe. Confirmed to cause native's own seq=8 dispatch to re-enter the forget-action initializer indefinitely for the same unit.
- **REJECTED**: Explicit `pCurrentStock`/`WorkStock` rebinding to a *past* unit (Queue's `StartNext` model) is necessary for a Learn-As-New transaction whose native ownership window is still open. Confirmed: at the moment a Mutation is detected (Pending-equivalent point), native still holds `pCurrentStock == mutationUnit` in 5/5 sampled cases.
- **UNRESOLVED, not yet safe to assume either way**: whether restoring `WorkStock` to its pre-Inline value at transaction-completion time is itself correct, or whether native has its own expectation for what `WorkStock` should be at that point. The one real-machine test isolating this restoration was not completed before this document was written (see Section 8).
- **UNRESOLVED**: exact mechanism/timing of the "same skill candidate re-evaluated after Mutation completes" bug observed in the Inline PoC. Confirmed NOT explained by `GBWK+0x32` (skill-candidate seed) residue alone, since the confirmed-normal baseline (Section 5) also does not clear this field on the frame in question, yet does not re-enter `rstChkAddSkill`. The actual differentiator between "normal: advances past seq=8" and "Inline-observed: stuck re-evaluating seq=8" has not yet been isolated to a specific native call or state field.

## 7. Confirmed Multi-Object Confusion Traps (methodological, apply throughout V3)

1. Two static slots (`real-GBWK`, `TARGET`) were repeatedly confused because RIP-relative loads with superficially similar disassembly resolve to different absolute addresses depending on the instruction's own address. **Rule: never claim "same object" from instruction-shape similarity; always resolve to absolute address (`moduleBase + RVA`) and compare.**
2. `.pdata`/`RUNTIME_FUNCTION` boundaries can be single-chunk or chained (hot/cold split) for the *same* logical function; a short first-chunk boundary does not necessarily mean the function is that short - the full chain must be walked. Conversely, an adjacent function starting right after a chunk's end is not automatically part of the same function either - both possibilities must be checked via chain-walking before concluding either way.
3. The same numeric offset (`+0x32`, `+0x58`, `+0x60`) means different things on different base objects. Telemetry field names must always encode which base object was used (e.g. `realGbwk+0x32` vs. the pre-existing, unrelated `workStock+0x32`), and this document's field map (Section 1) is the single source of truth for what each *fully-qualified* base+offset pair means.

## 8. Explicitly Open Questions for V3 to Resolve Empirically (not to guess at)

1. What differentiates the confirmed-normal "advances past seq=8 after forget completes" case from the Inline-observed "gets stuck re-evaluating the same seq=8 candidate" case? (Section 6, last bullet)
2. Is WorkStock-restoration-at-completion correct, and if so, to what value?
3. What causes the "Two Frosts" / duplicate-party-entry visual corruption? Confirmed to persist even when the "stuck at seq=8" symptom and `DefSkillResult` residue are both independently fixed - this may be a separate, third failure mode, not automatically resolved by fixing 1 and 2.
4. Is the transient HP/Level display corruption (observed to self-correct once the status screen is opened) caused by the same root cause as (3), or independent?

**Explicit instruction carried into V3's own investigation discipline**: do not assume 1-4 share a single root cause. Investigate and fix incrementally, verifying each fix in isolation before combining.
