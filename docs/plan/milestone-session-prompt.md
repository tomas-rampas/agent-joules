# Agent Joules: milestone build session prompt

One Claude Code session per milestone, orchestrated by the top-tier model, implemented by cheaper sub-agents. The user starts a session with working directory `D:\src\github\NvidiaSDKProjects\hackathon-nebius-nvidia-2026\agent-joules` and types one kickoff line:

```
Run docs/plan/milestone-session-prompt.md for MILESTONE <Mk>. Previous exit commit: <hash or origin/main>. User actions done: <list or none>.
```

Everything below is binding for that session.

## 1. Role and model rules

- You are the orchestrator. You plan, delegate, verify sub-agent claims against the files, and own commits and the build log. You do not write code, tests or documentation yourself; a sub-agent does, and you check the result.
- Pass `model` explicitly on every Agent call so the user sees the tier. Tiers: `csharp-expert` sonnet (implementation), `technical-docs-writer` haiku (documentation text), `bash-expert` or `powershell-expert` haiku (long builds, test runs, log grinding; they return the exact command and its integer exit code), `code-review-gatekeeper`, `security-specialist`, `spec-compliance-reviewer`, `peer-review-critic` opus (gates, never moved down), `comprehensive-analyst` sonnet (analysis), `Explore` haiku (broad read-only search). Never `fable`, never `fork`.
- Token discipline: read the milestone story file once and fully; read `docs/plan/plan.md` sections 2, 3.4, 3.5 and 4 and only the design sections a story names; read the prompt's standing orders and the REQ and EDGE items the milestone covers; never re-read a file you already hold; run short commands inline and delegate long or noisy runs to an executor; batch independent tool calls in one turn.
- Reviewer findings are applied, not argued, unless they contradict the prompt, the design or the plan; then record the conflict in `docs/build-log.md` and keep the binding text.

## 2. Binding inputs, in reading order

1. `docs/agent-joules.prompt.md`: the Builder standing orders (items 1 to 8), sections 6 (honesty), 8 (secrets), 10 (time boxes), 12 (user actions), and the REQ and EDGE items listed in the milestone's Coverage section with their Acceptance lines.
2. `docs/design/technical-design.md`: section 2 (solution layout and build files) for M0, then the sections each story names; section 9.2 resolves conflicts with the prompt.
3. `docs/plan/plan.md`: the milestone row (section 2), the gate table and re-planning rule (3.5), the cut order (3.4), the review gates (4), the seams the milestone inherits (5).
4. `docs/plan/stories/<Mk>-*.md`: every story, in dependency order: Covers, acceptance criteria, Gherkin scenarios, tasks, test mapping, docs impact, definition of done.
5. `docs/build-log.md`: the previous session's `## <Mk-1> exit` block (base commit, open items, velocity). For M0 there is none.

## 3. Preconditions, stop if any fails

- `git status --short` is empty and `git rev-parse HEAD` equals the previous exit commit named in the kickoff line (for M0: `origin/main`).
- The previous milestone's story file has every definition-of-done box ticked, or the kickoff line names the exceptions.
- `dotnet --version` prints 10.0.4xx; from M1 on, `dotnet build -warnaserror -tl:off --no-incremental` exits 0 before any change.
- Keys the milestone needs exist in user scope: check with a length-only PowerShell expression such as `[Environment]::GetEnvironmentVariable('NEBIUS_API_KEY','User').Length`; never print a value, never write one to a file.
- The user actions due before this milestone (plan.md section 3 calendar, prompt section 12) are confirmed in the kickoff line; otherwise ask once and stop.
- Create the milestone branch from main: `git switch -c milestone/<Mk>`. All commits of the session land on it.

## 4. Per-story loop

Take stories in the dependency order of the story file. For each story:

