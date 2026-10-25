@unit @AJ-S122 @EDGE-017
Feature: Enforce the 4-hour and USD 25 stop
  @positive
  Scenario: AJ-122 a run under both limits continues
    Given the observed cost is 12.40 USD and 1 hour has passed since the tier start
    When the guard is checked
    Then the decision is Continue
  @positive
  Scenario: AJ-122 one second before four hours still continues
    Given the observed cost is 6.00 USD and 3 hours 59 minutes 59 seconds have passed
    When the guard is checked
    Then the decision is Continue
  @negative
  Scenario: AJ-122 an observed cost of 25 USD stops new work
    Given the observed cost is 25.00 USD
    When the guard is checked before the run
    Then the decision is Stop with reason "cost-cap"
    And "run --tier cloudgpu" exits 6 and starts no workflow call
  @negative
  Scenario: AJ-122 four hours stop new work
    Given 4 hours have passed since the tier start and the observed cost is 6.00 USD
    When the guard is checked before the next repeat
    Then the decision is Stop with reason "time-box"
  @negative
  Scenario: AJ-122 a mid-run stop keeps the finished repeats as a partial file
    Given repeats 1 and 2 of task one finished and the time box expires before repeat 3
    When the orchestrator checks the guard
    Then no further repeat starts
    And the run file has run.status "partial" and run.stopReason "time-box"
    And the exit code is 0 and the output tells the user to delete the VM
  @negative
  Scenario: AJ-122 cost cap wins when both limits hold
    Given the observed cost is 25.50 USD and 4 hours have passed
    When the guard is checked
    Then the stop reason is "cost-cap"
  @negative
  Scenario: AJ-122 a stop threshold above the cap is rejected
    Given CloudGpu:CostStopUsd is 31
    When I run "run --tier cloudgpu --dry-run"
    Then the exit code is 2
