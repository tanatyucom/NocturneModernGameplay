using System;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // ROOT-CAUSE FIX: DefaultSkill classification's "already handled this
    // result lifecycle" guard, applied at the exact native classifier
    // boundary.
    //
    // Full static confirmation this session: rstcalc.rstChkAddSkill(ushort
    // SkillID) : sbyte (VA 0x18227F940, exact metadata match) is called by
    // rstCalc's own DefaultSkill classification loop with SkillID=EventParam
    // (the current curriculum candidate). Its FULL body (thunk target
    // 0x19653FBE0, plus the nested pure array-scan 0x182410660) contains
    // ZERO write instructions - it is a completely pure, stateless
    // classifier:
    //   returns 0  if the candidate is already owned (found in stock.skill[])
    //   returns 1  if not owned and skillcnt < 8 (room available)
    //   returns 2  if not owned and skillcnt >= 8 (full - forget required)
    // It has NO concept of "this candidate was already decided earlier in
    // this same result lifecycle" - it purely re-checks current stock state
    // every single call. This is the SAME structural characteristic the old
    // Queue-era archive recorded for this exact function (rstChkAddSkill's
    // candidate selector, "349-zombie" root cause, master-archive.md
    // Section 8.5/11: "fully stateless - its only filter is
    // HasSkill(candidate)==false").
    //
    // The caller's own logic (rstCalc's classification loop, VA
    // ~0x18227EE58-0x18227EE97) treats a 0 result as "already owned, skip
    // this candidate" - decrementing LevelUpCnt and looping back to
    // re-evaluate, exactly the same bookkeeping path a genuinely-owned
    // candidate would take. Forcing this function's result to 0 for a
    // HandledCandidates-tracked (stock, skill) pair therefore does not
    // invent a new code path - it reuses native's own existing
    // "already-owned, move on" pathway, telling native "as far as this
    // result lifecycle is concerned, treat this candidate as already
    // settled" without ever touching DefSkillResult/EventNums/SeqInfo
    // directly.
    [HarmonyPatch(typeof(rstcalc), nameof(rstcalc.rstChkAddSkill))]
    internal static class DefaultSkillHandledClassificationBlock
    {
        internal static readonly bool Enabled = true;

        // Positional binding (__0) rather than the confirmed parameter name
        // "SkillID" - consistent with this codebase's established caution
        // for native-heavy interop methods (see OptionFCandidatePoolDiagnostics.cs).
        private static void Postfix(ushort __0, ref sbyte __result)
        {
            if (!Enabled) return;
            if (__result == 0) return; // already native's own "no-op" outcome
            try
            {
                var gbwk = rstinit.GBWK;
                var stock = gbwk?.pCurrentStock;
                if (stock == null || stock.Pointer == IntPtr.Zero) return;

                long stockPtr = stock.Pointer.ToInt64();
                if (!HandledCandidatesObserver.IsHandled(stockPtr, __0)) return;

                int frame = UnityEngine.Time.frameCount;
                MelonLogger.Msg(
                    "[NocturneModernGameplay] DEFAULTSKILL-CLASSIFICATION-BLOCK; " +
                    $"frame={frame}; unit={stock.id}; stockPtr=0x{stockPtr:X}; " +
                    $"candidate={SkillNameResolver.Format(__0)}; nativeResult={__result}; " +
                    "action=FORCE-RESULT-0-ALREADY-HANDLED.");

                __result = 0;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] DefaultSkillHandledClassificationBlock postfix failed safely: {ex.Message}");
            }
        }
    }
}
