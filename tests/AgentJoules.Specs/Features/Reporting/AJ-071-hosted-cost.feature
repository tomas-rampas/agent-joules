@unit @AJ-S071 @REQ-009
Feature: AJ-071 Compute hosted token cost in USD or label it N/A
  @positive
  Scenario: AJ-071 one million input and half a million output tokens cost 0.30 USD
    Given a price row with 0.10 USD per 1M input and 0.40 USD per 1M output tokens
    When the cost of 1000000 input and 500000 output tokens is computed
    Then the cost is 0.30 with label "estimated" and currency "USD"
  @positive
  Scenario: AJ-071 a per-agent model override uses its own price row
    Given rows for models "model-a" and "model-b" with different prices
    When the Writer uses "model-b" and the Planner uses "model-a"
    Then each cost is computed from its own model row
  @negative
  Scenario: AJ-071 nvidia free tier without a row is N/A with its reason
    Given no price row for tier "nvidia"
    When the cost of a nvidia step is computed
    Then the cost has label "N/A", reason "free-tier-no-price" and a null value
  @negative
  Scenario: AJ-071 missing nebius row is N/A with the default reason
    Given no price row for the nebius model
    When the cost of a nebius step is computed
    Then the cost has label "N/A", reason "price-not-found" and a null value
  @negative
  Scenario: AJ-071 unreported tokens give N/A not zero
    Given a price row exists and the output tokens are not-reported
    When the cost is computed
    Then the cost has label "N/A", reason "tokens-not-reported" and a null value
