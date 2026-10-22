---
name: raise-question
description: Escalate a product or architecture uncertainty to the architect. Use instead of guessing, when a decision is not settled by CLAUDE.md, an ADR or a spec.
---

# Raise a question

1. Find the next free number: look at `docs/questions/` and use `Q-` plus the next three-digit number.
2. Copy `docs/questions/_template.md` to `docs/questions/Q-xxx.md` and fill it:
   - **Raised by:** your agent name and the task id.
   - **Context:** what you were doing and why it matters. Name the ADR or spec that does not decide it.
   - **Options:** 2 or 3 real alternatives, each with its main trade-off.
   - **Recommendation:** which option you would pick and why.
   - Leave **Answer (architect)** empty and Status `open`.
3. Set the task's Status to `blocked` and add a `Blocked by: Q-xxx` line to its header.
4. Stop work on the blocked part and move on to unblocked tasks. Do not build on an assumed answer.

Do not re-litigate an accepted ADR. If you think one is wrong, write a question that says so.
