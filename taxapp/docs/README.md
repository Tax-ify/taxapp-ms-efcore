# EF Core codebase taxonomy

Internal reference notes mapping major EF Core subsystems to source locations.

| Topic | Document |
|-------|----------|
| Onboarding: migration files, snapshots, and why git merge/rebase fails | [migration-study-guide.md](migration-study-guide.md) |
| Code First migration scaffolding (`.cs`, `.Designer.cs`, model snapshot) | [migration-generation.md](migration-generation.md) |
| Recovering out-of-sync Designer / snapshot after parallel team merges | [migration-merge-recovery.md](migration-merge-recovery.md) |
| Tool: `eng/Tools/MigrationMetadataRegenerator` | see migration-merge-recovery.md |

Suggested reading order: **study guide → generation (optional skim) → merge recovery (when needed)**.
