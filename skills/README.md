# skills

Claude Code skills - reusable procedures rather than programs. Each folder holds a `SKILL.md`
that Claude loads on demand when a matching problem comes up, plus any scripts it refers to.

| Skill | What it is for |
| --- | --- |
| [game-network-priority](game-network-priority) | Working out why a competitive game shows packet loss while something is downloading, and which fixes actually help. The short answer is bufferbloat, and it is not what most guides tell you to do. |

## Using these

Copy a folder into `~/.claude/skills/` (user-wide) or `.claude/skills/` inside a project.
Claude reads the `description` in the frontmatter to decide when a skill is relevant, so that
line matters more than the body.
