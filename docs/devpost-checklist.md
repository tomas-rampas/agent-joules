# Agent Joules: Devpost submission checklist

Verified: 2026-10-26

This file records the verified hackathon rules, maps every Devpost field to a repository artifact and holds the commands the user runs. The builder fills no value that belongs to the user (Devpost handle, join date, credit date, video link, team, track) and runs none of the commands below.

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
- [ ] Try it out links -> https://tomas-rampas.github.io/agent-joules/report/ and https://github.com/tomas-rampas/agent-joules (user) (confirm after Pages is enabled)
- [ ] YouTube video link -> (user)
- [ ] track -> Coding and agentic engineering recommended, the user decides, see Tracks (user)
- [ ] team members -> (user)
- [ ] repository URL -> https://github.com/tomas-rampas/agent-joules (user) (the push is a user action, see User actions)
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

The Pages address is inferred from the standard project-pages pattern; the user confirms it after Pages is enabled (see User actions). Fallbacks, in order: the v0.1.0 release, then the public video URL.

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

Before you push, check:

- `dotnet run --project src/AgentJoules.Cli -- report --verify-readme` exits 0
- `git grep -nE "nvapi-[A-Za-z0-9_-]{8,}"` returns nothing
- `git status --short` is empty

Push the branch:

User action (the builder never runs this):

```
git push -u origin main
```

Enable GitHub Pages for the report (Settings, Pages, Deploy from a branch, branch `main`, folder `/docs`), or use the command, then check the address:

User action (the builder never runs this):

```
# Settings > Pages > Build and deployment > Source: Deploy from a branch > Branch: main, folder: /docs
gh api -X POST repos/tomas-rampas/agent-joules/pages -f build_type=legacy -f "source[branch]=main" -f "source[path]=/docs"
curl -sI https://tomas-rampas.github.io/agent-joules/report/ | head -1
```

Create the release. The notes file docs/release-notes-v0.1.0.md and the three zip assets are produced by M7 (AJ-157); do not run this before they exist.

User action (the builder never runs this):

```
gh release create v0.1.0 --repo tomas-rampas/agent-joules --target main --title "Agent Joules v0.1.0" --notes-file docs/release-notes-v0.1.0.md artifacts/agent-joules-v0.1.0-fdd.zip artifacts/agent-joules-v0.1.0-results.zip artifacts/agent-joules-v0.1.0-report.zip
```

## Submission order

The twelve-step order is written in M7 (AJ-158).
