# AGENTS.md

## Purpose
This repository uses a lightweight GDS-style workflow for game mod development.

## Core Rules
- Analyze before hooking or patching.
- No Hook Without Analysis.
- Prefer the smallest reversible change.
- Separate observation/telemetry from behavior changes.
- Do not silently upgrade hypotheses into facts.
- Preserve a clean known-good baseline before experiments.
- Experimental work should live on a separate branch when practical.
- Do not mix unrelated features into one implementation step.

## Evidence States
Use these labels consistently:
- CONFIRMED — directly verified by source, static analysis, runtime evidence, or repeatable behavior.
- HYPOTHESIS — plausible explanation not yet proven.
- REJECTED — tested or analyzed and shown not to fit the evidence.
- UNRESOLVED — important question still open.

## Workflow
1. Define the exact question.
2. Gather existing evidence.
3. Perform static analysis first where possible.
4. Add read-only telemetry only when static evidence is insufficient.
5. Run the minimum useful real-hardware test.
6. Update research notes with evidence state.
7. Implement only after the intervention point is sufficiently understood.
8. Validate success and regression behavior.
9. Record rollback/fallback.

## AI Roles
- ChatGPT: coordinator / architecture / evidence synthesis.
- Claude Code or equivalent: deep static/native reverse engineering.
- Codex or equivalent: local implementation, build, validation, repository work.

Roles are guidelines, not authority boundaries. Evidence outranks tool preference.

## Safety Boundary
Never treat a pointer, offset, field name, or object identity as confirmed solely because it resembles an earlier pattern.
Record exact provenance:
- function
- instruction address / RVA when relevant
- static slot / base object
- dereference chain
- field offset
- runtime pointer identity when available

For native analysis:
- Distinguish pointer identity from the content pointed to.
- Do not infer a shared context from a similar-looking static slot alone.
- Prefer `moduleBase + RVA` over absolute addresses.
- Use fail-closed behavior when an expected address, byte sequence, pointer chain, or runtime invariant cannot be verified.

## Completion
A task is complete only when:
- intended behavior is verified,
- known regressions are checked,
- unresolved items are explicitly listed,
- generated telemetry/debug code is retained or removed intentionally,
- rollback is documented.
