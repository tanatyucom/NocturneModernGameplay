# Skill Mutation native flow analysis

## Scope

Battle result processing from party-demon level-up through normal skill learning,
skill mutation calculation, mutation UI, skill replacement, and return to the next
result target.

Static executable addresses below are for the currently installed SMT3HD build.

## Confirmed methods and addresses

| Method | Static address / target | Role |
|---|---:|---|
| `rstChkSkillPowerUp1` | `0x182280220` | First mutation-start check (level and 25% gate) |
| `rstChkSkillPowerUp2` | thunk `0x182280340`, target `0x1965467B0` | Second mutation-start/random-category check |
| `rstCalcSkillPowerUp` | thunk `0x18227E5F0`, target `0x19653E7D0` | Wrapper gate before mutation core |
| `rstCalcSkillPowerUpCore` | `0x18227E100` | Candidate selection, duplicate checks, processed flag, mutation mapping |

## Result work layout used by mutation

Offsets are relative to the native result work object behind `rstinit.GBWK`.

| Offset | Managed field | Meaning |
|---:|---|---|
| `+0x4A` | target cursor/index | Current party-result target index |
| `+0x4B` | `PUpSkillResult` | Mutation result/category consumed by update UI |
| `+0x4C` | `PUpSkillIndex` | Learned-skill slot selected for mutation |
| `+0x4E` | `PUpSkillID` | Initially selector result; then overwritten with mutated skill ID |
| `+0x60` | `pCurrentStock` | Demon currently owned by the calculator |
| `+0x7E` | mutation UI substate | Internal state used by `rstUpdateSeqSkillPowerUp` |

`rstCalcSeqDevilLevelUp` selects a roster entry using the result target cursor and
writes that exact demon pointer to `resultWork + 0x60`. This is the authoritative
calculation target. `WorkStock` can still refer to a different UI/ordinary-learn
target while queued custom work is active; mixing the two caused the observed
High Pixie/Jack Frost cross-unit bug.

## `rstCalcSkillPowerUpCore` exact decision order

1. Calls `rstRndGetPowerUpSkill(pCurrentStock, &work.PUpSkillIndex)`.
2. Stores its return value into `work.PUpSkillID`.
3. Returns `0` when no candidate was returned.
4. Rejects a candidate already present in the demon's pre-level skill list.
5. Produces a random category (`0`, `1`, or `2`), with different thresholds based
   on the demon/result condition read from `pCurrentStock + 0x14`.
6. Checks whether the candidate is a valid pre-level owned skill.
7. Tests `pCurrentStock->flag & 0x40` and immediately returns `0` when set.
8. Scans the party demons and observes the same `flag & 0x40` processed marker.
9. On the no-mutation branch, sets `pCurrentStock->flag |= 0x40` and returns `1`.
10. On the mutation branch, reads `pCurrentStock->skill[PUpSkillIndex]`, calls the
    native mutation mapper, stores the mapped skill into `work.PUpSkillID`, and
    returns `2` when mapping succeeds (otherwise `3`).

### Critical correction

`datUnitWork_t.flag & 0x40` is not an eligibility/candidate flag. It is a
processed/suppression marker. Forcing it on every roster demon makes the core
return `0` before mutation mapping. The earlier `MutationEligibilityFlagPatch`
therefore inverted the intended behavior and must be removed.

## Result sequence observed at runtime

Normal full-skill level-up flow:

```text
0 -> 6 (select demon) -> 7 -> 8 (normal skill check)
  -> 21 (forget prompt init) -> 22 (selection)
  -> 8 (ordinary learn completed)
```

When native mutation succeeds after returning from child sequence 21:

```text
8/last=21 -> mutation core returns 2
          -> 10 (mutation presentation/decision)
          -> mutation replacement write
          -> 11 -> 12 -> 13 -> 14
          -> 6 (next demon) or 19 (result end)
```

`rstUpdateSeqSkillPowerUp` consumes all of `PUpSkillResult`, `PUpSkillIndex`,
`PUpSkillID`, and the internal byte at `+0x7E`. Setting only the three exposed
managed fields is not equivalent to entering the native sequence normally.

## Causes of the failed diagnostic revisions

1. Patching only `test al,3` removed one 25% gate, but result construction also
   contains other checks and inlined copies.
2. Patching method entries did not affect checks that were inlined into `rstCalc`.
3. Treating flag `0x40` as eligibility suppressed the mutation core.
4. Editing only the Postfix return/index of `rstRndGetPowerUpSkill` omitted native
   selector side effects.
5. Calling the mapper merely to probe candidates consumed/changed mutation state.
6. Running the unfinished Learn-as-New queue alongside the diagnostic changed
   `pCurrentStock` during the next demon's flow, producing cross-unit UI content.
7. Detecting `seq=8,last=21,change=1` in an update Prefix was one frame too late;
   that state is created inside the update and is only reliably visible in Postfix.

## Safe implementation direction

The next diagnostic build should make only these controlled changes:

1. Disable Learn-as-New completely during forced-mutation testing.
2. Remove all writes of demon flag `0x40` and remove the patched
   `rstChkDevilSkillPowerUp` / `rstChkPartyDevilSkillPowerUp` returns.
3. Observe the `rstUpdate` Postfix transition to `seq=8,last=21,change=1`.
4. Use the authoritative `pCurrentStock` that was set by
   `rstCalcSeqDevilLevelUp`; never replace it with a queued unit.
5. Invoke a replacement calculation once per result target, populate all native
   mutation fields including the `+0x7E` substate, then enter sequence 10 using
   the same values written by a captured natural success.
6. Keep a per-result-target key (target cursor plus unit pointer), not only demon
   ID, and clear it on result sequence 0/19.

Before implementing item 5, capture one unmodified natural success with byte-level
snapshots of result work immediately before and after `rstCalcSkillPowerUpCore`.
That snapshot will determine the exact `+0x7E` value and any adjacent fields that
must be copied; guessing them is no longer acceptable.
