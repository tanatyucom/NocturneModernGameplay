# Research Notes

This directory is the Source of Truth for reverse-engineering and investigation notes.

## Required Evidence States
Every meaningful conclusion should be tagged as one of:

- **CONFIRMED**
- **HYPOTHESIS**
- **REJECTED**
- **UNRESOLVED**

## Suggested Investigation File

Create one Markdown file per major investigation, for example:

`YYYY-MM-DD_skill-mutation-workstock-ownership.md`

Recommended structure:

```md
# Investigation: <title>

## Question
What exactly are we trying to prove?

## Baseline
Game/mod/build/reproduction information.

## Known Facts
- [CONFIRMED] ...

## Hypotheses
- [HYPOTHESIS] ...

## Rejected Explanations
- [REJECTED] ...

## Runtime Evidence
Exact logs, pointer identities, state transitions, screenshots, etc.

## Static Evidence
Functions, RVAs, instructions, dereference chains, call graph.

## Current Model
Best explanation that fits all evidence.

## Unresolved
- [UNRESOLVED] ...

## Next Minimal Test
One test that maximally distinguishes the remaining explanations.

## Implementation Consequence
What may safely change now, and what must not yet change.
```

## Native Evidence

When native analysis is involved, distinguish and record:

- function
- instruction address / RVA
- static slot
- base object
- dereference chain
- field offset
- runtime pointer identity
- pointed-to content, separately from pointer identity

Prefer `moduleBase + RVA` over an absolute address. A similar-looking static slot is not sufficient evidence that two observations share the same context.

## Principle
Corrections are part of the research record.
When a hypothesis is disproven, mark it REJECTED rather than deleting the history.
