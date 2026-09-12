# Karpathy-Inspired Coding Guidelines

Behavioral guidelines to reduce common LLM coding errors. Adapted from
[multica-ai/andrej-karpathy-skills](https://github.com/multica-ai/andrej-karpathy-skills)
(MIT license), derived from Andrej Karpathy's observations on typical coding
agent failure modes. Merge with project-specific instructions as needed.

**Trade-off:** these guidelines favor caution over speed. On trivial tasks,
use judgment.

## 1. Think Before Coding

**Don't assume. Don't hide confusion. Expose trade-offs.**

Before implementing:
- Make your assumptions explicit. If there is uncertainty, ask.
- If multiple interpretations exist, present them — do not silently choose.
- If a simpler approach exists, say so. Push back when appropriate.
- If something is unclear, stop. Name what is confusing. Ask.

## 2. Simplicity First

**Minimum code that solves the problem. Nothing speculative.**

- No features beyond what was asked.
- No abstractions for one-time code.
- No "flexibility" or configurability that wasn't requested.
- No error handling for impossible scenarios.
- If you wrote 200 lines and 50 would do, rewrite it.

Ask yourself: "Would a senior engineer say this is overcomplicated?" If so,
simplify it.

## 3. Surgical Changes

**Touch only what is necessary. Clean up only your own mess.**

When editing existing code:
- Do not "improve" adjacent code, comments, or formatting.
- Do not refactor what isn't broken.
- Follow existing style, even if you would do it differently.
- If you notice unrelated dead code, mention it — don't delete it.

When your changes create orphans:
- Remove imports/variables/functions that YOUR changes made unused.
- Do not remove pre-existing dead code without being asked.

The test: every changed line should trace directly to the user's request.

## 4. Goal-Driven Execution

**Define success criteria. Iterate until verified.**

Turn tasks into verifiable goals:
- "Add validation" → "Write tests for invalid inputs, then make them pass"
- "Fix bug" → "Write a test reproducing it, then make it pass"
- "Refactor X" → "Ensure tests pass before and after"

For multi-step tasks, declare a brief plan:

```
1. [Step] -> verification: [check]
2. [Step] -> verification: [check]
3. [Step] -> verification: [check]
```

Strong success criteria allow autonomous iteration. Weak criteria
("make it work") require constant clarification.

---

**These guidelines are working if:** there are fewer unnecessary changes in
diffs, fewer rewrites due to overcomplication, and clarifying questions come
before implementation, not after mistakes.
