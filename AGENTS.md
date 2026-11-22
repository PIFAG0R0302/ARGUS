# ARGUS Project Rules

## Clarify Before Coding (Pre-Implementation Gate)

Before editing any code for a **non-trivial bugfix, feature, or refactor** in ARGUS:
1. **Do not guess** ambiguous requirements, scope, UX behavior, edge cases, or architectural trade-offs.
2. **Inspect minimally first** (check the relevant file, logs, or error so questions are grounded in facts, not generic).
3. **Ask 5–10 concise, targeted clarifying questions** using `ask_question` (with recommended options first) — or a structured numbered list when free-text input is required. For deep design/architecture sessions, also utilize the project skills in `.agents/skills/` (`grilling`, `grill-me`, `brainstorming`).
4. **Wait for the user's confirmation** before modifying code or running build/destructive commands.

**Skip this gate only when:**
- The task is trivial/mechanical (typo, single-line fix, explicit exact instruction).
- All requirements and edge cases are already unambiguous from the user's prompt.
- The user explicitly asks to proceed immediately without questions.
