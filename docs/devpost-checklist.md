# Agent Joules: Devpost submission checklist

Verified: 2026-10-26

This file records the verified hackathon rules, maps every Devpost field to a repository artifact and holds the commands the user runs. The builder fills no value that belongs to the user (Devpost handle, join date, credit date, video link, team, track) and runs none of the commands below except the git and gh steps delegated to the session at the M7 exit by the user's decision of 2026-10-27 12:11Z.

## Source and verification

- Hackathon page: https://nebiusglobalaihackathon.devpost.com/ (also /rules, /resources, /details/dates and /details/faqs under that address).
- Verified: 2026-10-26. The builder's fetch read five pages on 2026-10-26, the Overview, /rules, /resources, /details/dates and /details/faqs, and all five answered HTTP 200. The first fetch, on 2026-10-17, had returned HTTP 403, so the rules were then known only from the form the user showed on 2026-10-18 and from aggregator pages.
- Evidence classes used below: `verified` (quoted from the official pages on the date above), `user-stated, not verified` (from the form seen on 2026-10-18, not stated on the public pages), `aggregator, unverified` (from third-party pages only).

## Rules verification

The Rules tab text was read by the builder on 2026-10-26, so the user does not need to paste it. Items the public pages do not state keep the class `user-stated, not verified`.

## Rules

