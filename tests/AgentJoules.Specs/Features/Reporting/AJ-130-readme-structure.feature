@unit @AJ-S130 @REQ-025
Feature: AJ-130 README pitch and architecture diagram
  @positive
  Scenario: AJ-130 README carries a valid Mermaid architecture block
    Given the committed README.md
    When the Mermaid block is extracted
    Then exactly one block exists and its first line is "flowchart LR"
    And it names agents, metered clients, providers, the NVML meter, the sink, results, the report and OpenTelemetry
  @positive
  Scenario: AJ-130 README pitch equals the tagline in the pitch file
    Given the committed README.md and docs/devpost-pitch.md and a completed cloudgpu run file
    When line 3 of the README is compared with the Tagline section
    Then both read "Agent Joules: energy per agent step, measured on your GPUs"
    And the text is 58 characters
  @positive
  Scenario: AJ-130 README uses the singular tagline when the cloudgpu tier did not run
    Given the committed README.md and no completed or partial cloudgpu run file
    When line 3 of the README is checked
    Then it reads "Agent Joules: energy per agent step, measured on your GPU"
    And the change is logged in docs/build-log.md
  @negative
  Scenario: AJ-130 README without a Mermaid fence is rejected
    Given a copy of the README with the mermaid fence line removed
    When the README structure check runs
    Then it fails with "missing mermaid block"
  @negative
  Scenario: AJ-130 README with a missing node in the diagram is rejected
    Given a copy of the README whose diagram has no OpenTelemetry node
    When the README structure check runs
    Then it fails with "diagram lacks: OpenTelemetry"
  @negative
  Scenario: AJ-130 README with a missing Architecture heading is rejected
    Given a copy of the README without the heading "## Architecture"
    When the README structure check runs
    Then it fails with "missing heading: Architecture"
  @negative
  Scenario: AJ-130 README with the old status text is rejected
    Given a copy of the README that still says "Filled during the build" under Architecture
    When the README structure check runs
    Then it fails with "leftover placeholder text: Filled during the build"