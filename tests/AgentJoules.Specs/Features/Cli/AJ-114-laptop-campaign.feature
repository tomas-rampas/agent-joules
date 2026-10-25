@integration @AJ-S114 @REQ-018 @REQ-020 @REQ-021
Feature: Run the laptop measurement campaign
  @positive
  Scenario: AJ-114 the full campaign writes three completed run files
    Given the preflight passed, the keys are set and Ollama serves the local model
    When I run "run --tier all --repeat 3"
    Then the exit code is 0
    And one completed run file exists for each of "local", "nvidia" and "nebius"
    And each file has 3 tasks, run.repeat 3 and 36 steps
  @positive
  Scenario: AJ-114 the local file carries warm-up, baseline and runtime evidence
    Given the campaign completed
    When I read the latest local run file
    Then baseline.seconds is at least 30
    And environment.modelLoadSeconds and environment.gpuOffloadPercent are numbers
    And environment.powerSource is one of "ac", "battery" and "unknown"
  @positive
  Scenario: AJ-114 hosted steps carry no energy values
    Given the campaign completed
    When I read the latest nvidia and nebius run files
    Then every step has grossJoules and netJoules with label "N/A" and a null value
  @positive
  Scenario: AJ-114 the committed files validate
    Given the three run files are in results/
    When I run "validate results/"
    Then the exit code is 0
    And the output contains "Validated" followed by the number of run files
  @negative
  Scenario: AJ-114 a missing nebius key stops the campaign before any call
    Given NEBIUS_API_KEY is not set
    When I run "run --tier all --repeat 3"
    Then the exit code is 3
    And no run file was written for any tier
  @negative
  Scenario: AJ-114 rate-limit exhaustion on a hosted tier leaves a failed file that is not committed
    Given the nvidia tier answers HTTP 429 until the five attempts are exhausted
    When the run stops
    Then the exit code is 5 and the nvidia file has run.status "failed"
    And the failed file is excluded from the commit and logged in docs/build-log.md
  @negative
  Scenario: AJ-114 a failed tier is re-run alone
    Given the local and nebius files are completed and the nvidia file failed
    When I run "run --tier nvidia --repeat 3"
    Then a completed nvidia run file exists and the local file is unchanged
