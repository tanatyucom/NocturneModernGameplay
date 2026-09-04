# AI_WORKFLOW.md

## Mod Development Workflow Standard

This is a lightweight GDS workflow intended for mod development in general, not for one specific game or mod.

### 1. Question
Write one concrete technical question.

Bad:
> Why is the mod broken?

Good:
> Which native object owns the skill array written by function X at state Y?

### 2. Baseline
Record:
- game/mod version
- build hash if useful
- relevant configuration
- known-good behavior
- current branch/commit
- reproduction steps

Preserve a known-good baseline before an experiment. Use a separate branch for a large experiment when practical.

### 3. Evidence Table
Maintain findings using:

| Finding | State | Evidence | Next action |
|---|---|---|---|
| Example | CONFIRMED | static + runtime | none |
| Example | HYPOTHESIS | indirect | targeted telemetry |

Allowed states:
CONFIRMED / HYPOTHESIS / REJECTED / UNRESOLVED

### 4. Reverse Engineering Order
Preferred order:
1. existing source and metadata
2. static/native analysis
3. cross-reference / call-chain analysis
4. read-only runtime telemetry
5. minimal experimental intervention
6. production implementation

No Hook Without Analysis. Do not add a hook merely because it is easy.

For native findings, record the function, instruction address/RVA, static slot, base object, dereference chain, field offset, and runtime pointer identity as applicable. Distinguish pointer identity from pointed-to content. Do not infer shared context from a static slot's appearance alone. Prefer `moduleBase + RVA` over absolute addresses.

### 5. Telemetry Rules
Telemetry should:
- be read-only,
- reuse existing hooks where possible,
- log identity/provenance rather than only interpreted labels,
- be removable,
- avoid changing state-machine timing when possible.

Keep telemetry and behavior changes separate.

### 6. Experimental Change
Before changing behavior, state:
- exact intervention point,
- expected effect,
- failure mode,
- rollback,
- evidence needed to promote it.

Experiments should fail closed where practical and use the smallest useful real-hardware test.

### 7. Validation
Validate:
- intended success case,
- known failure/cancel path,
- repeated/reload behavior when relevant,
- multi-entity / queue behavior when relevant,
- save/load or persistence when relevant,
- no corruption of unrelated state.

### 8. Research-to-Implementation Gate
Implementation may proceed when:
- ownership/context is understood enough for the change,
- the intervention point is justified,
- major alternative explanations are rejected or bounded,
- rollback is known.

### 9. Documentation
Keep research history in `docs/research/`.
Do not rewrite old hypotheses as though they were always known.
Preserve corrections.

### 10. Commit Boundary
Prefer one conceptual change per commit:
- telemetry
- RE finding/documentation
- implementation
- cleanup

Do not mix broad refactors into a bug-fix experiment unless required. Do not mix unrelated features into one change.
