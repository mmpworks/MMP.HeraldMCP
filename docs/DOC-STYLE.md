# Documentation rules for this repo

Every document in this repo follows these rules. They are adapted from the
house application-writing discipline, which fixes the same failure modes
technical docs have: claims above their evidence, prose written to impress,
and repetition across documents.

## Evidence rules

1. **Every claim traces to something a reader can check**: a source file, a
   test name, a benchmark output, a PRD section, a commit. A claim with no
   trace gets deleted or downgraded until it has one.
2. **State claims at their evidence tier, never above.** "Masks the
   patterns in `RedactionPatterns.cs`, verified by A12's corpus" — never
   "keeps your secrets safe." A test proves what it tests; a benchmark
   proves the machine it ran on. Say which.
3. **Reconcile counts before polishing prose.** If a doc says five tools
   and the code registers four, stop writing and resolve the fact. Numbers
   agree across README, reference, and PRD or the build is not done.
4. **Never add a capability because a reader might want it.** The
   documentation boundary is the shipped behavior. Roadmap items are
   labeled as such and live in one place.

## Writing rules

5. **A person doing the work, in plain verbs**: reads, masks, refuses,
   counts, returns. No fragment stacks ("Path containment. Redaction at
   the boundary. Budgets enforced.") — write the sentence.
6. **Cut the moral after the evidence.** The mechanism shows the property.
   "The searcher reads only from the caller's reader; a test proves there
   is no filesystem fallback" needs no closing lesson about why that
   matters.
7. **No manifesto openings.** Start with what the thing is and does, not a
   belief about observability or the agent era.
8. **No invented opponent.** Describe our method without inventing a
   careless alternative someone else would have chosen.
9. **No slogans.** A compact maxim ("the agent proposes, the gate
   decides") repeats what the mechanism already established. State the
   enforcement point and the rejection behavior instead.
10. **Detail density is not credibility.** Pick the two or three mechanisms
    that distinguish the component; put the rest in the reference page.
    A README is not an architecture review.
11. **No exact-match declarations.** Never announce the tool is what the
    reader needs; show the named behavior and let the fit emerge.

## Structure rules

12. **Each document has one job, and they do not repeat each other:**
    - `README.md` — what it is, what it does, the five-minute start, where
      everything else lives.
    - `docs/tools.md` — the tool reference: parameters, result shape,
      errors, budgets. The one place tool behavior is specified for users.
    - `docs/security.md` — the security model and its RESIDUALS, stated in
      full. This is the page that says what the masker cannot guarantee.
    - `docs/PRD.md` — the build contract and its history. Not user-facing.
    - `docs/prd-design-meetings/` — the review-process record.
13. **Residuals are content, not shame.** The false-negative boundary of
    the masker, the O(pages × size) paging cost, the operator expectation
    on root config — these get stated where users will read them, with the
    same prominence as the features.
14. **House mechanics apply on top**: ASD-STE100 sentence rules, the plain
    declarative register (conclusion first, enumerate, repeat the noun),
    and the tic gate (`python ~/.claude/hooks/tic-gate.py <file>`) before
    any commit of outward text.

## Acceptance check for any doc

- Every claim has a trace a reader can follow.
- Counts and version numbers agree with the code and with every other doc.
- No sentence exists only to impress; no lesson follows its own evidence.
- The reader knows what to do next after every section.
- Nothing internal leaked outward (gates, verdicts, private repo names).
