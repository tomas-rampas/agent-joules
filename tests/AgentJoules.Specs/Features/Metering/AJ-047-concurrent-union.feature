@unit @AJ-S047 @REQ-013 @EDGE-004
Feature: AJ-047 Flag shared windows and total concurrent runs over the union window
  @positive
  Scenario: AJ-047 overlapping branches are flagged and totalled over the union
    Given concurrent mode and two branches of 1 second at a constant 60 W on a deterministic meter
    When the run is assembled
    Then both readings have flag "shared-window"
    And the run gross energy equals the union-window energy and not the sum of the two readings
  @positive
  Scenario: AJ-047 serialized run totals are the sum of the steps
    Given serialized mode and two consecutive 1 second steps at a constant 60 W
    When the run is assembled
    Then the run gross energy equals the sum of the two readings
    And no reading has flag "shared-window"
  @positive
  Scenario: AJ-047 cost is totalled per currency without conversion
    Given step costs of 0.10 EUR, 0.20 EUR and 0.05 USD
    When the run is assembled
    Then costByCurrency has EUR 0.30 and USD 0.05
  @negative
  Scenario: AJ-047 windows that only touch are not shared
    Given concurrent mode and two windows where the first ends exactly when the second starts
    When the run is assembled
    Then neither reading has flag "shared-window"
  @negative
  Scenario: AJ-047 hosted-tier concurrent totals are N/A not zero
    Given concurrent mode on tier "nvidia" with unmetered readings
    When the run is assembled
    Then totals grossJoules has label "N/A" and a null value
  @negative
  Scenario: AJ-047 judge readings never enter the totals
    Given 4 workflow readings and 4 judge readings
    When the run is assembled
    Then totals inputTokens equals the sum over the 4 workflow readings only
