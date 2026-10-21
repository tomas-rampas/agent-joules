@unit @AJ-S054 @REQ-017
Feature: AJ-054 Add run --dry-run that validates everything and makes no completion call
  @positive
  Scenario: AJ-054 dry-run prints endpoint, matched IDs and OK per tier
    Given fake keys, fake models responses and a scripted GPU for tiers "local", "nvidia" and "nebius"
    When "run --tier all --dry-run" is executed
    Then the exit code is 0
    And each tier line shows the endpoint, the matched model IDs and "OK"
  @positive
  Scenario: AJ-054 help lists all seven options
    Given the run command
    When "run --help" is executed
    Then the output lists --tier, --task, --mode, --out, --baseline-seconds, --repeat and --dry-run
  @negative
  Scenario: AJ-054 unknown tier exits 2
    Given the run command
    When "run --tier bogus" is executed
    Then the exit code is 2
  @negative
  Scenario: AJ-054 invalid repeat count exits 2
    Given the run command
    When "run --tier nvidia --repeat 0 --dry-run" is executed
    Then the exit code is 2
  @negative
  Scenario: AJ-054 dry-run on local without a GPU exits 6
    Given the GPU port reports no device
    When "run --tier local --dry-run" is executed
    Then the exit code is 6
  @negative
  Scenario: AJ-054 dry-run makes no completion request
    Given fake keys and models responses for tier "nvidia"
    When "run --tier nvidia --dry-run" is executed
    Then no recorded request path ends with "/chat/completions"
