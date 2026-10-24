@unit @AJ-S070 @REQ-009
Feature: AJ-070 Load a price table that cannot represent a zero price
  @positive
  Scenario: AJ-070 valid row loads and is found by tier and model
    Given a price file with one nebius row that has both prices, a source URL and a retrieval date
    When the price table is loaded and "nebius" with that model ID is looked up
    Then the row is returned
  @positive
  Scenario: AJ-070 model without a row is not found
    Given a price file with one nebius row
    When "nvidia" with another model ID is looked up
    Then the lookup returns no row
  @negative
  Scenario: AJ-070 row without a source URL is rejected
    Given a price file whose only row has no sourceUrl
    When the price table is loaded
    Then the exit code is 2
    And the output names row 0 and the field "sourceUrl"
  @negative
  Scenario: AJ-070 zero price is rejected
    Given a price file whose only row has inputUsdPer1M 0
    When the price table is loaded
    Then the exit code is 2
    And the output names the field "inputUsdPer1M"
  @negative
  Scenario: AJ-070 duplicate tier and model rows are rejected
    Given a price file with two rows for the same tier and model ID
    When the price table is loaded
    Then the exit code is 2
