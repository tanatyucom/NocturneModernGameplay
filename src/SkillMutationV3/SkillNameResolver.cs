using System;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;

namespace NocturneModernGameplay
{
    // Diagnostic usability helper ONLY (Repeat=Unlimited / Option F
    // investigation). Resolves a raw skill ID to its in-game display name
    // for logging, so a human reading V3-OPTIONF-* lines does not have to
    // cross-reference numeric skill IDs manually. Not part of Skill
    // Mutation V3 / Repeat=Unlimited itself: no gameplay semantics, no
    // __result mutation, no native/game-state write of any kind. This file
    // adds display-name lookup only - it does not change what any existing
    // diagnostic observes.
    //
    // Native call used: Il2Cpp.datSkillName.Get(int, int) : string
    // (managed signature confirmed this session via raw metadata: public
    // static String Get(Int32, Int32), Il2Cpp.datSkillName - not guessed).
    // docs/investigation-log.md already identifies this as "表示名を返す
    // 汎用ディスパッチャ", called routinely from several existing native UI
    // paths (forget UI, Mutation presentation message, ordinary level-up,
    // Hearts Skill UI) - i.e. this is not a new/unusual call path, only a
    // new CALLER of an already-heavily-used native function.
    //
    // Second argument semantics (CONFIRMED for one call site, NOT
    // generalized to all of them): legacy/skillmutation/
    // SkillMutationLearnAsNew.LegacyFinal.cs's OverrideQueuedUiSkillId
    // documents that, for the forget-UI list-rendering call site
    // specifically, "Name rendering passes the demon id for list entries
    // and 0 for the confirmation message." The confirmation-message case
    // (second arg == 0) is therefore a CONFIRMED-valid, already-exercised
    // argument combination for this native function in production - this
    // resolver always passes 0, matching that already-proven-safe usage.
    // Whether "0" means the exact same thing (e.g. "no demon context") at
    // the other 3+ known call sites (rstCalc Mutation message,
    // rstUpdateSeqDefaultSkill, Hearts Skill UI) is UNRESOLVED and not
    // assumed here - this resolver only relies on the one call shape that
    // is directly evidenced as valid.
    //
    // Side-effect safety: this session did not newly disassemble
    // datSkillName.Get's own native body byte-by-byte. Evidence is
    // deliberately kept to the scope that is actually confirmed: this
    // function is an existing, routinely-invoked display-name lookup
    // dispatcher already called from several native UI/presentation paths
    // without any documented state-mutating side effect in this project's
    // prior investigation notes. It is not CONFIRMED side-effect-free by
    // fresh disassembly this session.
    internal static class SkillNameResolver
    {
        private const string UnresolvedName = "<name-unresolved>";

        // Process-lifetime cache. Diagnostic-only: never read by any
        // behavior-affecting code path, never persisted, never cleared by
        // gameplay events - a plain in-memory lookup avoiding a repeat
        // native call for the same skill ID.
        private static readonly Dictionary<int, string> Cache = new();

        // Resolves a skill ID to its display name, or UnresolvedName on any
        // failure (null return, exception, etc.) so a lookup failure can
        // never throw into or otherwise affect the caller's own diagnostic
        // (let alone Core's native processing).
        internal static string Resolve(int skillId)
        {
            if (Cache.TryGetValue(skillId, out string? cached))
                return cached;

            string resolved;
            try
            {
                // Second argument fixed at 0 - see class header comment
                // ("confirmation message" case, the one CONFIRMED-valid
                // shape for this argument this session found evidence for).
                string? name = datSkillName.Get(skillId, 0);
                resolved = string.IsNullOrEmpty(name) ? UnresolvedName : name;
            }
            catch (Exception ex)
            {
                resolved = UnresolvedName;
                MelonLogger.Warning(
                    "[NocturneModernGameplay] SkillNameResolver failed safely for " +
                    $"skillId={skillId}: {ex.Message}");
            }

            Cache[skillId] = resolved;
            return resolved;
        }

        // "19:\"SkillNameA\"" - single-ID named representation.
        internal static string Format(int skillId) =>
            $"{skillId}:\"{Resolve(skillId)}\"";

        // "19:\"SkillNameA\"->22:\"SkillNameC\"" - power-up pair named
        // representation.
        internal static string FormatPair(int fromSkillId, int toSkillId) =>
            $"{Format(fromSkillId)}->{Format(toSkillId)}";
    }
}
