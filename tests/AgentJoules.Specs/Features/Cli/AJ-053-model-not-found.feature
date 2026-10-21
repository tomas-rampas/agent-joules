@unit @AJ-S053 @EDGE-008
Feature: AJ-053 Exit 4 before any completion call when a model ID is not listed
  @positive
  Scenario: AJ-053 all configured IDs listed passes
    Given a fake models response listing every configured model ID for tier "nvidia"
    When the model check runs
    Then the check passes
  @positive
  Scenario: AJ-053 available IDs are capped at 20 with Nemotron and Qwen first
    Given a fake models response with 30 IDs including 2 Nemotron and 1 Qwen entries
    And the configured ID is absent
    When the command runs
    Then the output lists 20 IDs
    And the Nemotron and Qwen IDs appear before the others
  @negative
  Scenario: AJ-053 missing model ID exits 4 with no completion call
    Given a fake models response that lacks the configured ID "missing-model"
    When "run --tier nvidia" is executed
    Then the exit code is 4
    And the output contains "missing-model" and at least one listed ID
    And the fake handler recorded 0 requests to the completions route
  @negative
  Scenario: AJ-053 per-agent model ID that is not listed exits 4
    Given AgentModelIds maps Writer to an ID absent from the fake models response
    When "run --tier nebius --dry-run" is executed
    Then the exit code is 4
    And the output contains that ID
  @negative
  Scenario: AJ-053 unreachable models endpoint exits 5
    Given a models endpoint that refuses the connection
    When "run --tier nvidia --dry-run" is executed
    Then the exit code is 5
