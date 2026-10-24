@unit @AJ-S104 @EDGE-009
Feature: Report Ollama failures with the exact fix command
  @positive
  Scenario: AJ-104 Ollama is up and the tag is present
    Given the fake Ollama endpoint answers /api/version and lists "nemotron-3-nano:30b"
    When I run "run --tier local --dry-run"
    Then the exit code is 0
    And the output contains "OK"
  @positive
  Scenario: AJ-104 the configured tag is found among many listed tags
    Given the fake endpoint lists 25 tags including "nemotron-3-nano:30b"
    When I run "run --tier local --dry-run"
    Then the exit code is 0
  @negative
  Scenario: AJ-104 Ollama refuses connections
    Given the fake endpoint refuses connections
    When I run "run --tier local --dry-run"
    Then the exit code is 5
    And the output contains the configured endpoint and "ollama serve"
  @negative
  Scenario: AJ-104 the endpoint times out
    Given the fake endpoint never answers within the configured timeout
    When I run "run --tier local --dry-run"
    Then the exit code is 5
    And the output contains "ollama serve"
  @negative
  Scenario: AJ-104 the tag is not pulled
    Given the fake endpoint lists tags that do not include "nemotron-3-nano:30b"
    When I run "run --tier local --dry-run"
    Then the exit code is 4
    And the output contains "ollama pull nemotron-3-nano:30b"
    And no request ended in "/chat/completions"
  @negative
  Scenario: AJ-104 another configured tag is named in the pull command
    Given Tiers:local:ModelId is "qwen3:8b" and the endpoint does not list it
    When I run "run --tier local --dry-run"
    Then the exit code is 4
    And the output contains "ollama pull qwen3:8b"