- Eligibility: individuals who are at least the age of majority where they reside, teams and organizations; excluded are Brazil, Quebec, Russia, Crimea, Cuba, Iran, North Korea and countries under OFAC sanctions; the sponsor is Nebius B.V. and the administrator is Devpost; rules page https://nebiusglobalaihackathon.devpost.com/rules, 2026-10-26 (evidence: verified)
- Required or judged technologies: "All submissions must run on either Nebius Token Factory or Nebius AI Cloud and use at least one NVIDIA open source model" (overview page https://nebiusglobalaihackathon.devpost.com/); Technological Implementation judges "Nebius Token Factory or AI Cloud model(s), and NVIDIA Nemotron or other NVIDIA open source models" (rules page); 2026-10-26 (evidence: verified)
- Submission fields: the pages list the working project, the track, the project description ("what you created, why you created it, and how it works"), the demo URL, the public YouTube video, the public repository URL with a README, the feedback section, the prior-work explanation and an optional city; the 200-character elevator pitch, the Built-with field of 25 tags, the project-name, team-members and Try-it-out fields are not on the public pages (evidence: user-stated, not verified)
- Video length limit: "less than three (3) minutes", public on YouTube, shows the project functioning, no third-party trademarks or copyrighted music without permission; rules page, 2026-10-26 (evidence: verified)
- License requirement: the project must be "open source by including an open source license file (such as Apache 2.0, MIT, or MPL 2.0)" visible in the About section; the word OSI does not appear on the pages, so reading it as an OSI-approved license is the builder's reading; this repository's license is MIT (LICENSE); rules page, 2026-10-26 (evidence: verified)
- Repository visibility requirement: a "public code repository ... on either GitHub, GitLab or Bitbucket" with "all necessary source code, assets, and instructions"; rules page, 2026-10-26 (evidence: verified)
- Judging criteria and weights: stage one is a pass/fail fit check; stage two scores four criteria, "equally weighted": Technological Implementation, Design, Potential Impact, Quality of the Idea; a tie is broken by the first criterion, then the next, then a judge vote; rules page, 2026-10-26 (evidence: verified)
- Tracks and selection rule: Coding and Agentic Engineering, Best Apps and Agents, Personal AI, Physical AI; "Pick the track that is the best fit for your project" (overview page https://nebiusglobalaihackathon.devpost.com/); "Identify which track you are submitting your Project into" (rules page); 2026-10-26 (evidence: verified)
- Builder Program or credit offers: USD 25 of Token Factory credits through the form on the Resources tab with the activation code `NEBIUS-DEVPOST-GLOBAL26` (link https://nebius.com/promo-code?utm_promo_event_code=2026-devpost-global-ai-hack&utm_promo_code_type=Token_Factory&utm_promo_activation_code=NEBIUS-DEVPOST-GLOBAL26); another USD 25 for joining the Builders Program (https://dev.nebius.com/builders); the FAQ (/details/faqs) states the credits are Token Factory credits only, with no separate AI Cloud credit grant; resources page and /details/faqs, 2026-10-26 (evidence: verified)
- Demo or test URL: "Provide a URL to a working demo, hosted application, or test build of your Project"; judges may judge from the text, images and video only; a private site needs credentials in the testing instructions; the project stays free to judges until judging ends; rules page, 2026-10-26 (evidence: verified)
- Required NVIDIA model families: "at least one NVIDIA open source model"; Nemotron is named and the FAQ allows other NVIDIA open models; overview page https://nebiusglobalaihackathon.devpost.com/ and /details/faqs, 2026-10-26 (evidence: verified)
- Required Nebius product: Token Factory or AI Cloud; the definition is a project that "makes a runtime call to the Token Factory inference API, or is deployed/run using Nebius AI Cloud compute (Serverless Jobs, Serverless Endpoints, or DevPods)" (rules page); a HuggingFace or OpenRouter call does not count (/details/faqs); 2026-10-26 (evidence: verified)
- Deadline with time zone: 2026-10-30 10:00 PDT (17:00 UTC), stated on the rules page as "Friday, October 30, 2026 (10:00 am Pacific Time)" and on the dates page (/details/dates) as "Oct 30, 2026 @ 10:00am PDT"; judging runs December 1 to December 15, 2026 and winners are announced on or around January 11, 2027; dates page, 2026-10-26 (evidence: verified)

## Conflicts

Four conflicts exist between the verified rules and the prompt; the verified rule wins in each.

1. AI Cloud compute definition. The rules define AI Cloud compute as Serverless Jobs, Serverless Endpoints or DevPods. The cloudgpu tier ran on a plain Nebius AI Cloud GPU virtual machine (docs/cloudgpu-run.md), which the definition does not list. Resolution: the platform rule is met by the runtime Token Factory calls of the nebius tier (results/20261025T133440Z-nebius.json); the virtual machine is reported as an additional Nebius service, not as the qualifying one.
2. Video length. The overview page says "3-minute or shorter"; the Official Rules (rules page) say "less than three (3) minutes". Resolution: the Official Rules prevail (Official Rules section 11.4); the demo script plans 165 seconds (content/demo-script.md), under both limits.
3. License wording. The prompt says "OSI license"; the rules say "open source license file (such as Apache 2.0, MIT, or MPL 2.0)". Resolution: MIT satisfies both.
4. Technological Implementation wording. The Official Rules (rules page) say "or other NVIDIA open source models"; the overview page names Nemotron only in its judging line. Resolution: the Official Rules prevail (Official Rules section 11.4); no change is needed, the project uses Nemotron.

No other conflict was found on 2026-10-26.

## Fields to artifacts

- [ ] project name -> README.md (line 1, the title Agent Joules)
- [ ] elevator pitch (200 characters) -> docs/devpost-pitch.md candidate A (text in Elevator pitch below)
- [ ] About the project -> docs/devpost-story.md
- [ ] Built-with tags (25 maximum) -> docs/devpost-checklist.md section Built-with
- [ ] Try it out links -> https://tomas-rampas.github.io/agent-joules/report/ and https://github.com/tomas-rampas/agent-joules (user) (confirm after Pages is enabled) (2026-10-28: the site root https://tomas-rampas.github.io/agent-joules/ is a landing page for the judges that links to the report; the demo URL stays /report/ by the user's decision of 2026-10-28) (2026-10-28: the hosted replay https://tomas-rampas.github.io/agent-joules/replay/ is a third link for the Try it out field; the demo URL stays /report/)
- [ ] YouTube video link -> (user)
- [ ] track -> Coding and agentic engineering recommended, the user decides, see Tracks (user)
- [ ] team members -> (user)
- [ ] repository URL -> https://github.com/tomas-rampas/agent-joules (user) (the publication of main is the session's at the M7 exit on the user's delegation of 2026-10-27 12:11Z, see User actions)
- [ ] OSI license -> LICENSE
- [ ] README with setup instructions -> README.md (section Quick start)
- [ ] Nemotron usage highlights -> README.md section `How this project uses NVIDIA Nemotron`
- [ ] Token Factory highlights -> README.md section `Where Nebius Token Factory accelerated the workflow`
- [ ] other Nebius tools -> README.md section `Other Nebius tools or services used`
- [ ] Nemotron and Nebius feedback questionnaire -> docs/devpost-feedback.md
- [ ] prior-work explanation -> docs/build-log.md section `Base commits` (the repository was created 2026-10-18, inside the submission period; no prior project)

## Built-with

Tags in the order of the Built-with field (25 at most):

- C#
- .NET
- Microsoft Agent Framework
- NVIDIA Nemotron
- Nebius Token Factory
- NVML
- Nebius AI Cloud
- NVIDIA NIM
- Microsoft.Extensions.AI
- OpenTelemetry
- Reqnroll
- GitHub Actions
- GitHub Pages

Tags: 13

`Nebius AI Cloud` is listed because a completed cloudgpu run file exists: results/20261026T124721Z-cloudgpu.json.

## Demo URL

Demo URL: https://tomas-rampas.github.io/agent-joules/report/ because the static report page served by GitHub Pages from the docs folder of main satisfies "a working demo, hosted application, or test build".

The hosted replay page https://tomas-rampas.github.io/agent-joules/replay/ (the live dashboard replaying the committed cloudgpu and local runs in the browser, no key and no server) is a second satisfier of "a working demo, hosted application, or test build", after the report page. The Pages address is inferred from the standard project-pages pattern; the user confirms it after Pages is enabled (see User actions). Fallbacks, in order: the v0.2.0 release (the submitted release), then the public video URL.

## Elevator pitch

Rule of AJ-141: `grep -lE '"status": "(completed|partial)"' results/*Z-cloudgpu.json` prints results/20261026T124721Z-cloudgpu.json (decided 2026-10-26), so candidate A applies, not A-local. The GPU name in that file is `NVIDIA H100 80GB HBM3`, which lacks L40S, so the GPU name NVIDIA H100 80GB HBM3 replaced the L40S (the K3 amendment allows the full name; the first `[A-Z]+[0-9]+[A-Z]*` token of it is H100; done by AJ-129 on 2026-10-26; the change is recorded in docs/build-log.md).

```
Which agent step burns most energy? .NET middleware meters joules per Microsoft Agent Framework step on your GPU and Nebius AI Cloud NVIDIA H100 80GB HBM3; compares cost to Nemotron on Token Factory.
```

Characters: 199

Short tagline row (58 characters, docs/devpost-pitch.md `## Tagline (60 characters)`): Agent Joules: energy per agent step, measured on your GPUs

## Registration

Devpost handle: <user fills>
Joined on: <user fills>
Team: solo unless stated
Credits requested on: <user fills>

## Tracks

The form offers four tracks:

- Coding and agentic engineering
- Best apps and agents
- Personal AI
- Physical AI

The rules page states the goal of the two candidate tracks:

- Coding and Agentic Engineering: "Build coding agents and developer tools: agents that write, run, and test code in Token Factory."
- Best Apps and Agents: "Build any app or agent someone would actually use... Power it with Nemotron models on Nebius through Token Factory."

Stage One is a pass/fail test of "a genuine attempt at the track's stated goal", so the track choice can fail the project before scoring.

Recommendation: Coding and agentic engineering. Reason: the project is developer tooling, a .NET middleware and CLI that instruments agent workflows and reports per-step energy, tokens, latency and cost. Fit risk: the demo tasks (incident-brief, api-migration, energy-explainer) are not coding agents, so the fit to Coding and Agentic Engineering rests on Agent Joules being a developer tool for agent workflows, not on a coding agent. Best apps and agents fits an app that measures agents and is the lower-risk reading of its goal text. The user weighs this risk and decides.

Final pick: the user decides

## Judging criteria

Equal weights, verified 2026-10-26 on the rules page (researched 2026-10-17 from aggregator pages before that). Each criterion maps to a README section or artifact:

- Technological Implementation -> README.md sections `How this project uses NVIDIA Nemotron`, `Where Nebius Token Factory accelerated the workflow` and `Architecture`
- Design -> README.md section `CLI reference`, docs/report/index.html and the dashboard (`--dashboard`)
- Potential Impact -> README.md section `What it measures` and content/article.md
- Quality of the Idea -> README.md line 3 (the tagline) and section `Prior art`

## Credits

- USD 25 of Token Factory credits through the form on the Resources tab, with the activation code `NEBIUS-DEVPOST-GLOBAL26` (link in the Rules item on credits). User action.
- Another USD 25 for joining the Nebius Builders Program (https://dev.nebius.com/builders). User action.
- Both are Token Factory credits and do not apply to Nebius AI Cloud: the virtual machine was paid from the user's AI Cloud balance (docs/cloudgpu-run.md, section Cost).

## Feedback form, user fills

Seven answers need the user's own opinion. Each is a placeholder in docs/devpost-feedback.md:

- Q2: my rating from 1 to 10 and notes on the Nemotron output, in docs/devpost-feedback.md
- Q4: the other models I used for similar tasks outside this project, in docs/devpost-feedback.md
- Q5: the most valuable Nebius capability and how, in docs/devpost-feedback.md
- Q6: my likelihood to recommend, from 1 to 10, and why, in docs/devpost-feedback.md
- Q7: my rating from 1 to 10 and the comparison with environments I used before, in docs/devpost-feedback.md
- Q8: the features or improvements I wanted while building, in docs/devpost-feedback.md
- Q9: what I most hope to see from the Nemotron team next, in docs/devpost-feedback.md

## User actions

Screenshot (user action, decision 7 of the M6 exit; the README alt text says "Live dashboard during the demo rehearsal"): run the live rehearsal run of README step 9 with the NIM container up, `dotnet run --project src/AgentJoules.Cli -- run --tier local --task energy-explainer --repeat 1 --out artifacts/demo-run --dashboard` (it writes outside results/), or, when the container is down, the replay `dotnet run --project src/AgentJoules.Cli -- dashboard --replay results/20261027T095519Z-local.json` (the page then shows the replay banner), open the printed Dashboard address in a browser set to the dark theme (the page default, rule R6), capture the page at about 1280 px width and save it as `docs/report/dashboard.png`; then `dotnet run --project src/AgentJoules.Cli -- report --in results/ --out docs/report/ --update-readme` adds the README link, and Definition of Done box 1 (REQ-032) is ticked.

Before you push (or before the session's publication of main at the M7 exit, on the user's delegation), check:

- `dotnet run --project src/AgentJoules.Cli -- report --verify-readme` exits 0
- `git grep -nE "nvapi-[A-Za-z0-9_-]{8,}"` returns nothing
- `git status --short` is empty

Push the branch:

User action delegated to the session at the M7 exit (the user's decision of 2026-10-27 12:11Z):

```
git push -u origin main
```

After the push, check the CI run on main that the push starts (REQ-024; the run is not a Definition of Done item; the builder ran the same read-only commands on the earlier run 40426079785):

Read-only check (the user runs it after the push; the builder may run it too):

```
gh run list --workflow ci.yml --branch main --limit 1 --json status,conclusion,databaseId
gh run view <id> --json jobs --jq '.jobs[] | [.name,.conclusion]'
```

Both runners (`build (windows-latest)` and `build (ubuntu-latest)`) must show `success`. If a runner is red: paste the failing step's name and its last lines to the builder; the builder records the finding in `docs/build-log.md`, prepares the fix as a local commit for you to push, and the submission does not wait for it (REQ-024 says the first run is not a Definition of Done item).

Enable GitHub Pages for the report (the site now serves the landing page, the report and the hosted replay) (Settings, Pages, Deploy from a branch, branch `main`, folder `/docs`), or use the command, then check the address:

User action delegated to the session at the M7 exit (the user's decision of 2026-10-27 12:11Z):

```
# Settings > Pages > Build and deployment > Source: Deploy from a branch > Branch: main, folder: /docs
gh api -X POST repos/tomas-rampas/agent-joules/pages -f build_type=legacy -f "source[branch]=main" -f "source[path]=/docs"
curl -sI https://tomas-rampas.github.io/agent-joules/report/ | head -1
```

Create the release. The notes file docs/release-notes-v0.1.0.md and the three zip assets are produced by M7 (AJ-157); do not run this before they exist.

User action delegated to the session at the M7 exit (the user's decision of 2026-10-27 12:11Z):

```
gh release create v0.1.0 --repo tomas-rampas/agent-joules --target main --title "Agent Joules v0.1.0" --notes-file docs/release-notes-v0.1.0.md artifacts/agent-joules-v0.1.0-fdd.zip artifacts/agent-joules-v0.1.0-results.zip artifacts/agent-joules-v0.1.0-report.zip
```

After the release exists, check that it holds exactly the three assets (the command prints three names):

User action delegated to the session at the M7 exit (the user's decision of 2026-10-27 12:11Z):

```
gh release view v0.1.0 --repo tomas-rampas/agent-joules --json assets --jq '.assets[].name'
```

The release is created after the final publication of main (the session's publication of main at the M7 exit, on the user's delegation). The tag marks the release snapshot created at the M7 exit; the commits that follow it (the dashboard screenshot, a Demo URL change, a video-cut note) are documentation-only; to move the tag to the final submitted commit the user runs `git tag -f -a v0.1.0 -m "Agent Joules v0.1.0" <commit>`, `git push -f origin v0.1.0`, and, if docs/report changed, rebuilds the report zip and runs `gh release upload v0.1.0 artifacts/agent-joules-v0.1.0-report.zip --clobber`. (2026-10-27 after the exit: superseded; v0.1.0 stays at the M7 exit snapshot 98cc246 and the submitted commit is marked by v0.2.0, see the next paragraph.) The user keeps the repository unchanged on the submitted commit (the tagged commit or, after a documentation-only commit such as the screenshot, the commit the tag is moved to) until the results are announced (prompt section 12, step 9); a later commit would move main away from the submitted commit.

2026-10-27 (after the exit): the user instructed the session "move it to 0.2.0". The orchestrator measured on 2026-10-27 that the v0.1.0 release marks the M7 exit snapshot 98cc246 (tag object 8d28d83) with its three assets, and that three commits followed that changed no shipped code (5aa3a1f the M7 exit addendum record, 01dfcf4 the dashboard screenshot and its Specs report fixture, 8bb1bf0 the README cloudgpu bullet). The session created the tag v0.2.0 (object b39f487) at 8bb1bf0 and the release https://github.com/tomas-rampas/agent-joules/releases/tag/v0.2.0 at 15:55Z, with assets rebuilt from 8bb1bf0 (the sizes and hashes are in docs/build-log.md, the M7 exit addendum). Earlier, at 15:50Z, v0.1.0 had been moved to 8bb1bf0 and its report zip re-uploaded; a side note found that the v0.1.0 fdd zip then reported the stale hash 98cc246, so v0.1.0 was restored to its original tag object 8d28d83 at 98cc246 and its report zip rebuilt with the original three entries. The submitted release is v0.2.0; its notes file is docs/release-notes-v0.2.0.md. The verification command is `gh release view v0.2.0 --repo tomas-rampas/agent-joules --json assets --jq '.assets[].name'`, which lists the three assets. Plan, not done: after the commit that adds the notes file, the session moves v0.2.0 to that commit (the final submitted commit), rebuilds the fdd zip from it (the version string carries the commit hash), re-uploads it with --clobber and updates the release body from the notes file; the final hashes are recorded in the GitHub release body, not in the repository, so main stays on the submitted commit.

## Submission order

The twelve fields below follow the order of the Devpost form. Every step is the user's own action, and each one names the artifact it takes its text from.

1. Enter the project name, Agent Joules (README.md line 1) (user)
2. Paste the elevator pitch of 200 characters at most, the text of docs/devpost-pitch.md candidate A (199 characters; the text is in Elevator pitch above) (user)
3. Paste the About the project text from docs/devpost-story.md (user)
4. Pick the Built-with tags, 25 at most, from the list in the Built-with section of this checklist (13 tags) (user)
5. Fill the Try it out links with the `Demo URL:` line of this checklist and the repository https://github.com/tomas-rampas/agent-joules (user)
6. Paste the public YouTube video link, `<user fills>` (the video is recorded and uploaded by the user, see Timeline) (user)
7. Select the track, the user's pick; the recommendation is in Tracks (Coding and agentic engineering) (user)
8. Add the team members, from the Registration section (solo unless stated) (user)
9. Enter the repository URL, https://github.com/tomas-rampas/agent-joules, with the MIT license (LICENSE, read as an OSI-approved license) and the README highlights (the sections on Nemotron, Token Factory and the other Nebius tools, listed in Fields to artifacts) (user)
10. Answer the Nemotron and Nebius feedback questionnaire from docs/devpost-feedback.md, whose seven user placeholders are listed in Feedback form, user fills (user)
11. Review every field once more and submit the project on Devpost before the deadline of 2026-10-30 10:00 PDT (17:00 UTC) (user)
12. After submission, keep the repository unchanged on the submitted commit and confirm that the v0.2.0 tag marks it (see User actions; v0.1.0 marks the M7 exit snapshot 98cc246) (user)

## Cut-off plan

If an item is missing, submit what exists and regenerate every surface that quotes it, so that no sentence claims a file that is absent. In this table `AJ` stands for `dotnet run --project src/AgentJoules.Cli --`. The regeneration is always the pair `AJ report --in results/ --out docs/report/ --update-readme --render-content`, then `AJ report --verify-readme` (exit 0). A step of the pair that the row does not need reads `none`. A row whose regeneration cell reads none needs no generator run; its files are edited by hand. The fallback story is the story whose acceptance criteria define the fallback. The boxes are the numbered Definition of Done boxes of the prompt (7 and 20: run files per tier; 8: README results block; 21 to 23: cloudgpu files, cost and teardown, README row).

| Missing item | What is submitted | Files that change | Regeneration commands | Fallback story | Boxes unchecked |
|---|---|---|---|---|---|
| cloudgpu tier (no completed or partial `results/*Z-cloudgpu.json`) | README row `cloudgpu \| not run`; pitch A-local (docs/devpost-pitch.md, 192 characters); the singular tagline; the Built-with list without `Nebius AI Cloud`; the README sentence `None besides Token Factory.`; no cloudgpu sentence in the story, posts, article or script. The platform rule is met through the nebius run file. | README.md (tagline, results block, the Nebius tools sentence); docs/devpost-pitch.md and the Elevator pitch section here (pitch A-local); the Built-with list here; `content/linkedin-posts.template.md` (its cloudgpu sentence, line 25, is replaced by the A-local wording before the regeneration, because the renderer exits 2 on unresolved cloudgpu placeholders when no cloudgpu file exists); docs/devpost-story.md, content/article.md and content/demo-script.md (the cloudgpu sentences removed); docs/devpost-feedback.md and docs/release-notes-v0.1.0.md (their cloudgpu sentences and figures removed, and the zip assets are rebuilt); generated: docs/report/index.html, docs/report/summary.md, docs/report/joules-per-step.svg, content/linkedin-posts.md | `AJ report --in results/ --out docs/report/ --update-readme --render-content`, then `AJ report --verify-readme` | AJ-129 (tier consequences), AJ-151 (the cloudgpu absent record), AJ-133 (the not-run row) | 21, 22 and the substance of 23 |
| local tier (no local run file) | BLOCKED on this item: the local run file is part of the minimum submission (build log, Never dropped). Only if the user decides to submit anyway: README row `local \| not run`, the energy claims resting on the cloudgpu row alone, and every laptop figure removed from the story, posts, article and script | README.md (tagline, results block); docs/devpost-pitch.md; docs/devpost-checklist.md (the Elevator pitch section: the candidate A copy and the short tagline row); docs/devpost-story.md; content/article.md; content/demo-script.md; docs/release-notes-v0.1.0.md; docs/devpost-feedback.md; docs/methodology.md; content/linkedin-posts.template.md (its local placeholders on lines 5, 14, 16 and 25 are replaced or removed before the regeneration, because the renderer exits 2 on an unresolved local placeholder when no local file exists); generated: docs/report/index.html, docs/report/summary.md, docs/report/joules-per-step.svg, content/linkedin-posts.md; the zip assets are rebuilt | `AJ report --in results/ --out docs/report/ --update-readme --render-content`, then `AJ report --verify-readme` | AJ-129 (tier consequences), AJ-133 (the not-run row) | 7 and 20 for the local tier; 8 until the block is regenerated |
| nvidia tier (no nvidia run file) | README row `nvidia \| not run`; no nvidia figure in the story, posts, article or script | README.md (results block); docs/devpost-story.md; content/article.md; content/demo-script.md; docs/release-notes-v0.1.0.md; docs/devpost-feedback.md; docs/methodology.md; generated: docs/report/index.html, docs/report/summary.md, docs/report/joules-per-step.svg, content/linkedin-posts.md | `AJ report --in results/ --out docs/report/ --update-readme --render-content`, then `AJ report --verify-readme` | AJ-129 (tier consequences), AJ-133 (the not-run row) | 7 and 20 for the nvidia tier |
| nebius tier (no nebius run file) | BLOCKED. The hard rule: the Token Factory run is a hackathon requirement, so without a nebius run file the build is reported BLOCKED on that item and no submission is made with a claim the files do not support. When nebius is missing and a cloudgpu run file exists, the platform rule is met through Nebius AI Cloud and the nebius row reads `not run`. When both are missing, the user decides, with the missing key, URL or model named. | README.md (results block, Token Factory section); docs/devpost-story.md; content/article.md; content/demo-script.md; docs/release-notes-v0.1.0.md; docs/devpost-feedback.md; docs/methodology.md; docs/devpost-checklist.md (the Conflicts item 1 resolution and the Elevator pitch section; the Built-with tag `Nebius Token Factory` stays because the tier client integrates Token Factory in the code, and the row says that the tier did not run); docs/devpost-pitch.md (candidate A's Token Factory clause); content/linkedin-posts.template.md (its nebius sentence on line 27 is replaced or removed before the regeneration, because the renderer exits 2 on an unresolved nebius placeholder when no nebius file exists); generated: docs/report/index.html, docs/report/summary.md, docs/report/joules-per-step.svg, content/linkedin-posts.md; the zip assets are rebuilt | `AJ report --in results/ --out docs/report/ --update-readme --render-content`, then `AJ report --verify-readme` | AJ-129 (tier consequences), AJ-133 (the not-run row) | 7 and 20 for the nebius tier |
| judge (no judge score for some or all steps) | The run files as recorded: the quality of an unjudged step is `N/A` with the reason, never 0, and no joules-per-quality-point figure is claimed for it; the README disclosure of the judge coverage stays. The committed local file already holds 3 steps recorded N/A after RetriesExhaustedException. | docs/devpost-story.md and content/article.md (the quality sentences, if they quote an unjudged step); the README judge disclosure only if the run files change | none (the run files are not edited); if a run file is replaced, `AJ report --in results/ --out docs/report/ --update-readme --render-content`, then `AJ report --verify-readme` | AJ-129 (tier consequences) | none |
| Pages not live | The Demo URL falls back to the v0.2.0 release, then to the public video URL (prompt section 3); the user pastes the real address into the Try it out field | docs/devpost-checklist.md (the `Demo URL:` line); content/demo-script.md (line 54, the Try it out shot shows the Pages URL; shot 9 is in the minimal cut) | none | AJ-158 (this table; risk RK-2) | none |
| video not ready | The minimal cut: shots 1, 3, 6 and 9 of content/demo-script.md, planned 65 s (12 + 13 + 30 + 10), uploaded as a public YouTube video and noted here. A missing video is the only item for which no fallback exists. Target: public by 2026-10-30 07:00 UTC, latest 2026-10-30 11:00 UTC. | docs/devpost-checklist.md (a note of the cut, if used) | none | AJ-136 (the minimal cut) | none |
| feedback placeholders unfilled | The seven `owner: user` placeholders of docs/devpost-feedback.md stay unfilled and are listed in the final report; the user answers the questionnaire in the form, and no opinion is written for the user | none | none | AJ-142 (feedback placeholders), AJ-139 (user actions) | none |

## Timeline

Planning targets (assumed; one calendar, plan.md decisions D-6 and D-7). Every time is in UTC with the PDT equivalent in parentheses (PDT is UTC minus 7 hours). From the M7 exit the git and gh steps (the publication of main, the v0.1.0 tag, the release, the Pages source) are the session's on the user's delegation of 2026-10-27 12:11Z; the Devpost form, the video and the questionnaire are the user's. The build reached M7 on 2026-10-27, two days early, so these targets are upper bounds and the user may act earlier.

- Content freeze: 2026-10-29 17:00 UTC (10:00 PDT)
- M7 gate passed and main published (the session, on the user's delegation): 2026-10-29 20:00 UTC (13:00 PDT)
- Pages and the v0.1.0 release live: 2026-10-29 21:00 UTC (14:00 PDT) (done 2026-10-27: the Pages source set 13:17:04Z and /report/ answering 200 at 13:21:14Z, the v0.1.0 release 13:21Z, the v0.2.0 release 15:55Z)
- Video recorded on 2026-10-29 and public by 2026-10-30 07:00 UTC (00:00 PDT)
- Submission complete by 2026-10-30 14:00 UTC (07:00 PDT)
- The deadline: 2026-10-30 17:00 UTC (10:00 PDT)

The latest acceptable targets, used only when the evening plan slips:

- Main published by 2026-10-30 07:00 UTC (00:00 PDT)
- Pages and the release live by 2026-10-30 08:00 UTC (01:00 PDT)
- Video public by 2026-10-30 11:00 UTC (04:00 PDT)
- Submission complete by 2026-10-30 14:00 UTC (07:00 PDT)

3 hours of buffer remain between the last target and the deadline. For a user in central Europe the local time on that date is UTC+1 (CET), so the deadline is 18:00 CET and the last target is 15:00 CET.

Delegation (2026-10-27 12:11Z): the user asked the session to run the git and gh steps on their behalf, so the publication of main, the v0.1.0 tag, the GitHub release with the three assets and the Pages source setting are performed by the session at the M7 exit after the CI run on the merged main succeeds, each recorded with its command and exit code in docs/build-log.md; the Devpost form, the video and the questionnaire stay the user's. On the user's instruction of 2026-10-27 ("move it to 0.2.0") the session also created the v0.2.0 tag and release (15:55Z).
