@unit @AJ-S106 @REQ-021
Feature: Order warm-up, idle baseline and repeats in local runs
  @positive
  Scenario: AJ-106 the call order is warm-up, baseline, tasks
    Given a local run with fakes recording every operation
    When I run "run --tier local --task incident-brief --repeat 1 --baseline-seconds 30"
    Then the recorded order is probe, meter start, warm-up, baseline window, task calls
    And no step reading exists for the warm-up call
  @positive
  Scenario: AJ-106 the warm-up keeps the model loaded for 30 minutes
    Given the Ollama runtime is used
    When the warm-up call is made
    Then the request body contains keep_alive "30m" and an empty prompt
    And environment.modelLoadSeconds equals the fake warm-up duration
  @positive
  Scenario: AJ-106 three repeats produce twelve steps for one task
    When I run "run --tier local --task incident-brief --repeat 3"
    Then the run file has 12 steps with repeat values 1, 2 and 3 four times each
    And run.repeat is 3
  @positive
  Scenario: AJ-106 the power source is recorded
    Given the fake power source detector returns "battery"
    When I run "run --tier local --task incident-brief --repeat 1"
    Then environment.powerSource is "battery"
  @negative
  Scenario: AJ-106 a zero repeat is refused
    When I run "run --tier local --repeat 0"
    Then the exit code is 2
  @negative
  Scenario: AJ-106 a short baseline is flagged as not publishable
    When I run "run --tier local --task incident-brief --repeat 1 --baseline-seconds 10"
    Then the output contains "baseline shorter than 30 s is not publishable"
    And baseline.seconds is 10
    And run.flags contains "non-publishable"
  @positive
  Scenario: AJ-106 a 30 s baseline run carries no non-publishable flag
    When I run "run --tier local --task incident-brief --repeat 1 --baseline-seconds 30"
    Then run.flags does not contain "non-publishable"
  @negative
  Scenario: AJ-106 a short-baseline file is not selected as the latest local file
    Given a valid local run file and a newer local run file whose run.flags contain "non-publishable"
    When the latest file for tier "local" is selected
    Then the valid local run file is returned
  @negative
  Scenario: AJ-106 a failing warm-up stops before the baseline
    Given the fake Ollama endpoint refuses the warm-up request
    When I run "run --tier local --repeat 1"
    Then the exit code is 5
    And no baseline window was opened
  @negative
  Scenario: AJ-106 a hosted tier has no warm-up and no baseline window
    When I run "run --tier nvidia --task incident-brief --repeat 1 --baseline-seconds 30"
    Then no warm-up call and no baseline window were recorded
