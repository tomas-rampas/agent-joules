@unit @AJ-S074 @REQ-010
Feature: AJ-074 Derive joules and cost per quality point only when both inputs exist
  @positive
  Scenario: AJ-074 joules per quality point equals net joules divided by the score
    Given a step with net energy 120 J and quality 4
    When the derived metrics are computed
    Then joulesPerQualityPoint is 30 with label "model-judged"
  @positive
  Scenario: AJ-074 cost per quality point keeps the currency
    Given a step with cost 0.08 EUR and quality 4
    When the derived metrics are computed
    Then costPerQualityPoint is 0.02 with label "estimated" and currency "EUR"
  @negative
  Scenario: AJ-074 hosted step has no joules per quality point
    Given a hosted step whose energy is N/A and whose quality is 4
    When the derived metrics are computed
    Then joulesPerQualityPoint has label "N/A", reason "missing-input" and a null value
  @negative
  Scenario: AJ-074 unscored step has no derived metrics
    Given a local step with net energy 120 J and quality N/A
    When the derived metrics are computed
    Then both derived metrics have label "N/A" and a null value
  @negative
  Scenario: AJ-074 below-baseline step has no joules per quality point
    Given a local step with negative net energy flagged "below-baseline" and quality 3
    When the derived metrics are computed
    Then joulesPerQualityPoint has label "N/A" and reason "below-baseline"
