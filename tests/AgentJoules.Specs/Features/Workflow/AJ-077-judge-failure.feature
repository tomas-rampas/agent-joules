@unit @AJ-S077 @EDGE-012
Feature: AJ-077 Never let a judge failure change the exit code
  @positive
  Scenario: AJ-077 partial judge failure keeps the scores that succeeded
    Given a judge that fails for the third of 12 steps and answers "4" otherwise
    When the run is judged
    Then 11 steps have quality 4 with label "model-judged"
    And the third step has quality label "N/A" and qualityReason "not-scored"
  @positive
  Scenario: AJ-077 one warning line is printed per run
    Given a judge that fails for every step
    When the run completes
    Then exactly 1 judge warning line is printed
  @negative
  Scenario: AJ-077 failing judge leaves exit 0 and the results written
    Given a judge endpoint that answers 500 on every call
    When "run --tier nvidia --repeat 1" is executed
    Then the exit code is 0
    And every step has quality label "N/A", qualityReason "not-scored" and joulesPerQualityPoint label "N/A"
    And the results file has run.status "completed"
  @negative
  Scenario: AJ-077 judge 429 exhaustion does not change the exit code
    Given a judge endpoint that answers 429 on every call
    When "run --tier nvidia --repeat 1" is executed
    Then the exit code is 0
  @negative
  Scenario: AJ-077 unparseable judge reply is recorded as a parse error
    Given a judge that replies "excellent work"
    When the run is judged
    Then the quality reason starts with "parse-error:" and the exit code is 0
  @negative
  Scenario: AJ-077 missing judge key outside dry-run warns and exits 0
    Given the judge key is absent and the workflow tier key is present
    When "run --tier nebius --repeat 1" is executed
    Then the exit code is 0
    And every step quality is N/A
