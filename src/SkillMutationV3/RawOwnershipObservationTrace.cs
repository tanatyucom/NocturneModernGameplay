using System;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Skill Power-Up AddNew V3 - DEFAULTSKILL RUNTIME OBSERVATION
    // VALIDATION / RAW POINTER / RAW MEMORY TRACE.
    // Read-only observer only. Never writes any field.
    //
    // Purpose: exhaustive static analysis of rstUpdateSeqDefaultSkill (VA
    // 0x182288790, identity confirmed 3 independent ways: dispatcher seq=8
    // case block direct call, IL2CPP CodeGenModule metadata mapping, and
    // Harmony runtime correlation) and every reachable callee, including
    // both IL2CPP lazy-resolved generic thunks (resolved to UnityPlayer.dll
    // generic string-formatting code) and the GC-safepoint helper, found
    // NO skill[]/skillcnt write anywhere. Before searching further for a
    // native writer, this class checks a more basic question: is the
    // "stock" object read via the managed wrapper property
    // (GBWK.pCurrentStock) during Prefix and Postfix actually the SAME
    // native datUnitWork_s memory, read the same way native itself would?
    //
    // This class never trusts the wrapper alone. For every field of
    // interest it reads BOTH:
    //   - the wrapper property value (GBWK.pCurrentStock, stock.skillcnt,
    //     stock.skill[], stock.Pointer)
    //   - the raw native memory at the known confirmed offsets, via
    //     Marshal.ReadInt64/ReadInt32 directly against pointers obtained
    //     from raw reads (never from a property getter beyond the first
    //     GBWK.Pointer step)
    // and logs both side by side, plus a same-pointer verdict, so a
    // pointer-rebinding/wrapper-caching artifact can be told apart from a
    // genuine same-memory ownership change.
    //
    // Confirmed offsets used here (see SkillMutationV3_DesignSpec.md
    // Section 1/2, CONFIRMED via a prior session's runtime pointer-aligned
    // scan, 83/83 observations for pCurrentStock/WorkStock):
    //   GBWK + 0x60 = pCurrentStock field (pointer to datUnitWork_s)
    //   GBWK + 0x68 = WorkStock field (pointer to datUnitWork_s)
    //   GBWK + 0x32 = pending skill id (ushort), CONFIRMED this session
    //   datUnitWork_s + 0x14 = id (Int32)
    //   datUnitWork_s + 0x48 = skillcnt (Int32)
    //   datUnitWork_s + 0x50 = skill field (pointer to Int32[] array object)
    // IL2CPP array object layout assumed for the skill backing data
    // (standard il2cpp-api-types.h Il2CppArray shape - Il2CppObject header
    // 16 bytes [klass+monitor] + bounds ptr 8 bytes + max_length 8 bytes =
    // 0x20 byte header, then element data). This assumption is
    // cross-checked against the wrapper's own stock.skill[i] values in the
    // log output rather than trusted blindly.
    [HarmonyPatch(typeof(rstupdate), nameof(rstupdate.rstUpdateSeqDefaultSkill))]
    internal static class RawOwnershipObservationTrace
    {
        // HI-PIXIE STALL HEALTH CHECK: disabled - see
        // DefaultSkillIteratorTrace.Enabled for why.
        internal static readonly bool Enabled = false;

        private const int ArrayHeaderOffset = 0x20;
        private const int SlotCount = 8;

        private struct Snapshot
        {
            internal bool Valid;
            internal long GbwkPtr;
            internal long PCurrentStockField;
            internal long WorkStockField;
            internal long StockWrapperPtr;
            internal int RawUnit;
            internal int RawSkillCnt;
            internal long RawSkillArrayFieldPtr;
            internal int[] RawSkills;
            internal int WrapperUnit;
            internal int WrapperSkillCnt;
            internal int[] WrapperSkills;
            internal ushort RawPending32;
        }

        private static Snapshot _before;

        private static Snapshot Capture()
        {
            var s = new Snapshot { RawSkills = Array.Empty<int>(), WrapperSkills = Array.Empty<int>() };
            try
            {
                var gbwk = rstinit.GBWK;
                if (gbwk == null || gbwk.Pointer == IntPtr.Zero) return s;

                // --- raw-first: GBWK base and its two stock-pointer fields ---
                s.GbwkPtr = gbwk.Pointer.ToInt64();
                s.PCurrentStockField = Marshal.ReadInt64(gbwk.Pointer, 0x60);
                s.WorkStockField = Marshal.ReadInt64(gbwk.Pointer, 0x68);
                s.RawPending32 = unchecked((ushort)Marshal.ReadInt16(gbwk.Pointer, 0x32));

                if (s.PCurrentStockField != 0)
                {
                    var rawStockPtr = new IntPtr(s.PCurrentStockField);
                    s.RawUnit = Marshal.ReadInt32(rawStockPtr, 0x14);
                    s.RawSkillCnt = Marshal.ReadInt32(rawStockPtr, 0x48);
                    s.RawSkillArrayFieldPtr = Marshal.ReadInt64(rawStockPtr, 0x50);

                    var rawSkills = new int[SlotCount];
                    if (s.RawSkillArrayFieldPtr != 0)
                    {
                        var arrPtr = new IntPtr(s.RawSkillArrayFieldPtr);
                        for (int i = 0; i < SlotCount; i++)
                        {
                            rawSkills[i] = Marshal.ReadInt32(arrPtr, ArrayHeaderOffset + i * 4) & 0xFFFF;
                        }
                    }
                    s.RawSkills = rawSkills;
                }

                // --- now the wrapper, for comparison only, never as the source of raw values above ---
                var stock = gbwk.pCurrentStock;
                if (stock != null && stock.Pointer != IntPtr.Zero)
                {
                    s.StockWrapperPtr = stock.Pointer.ToInt64();
                    s.WrapperUnit = stock.id;
                    s.WrapperSkillCnt = stock.skillcnt;
                    var ownedArray = stock.skill;
                    int len = ownedArray?.Length ?? 0;
                    var wrapperSkills = new int[SlotCount];
                    for (int i = 0; i < SlotCount && i < len; i++)
                    {
                        wrapperSkills[i] = ownedArray![i] & 0xFFFF;
                    }
                    s.WrapperSkills = wrapperSkills;
                }

                s.Valid = true;
            }
            catch
            {
                s.Valid = false;
            }
            return s;
        }

        private static void Prefix()
        {
            if (!Enabled) return;
            try
            {
                _before = Capture();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] RawOwnershipObservationTrace prefix failed safely: {ex.Message}");
            }
        }

        private static void Postfix()
        {
            if (!Enabled) return;
            try
            {
                var after = Capture();
                if (!_before.Valid || !after.Valid) return;

                bool skillCntChanged = _before.RawSkillCnt != after.RawSkillCnt ||
                                        _before.WrapperSkillCnt != after.WrapperSkillCnt;
                bool skillsChanged = !SameArray(_before.RawSkills, after.RawSkills) ||
                                      !SameArray(_before.WrapperSkills, after.WrapperSkills);
                if (!skillCntChanged && !skillsChanged) return;

                bool samePCurrentStockPointer = _before.PCurrentStockField == after.PCurrentStockField;
                bool sameWorkStockPointer = _before.WorkStockField == after.WorkStockField;
                // NOTE: rawUnit (read from pCurrentStock+0x14) is EXCLUDED
                // from this agreement check. This session's real-machine
                // test showed rawUnit (e.g. 5505083) never matches
                // wrapperUnit (stock.id, e.g. 59) - the +0x14 offset
                // assumption for a raw "id" read is WRONG (or the field is
                // a different width/type than assumed) and is not yet
                // corrected. rawUnit is still logged for visibility but
                // must not be treated as validated until the correct offset
                // is confirmed. The load-bearing fields below (pointer
                // identity, skillcnt, skill array) are each independently
                // confirmed accurate and are unaffected by this.
                bool rawWrapperAgreeBefore = _before.PCurrentStockField == _before.StockWrapperPtr &&
                                              _before.RawSkillCnt == _before.WrapperSkillCnt &&
                                              SameArray(_before.RawSkills, _before.WrapperSkills);
                bool rawWrapperAgreeAfter = after.PCurrentStockField == after.StockWrapperPtr &&
                                             after.RawSkillCnt == after.WrapperSkillCnt &&
                                             SameArray(after.RawSkills, after.WrapperSkills);

                MelonLogger.Msg(
                    "[NocturneModernGameplay] RAW-OWNERSHIP-PREFIX; " +
                    $"gbwkPtr=0x{_before.GbwkPtr:X}; pCurrentStockField=0x{_before.PCurrentStockField:X}; " +
                    $"workStockField=0x{_before.WorkStockField:X}; stockWrapperPtr=0x{_before.StockWrapperPtr:X}; " +
                    $"rawUnitUnverifiedOffset0x14={_before.RawUnit}; rawSkillCnt={_before.RawSkillCnt}; " +
                    $"rawSkillArrayFieldPtr=0x{_before.RawSkillArrayFieldPtr:X}; " +
                    $"rawSkills=[{string.Join(",", _before.RawSkills)}]; " +
                    $"wrapperUnit={_before.WrapperUnit}; wrapperSkillCnt={_before.WrapperSkillCnt}; " +
                    $"wrapperSkills=[{string.Join(",", _before.WrapperSkills)}]; " +
                    $"rawPending32={_before.RawPending32}.");

                MelonLogger.Msg(
                    "[NocturneModernGameplay] RAW-OWNERSHIP-POSTFIX; " +
                    $"gbwkPtr=0x{after.GbwkPtr:X}; pCurrentStockField=0x{after.PCurrentStockField:X}; " +
                    $"workStockField=0x{after.WorkStockField:X}; stockWrapperPtr=0x{after.StockWrapperPtr:X}; " +
                    $"rawUnitUnverifiedOffset0x14={after.RawUnit}; rawSkillCnt={after.RawSkillCnt}; " +
                    $"rawSkillArrayFieldPtr=0x{after.RawSkillArrayFieldPtr:X}; " +
                    $"rawSkills=[{string.Join(",", after.RawSkills)}]; " +
                    $"wrapperUnit={after.WrapperUnit}; wrapperSkillCnt={after.WrapperSkillCnt}; " +
                    $"wrapperSkills=[{string.Join(",", after.WrapperSkills)}]; " +
                    $"rawPending32={after.RawPending32}.");

                MelonLogger.Msg(
                    "[NocturneModernGameplay] RAW-OWNERSHIP-VERDICT; " +
                    $"samePCurrentStockPointer={samePCurrentStockPointer}; sameWorkStockPointer={sameWorkStockPointer}; " +
                    $"rawWrapperAgreeBefore={rawWrapperAgreeBefore}; rawWrapperAgreeAfter={rawWrapperAgreeAfter}; " +
                    $"rawSkillCntChanged={_before.RawSkillCnt != after.RawSkillCnt}; " +
                    $"rawSkillsChanged={!SameArray(_before.RawSkills, after.RawSkills)}; " +
                    $"insertedRawEqualsPending32={(after.RawSkillCnt == _before.RawSkillCnt + 1 && after.RawSkillCnt >= 1 && after.RawSkillCnt <= SlotCount && after.RawSkills[after.RawSkillCnt - 1] == _before.RawPending32)}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning(
                    $"[NocturneModernGameplay] RawOwnershipObservationTrace postfix failed safely: {ex.Message}");
            }
        }

        private static bool SameArray(int[] a, int[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }
    }
}
