@unit @AJ-S072 @REQ-009
Feature: AJ-072 Compute local electricity cost in EUR and cloudgpu rental cost in USD
  @positive
  Scenario: AJ-072 3.6 million joules net at 0.30 EUR per kWh cost 0.30 EUR
    Given net energy of 3600000 J and an electricity price of 0.30 EUR per kWh
    When the local cost is computed
    Then the cost is 0.30 with label "estimated" and currency "EUR"
  @positive
  Scenario: AJ-072 cloudgpu cost is the hourly price times wall time
    Given an hourly price of 3.60 USD and a step wall time of 1000 seconds
    When the cloudgpu cost is computed
    Then the cost is 1.00 with label "estimated" and currency "USD"
  @positive
  Scenario: AJ-072 emissions are estimated when a factor is configured
    Given net energy of 3600000 J and a factor of 400 gCO2e per kWh
    When emissions are computed
    Then totals gco2e is 400 with label "estimated"
  @negative
  Scenario: AJ-072 energy N/A gives cost N/A
    Given a local step whose energy is N/A with reason "counter-and-sampling-unavailable"
    When the local cost is computed
    Then the cost has label "N/A", reason "energy-not-measured" and a null value
  @negative
  Scenario: AJ-072 below-baseline step has no cost
    Given a local step with negative net joules and flag "below-baseline"
    When the local cost is computed
    Then the cost has label "N/A" and reason "below-baseline"
    And the net joules remain negative in the reading
  @negative
  Scenario: AJ-072 unset hourly price gives cloudgpu cost N/A
    Given CloudGpu:HourlyPriceUsd is not configured
    When the cloudgpu cost is computed
    Then the cost has label "N/A" and reason "hourly-price-not-configured"
  @negative
  Scenario: AJ-072 unset emission factor gives emissions N/A
    Given Cost:GCo2ePerKwh is null
    When the run totals are assembled
    Then totals gco2e has label "N/A" and reason "not-configured"