1. **Red.** `csharp-expert` (sonnet) writes the story's Reqnroll feature file under `tests/AgentJoules.Specs/Features/` with the story's scenarios verbatim and their tags (`@unit` or `@integration`, `@AJ-S<nn>`, `@REQ-xxx` or `@EDGE-xxx`, `@positive` or `@negative`), plus the unit tests the story's test mapping names, in the classes the design names. An executor runs `dotnet test --filter "Category=AJ-S<nn>"` and reports the failing count: red must be real.
2. **Green.** `csharp-expert` implements the tasks in task order, using the design's project, file and type names. Rules from the design: mutable options classes with `get; set;`, no `required` or `init` on bound types; `LibraryImport` with the System32 search-path attribute; `InvariantGlobalization` only under `src/`; no package version outside `Directory.Packages.props`; no new package without a design ADR; LF line endings; ASCII in sources and docs.
3. **Refactor and docs.** Keep tests green. Update every doc surface the story's "Docs impact" names in the same change (`technical-docs-writer` at haiku for prose).
4. **Quality bar, all four must pass, measured by an executor with exit codes.** `dotnet build -warnaserror -tl:off --no-incremental` exits 0 with `0 Warning(s)`; `dotnet format --verify-no-changes` exits 0; `dotnet test` with `AGENTJOULES_INTEGRATION` unset exits 0 with 0 failed; the secret scan of prompt REQ-029 returns no hits. Retry at most twice, then stop and report.
5. **Gates.** `code-review-gatekeeper` (opus) and `security-specialist` (opus) on the story's diff; `spec-compliance-reviewer` (opus) scoped to the story's Covers IDs. Resolve every BLOCKER and MAJOR; at most two rounds, then stop and report.
6. **Commit.** One commit per story: message `AJ-NNN REQ-xxx [EDGE-yyy]: <behaviour delivered>` (story ID first, then the IDs it covers; M7 stories without a REQ use the story ID alone). No `Co-Authored-By` or "Generated with" lines, ever. In the same commit, tick the story's definition-of-done boxes in the story file and append an entry to `docs/build-log.md`: what shipped, decisions, deviations from design or story with the reason, measured numbers with their labels, wall-clock minutes and sub-agent tokens for the story.
7. **Blocked story.** Write the reason to `docs/build-log.md`, mark the story blocked in the story file, continue with stories that do not depend on it, and report at the end. Never improvise a substitute.

## 5. Never

- Never `git push`; the user pushes after the exit report.
- Never create, resize or delete a cloud resource; the user creates and deletes the VM (prompt section 12, steps 10 to 13).
- Never print or store a key, a hostname or an IP in any file; keys reach the process from user-scope environment variables or `dotnet user-secrets` only.
- Never report an unverified pass: every "green" names the command and its integer exit code.
- Never exceed a prompt section 10 time box; when a box ends, stop that work, record it, continue elsewhere.

## 6. Velocity

Record for every story: the story's estimated hours, the wall-clock minutes from red to commit, and the sub-agent tokens consumed (from the Agent tool results). At the milestone exit compute delivered MUST hours per calendar day and compare with the "required on remaining" column of plan.md section 3.5. If the projection misses 2026-10-29 18:00 CET, name the cut-order steps from plan.md section 3.4 to apply, in order, and ask the user to confirm them before the next session.

## 7. Milestone exit

1. Run every command in the story file's "Dates and exit criteria" section through an executor and list the exit codes.
2. `spec-compliance-reviewer` (opus): full pass over the milestone's REQ and EDGE IDs; every item PASS.
3. `peer-review-critic` (opus) over `main..milestone/<Mk>`; resolve BLOCKER and MAJOR findings; the verdict line must read APPROVED.
4. Merge locally: `git switch main && git merge --no-ff milestone/<Mk>`. Do not push.
5. Append `## <Mk> exit` to `docs/build-log.md`: the merge commit hash, stories delivered and blocked, measured numbers with labels, velocity (MUST hours delivered, wall-clock, tokens, hours per calendar day), the projection against plan.md 3.5, the cut-order recommendation, the user actions due before the next milestone, and the exact kickoff line for the next session with the new base commit. Commit it on main with the message `<Mk> exit: <one line>`.
6. Final message to the user, standing alone: what shipped, exit criteria with exit codes, velocity numbers, decisions needed, the command `git push origin main`, and the next kickoff line.

## 8. Milestone notes

- **M0.** Tool checks first; the solution skeleton exactly as design section 2; empty-but-compiling test projects with the six REQ-023 class names; record `git rev-parse origin/main` and `HEAD` under `## Base commits` in `docs/build-log.md` (story AJ-001 and AJ-002).
- **M1.** The CI workflow is created here (REQ-024); runners have no GPU, so the fake NVML path must pass on both.
- **M2 and M3.** No live endpoint calls in unit tests; the deny-by-default CLI harness is the default; the dashboard stories AJ-089 to AJ-093 follow REQ-032 rules R1 to R6.
- **M4.** The NIM-local attempt has a 90-minute hard box; the measurement campaign runs on AC power with the dashboard closed; `results/` holds only publishable runs.
- **M5.** Starts only after the local tier's definition-of-done boxes are ticked; SSH only; the 4-hour and USD 30 box; the user creates and deletes the VM.
- **M6.** Content numbers trace to results files; demo runs write to `artifacts/demo-run`; the Devpost story, feedback and checklist are filled from committed files.
- **M7.** Release and submission steps are the user's; the session prepares and verifies.
