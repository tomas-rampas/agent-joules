@unit @AJ-S057 @REQ-017 @EDGE-006
Feature: AJ-057 Stop on a failed step, keep partial results and exit 5
  @positive
  Scenario: AJ-057 steps finished before a failure stay in the partial file
    Given a nvidia run where the Reviewer of the second task fails after the first task completed
    When "run --tier nvidia --repeat 1" is executed
    Then the results file holds the 4 steps of the first task unchanged
  @positive
  Scenario: AJ-057 earlier tier files remain when a later tier fails
    Given tiers "nvidia" and "nebius" where nebius answers 429 on every call
    When "run --tier all --repeat 1" is executed
    Then the nvidia run file exists with status "completed"
    And the exit code is 5
  @negative
  Scenario: AJ-057 exhausted 429 retries write a failed step and exit 5
    Given an endpoint that answers 429 on every call
    When "run --tier nvidia --repeat 1" is executed
    Then exactly 5 HTTP requests were recorded for the first step
    And the results file has a step with outcome "error" and run.status "failed"
    And the exit code is 5
  @negative
  Scenario: AJ-057 a 500 response ends the tier with exit 5 and no retry
    Given an endpoint that answers 500
    When "run --tier nebius --repeat 1" is executed
    Then exactly 1 HTTP request was recorded
    And the exit code is 5
  @negative
  Scenario: AJ-057 steps that never ran are absent not zero
    Given a run that fails on its first step
    When the results file is read
    Then it contains 1 step and no step with a numeric 0 for tokens or joules
