# Installed project skills — BE

Updated: 2026-09-13

All skills in this file are copied project-local under `.agents/skills/`. The
generated `skills-lock.json` records the source paths and content hashes.

## .NET/PostgreSQL skills

- `backend-patterns` — `affaan-m/ecc`; service boundaries and backend layering.
- `dotnet-backend-patterns` — `wshobson/agents`; .NET backend patterns.
- `postgresql-table-design` — `wshobson/agents`; PostgreSQL schema/table guidance.

## Cross-cutting review skills

- `ponytail`, `ponytail-review`, `ponytail-audit`, `ponytail-debt`,
  `ponytail-gain`, `ponytail-help` — `DietrichGebert/ponytail`.
- Use the narrowest Ponytail skill for the task; no global plugin or hook was
  installed.

## Priority and safety

For API work, start with `dotnet-backend-patterns`; use
`postgresql-table-design` only for schema/data-model work. Do not run database
migrations or connect production merely because a skill suggests it. Review
each `SKILL.md` before executing scripts and keep credentials out of notes.

## Explicitly excluded

- `compose-multiplatform-patterns` (Kotlin Compose).
- Flutter agent plugins and Dart skills.
