# Agent Joules: Devpost elevator pitch

## Devpost elevator pitch (limit 200 characters)

**A (recommended)** (194 characters)

> Which agent step burns the most energy? A .NET middleware meters joules per Agent Framework step on your GPU and a Nebius AI Cloud H100, then compares cost with NVIDIA Nemotron on Token Factory.

If the cloudgpu run file names a GPU other than the H100 (the L40S fallback), the builder replaces the GPU name in this text and records the change in docs/build-log.md, as REQ-028 states; "L40S" has the same length as "H100", so the count does not change.

Why recommended: it ties with B on total and beats B on hackathon alignment, because it also names the middleware and NVIDIA, the sponsor on the gallery card. It packs middleware, .NET, Agent Framework, NVIDIA Nemotron and both Nebius products (AI Cloud and Token Factory) behind a question hook, which C's weaker hook does not match. Energy stays on the two GPUs the user controls; the hosted comparison stays on cost. REQ-028 maps the Devpost tagline field to candidate A, so the label stays.

**A-local (fallback)** (192 characters)

> Which agent step burns the most energy? A .NET middleware meters local GPU joules per Microsoft Agent Framework step and compares cost and latency with NVIDIA Nemotron on Nebius Token Factory.

Use it only when the cloudgpu tier produced no committed run file, as REQ-028 of the prompt states.

**B** (195 characters)

> Your agents report tokens, not energy. Agent Joules meters joules per .NET Microsoft Agent Framework step on your GPU and a Nebius AI Cloud H100, and compares cost with Nemotron on Token Factory.

**C** (196 characters)

> A power meter for AI agents: .NET middleware measures GPU energy per Microsoft Agent Framework step while NVIDIA Nemotron runs on your laptop and a Nebius AI Cloud H100, vs. cost on Token Factory.

## Spoken version (30 seconds)

(84 words)

> Your agents report tokens, not energy. Which step burns the most power? Agent Joules is a .NET middleware for Microsoft Agent Framework that uses NVIDIA's GPU management library to attribute joules, watts, tokens, latency, cost and a quality score to every agent step. The same workflow runs NVIDIA Nemotron on a laptop, on a Nebius AI Cloud H100, and on hosted APIs including Nebius Token Factory. Energy appears only where it is measured: on the laptop GPU and on the Nebius AI Cloud GPU.

## Tagline (60 characters)

(58 characters)

> Agent Joules: energy per agent step, measured on your GPUs

Plural "GPUs" because energy is now measured on two GPUs the user controls, the laptop and the rented H100, and the singular would read as laptop-only.

## Rubric and scores

Rubric fixed before scoring. Payoff (who benefits, why it matters) is scored under hackathon alignment because it feeds the Potential Impact criterion.

| Criterion | Weight | A | A-local | B | C |
|---|---|---|---|---|---|
| Hook strength in the first eight words | 20 | 19 | 19 | 19 | 17 |
| Concreteness of mechanism | 20 | 18 | 19 | 18 | 19 |
| Hackathon alignment | 20 | 19 | 18 | 18 | 19 |
| Honesty and defensibility | 20 | 19 | 19 | 19 | 19 |
| Length compliance | 10 | 10 | 10 | 10 | 10 |
| Readability for a non-specialist | 10 | 8 | 8 | 9 | 8 |
| **Total** | **100** | **93** | **93** | **93** | **92** |

A-local keeps its scores from the previous round; its text is unchanged.

| Candidate | Trajectory | Stop reason |
|---|---|---|
| A | 90, 93, 93, 93 | Plateau: iteration one lacked Nemotron (alignment 16); adding "with Nemotron on Token Factory" fixed it. Iteration three tried "cost and latency" and "NVIDIA Nemotron" for concreteness; both exceed 200 characters. Iteration four scored V1 (92) and V2 (93) against the iteration-three text (93); V2 tied and was adopted under the tie rule that prefers the text containing "NVIDIA". Gain zero |
| B | 90, 93, 93 | Plateau: iteration one lacked Nemotron and middleware (alignment 16); adding Nemotron fixed part. Iteration three tried "Agent Joules middleware"; it exceeds 200 characters |
| C | 92, 92 | Plateau: iteration one scored hook 17, but a question or a claim hook duplicates A or B. The next weakest, honesty ("NVIDIA Nemotron energy" attributes GPU energy to a model), was rewritten to "GPU energy ... while NVIDIA Nemotron runs"; honesty rose one point and alignment fell one (Nebius no longer adjacent to Token Factory). Gain zero |

Candidate A, iteration four (same rubric):

| Criterion | Weight | Iteration three | V1 | V2 (adopted) |
|---|---|---|---|---|
| Hook strength in the first eight words | 20 | 19 | 18 | 19 |
| Concreteness of mechanism | 20 | 18 | 18 | 18 |
| Hackathon alignment | 20 | 19 | 20 | 19 |
| Honesty and defensibility | 20 | 19 | 19 | 19 |
| Length compliance | 10 | 10 | 10 | 10 |
| Readability for a non-specialist | 10 | 8 | 7 | 8 |
| **Total** | **100** | **93** | **92** | **93** |

V1 (197 characters): "Which agent step burns most energy? .NET middleware meters joules per Microsoft Agent Framework step on your GPU and a Nebius AI Cloud H100, and compares cost with NVIDIA Nemotron on Token Factory." It names every signal (alignment 20), but dropping "the" and "A" weakens the hook and readability. V2 gains "NVIDIA" and loses "Microsoft", so alignment stays 19.

Points still open: A drops "Microsoft" from the framework name; "Agent Framework" is Microsoft's own short form, but a non-specialist may read it as a generic phrase. B omits "NVIDIA" (the H100 implies it). A and B drop "latency", which the four-tier wording pushed past the limit. A names no beneficiary (the question implies one). B omits "middleware". C uses the abbreviation "vs." and leaves the comparison target terse. Every four-tier candidate depends on the cloudgpu tier producing a committed run file; if it does not, A-local replaces A.

Sources: agent-joules.prompt.md, Layer 1 sections 1 (Mission and audience), 2 (addendum of 2026-10-18), 3 (Hackathon alignment), 5 (Model tiers), 6 (Honesty rules), REQ-028, REQ-031, and the Layer 2 Objective.
