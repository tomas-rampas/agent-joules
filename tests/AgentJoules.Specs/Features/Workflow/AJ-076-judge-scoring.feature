@unit @AJ-S076 @REQ-010
Feature: AJ-076 Score each ok step with a metered hosted judge
  @positive
  Scenario: AJ-076 judge reply 4 becomes quality 4 and a derived ratio
    Given a completed local step with net energy 120 J and a fake judge that replies "4"
    When the run is judged
    Then the step quality is 4 with label "model-judged"
    And joulesPerQualityPoint is 30
  @positive
  Scenario: AJ-076 judge readings stay out of the workflow totals
    Given 4 workflow readings and 4 judge readings with known token counts
    When the run totals are assembled
    Then totals inputTokens equals the sum over the workflow readings only
    And totals.judge inputTokens equals the sum over the judge readings
  @positive
  Scenario: AJ-076 self-judging is flagged when the judge model equals a step model
    Given a judge model ID equal to the Writer model ID
    When the run is judged
    Then run.selfJudged is true
  @negative
  Scenario: AJ-076 a failed step is not sent to the judge
    Given a step with outcome "error"
    When the run is judged
    Then the fake judge received no call for that step
    And the step quality has label "N/A"
  @negative
  Scenario: AJ-076 judge reading never appears in the workflow sink
    Given a judged run
    When the workflow sink snapshot is read
    Then it contains no reading for agent "Judge"
  @negative
  Scenario: AJ-076 an empty step output is not judged
    Given a step with outcome "ok" and outputChars 0
    When the run is judged
    Then the fake judge received no call for that step
    And the step quality has label "N/A" and reason "empty-output"
