@unit @AJ-S033 @REQ-004
Feature: Bind and validate the GPU, meter and baseline options
  @positive
  Scenario: AJ-033 The shipped file binds to the documented defaults
    Given config/appsettings.json
    When the options are bound
    Then Gpu:Index is 0, Meter:Method is "auto", Meter:SampleRateHz is 10, AllowBelowMinRate is false, Baseline:Seconds is 30 and the CV limit is 0.10
  @positive
  Scenario: AJ-033 An environment-style key overrides the file
    Given the isolated configuration holds AGENTJOULES__Gpu__Index "7"
    When the options are bound
    Then Gpu:Index is 7 and no process environment variable was set
  @positive
  Scenario: AJ-033 Five hertz is allowed only with the override
    Given Meter:SampleRateHz is 5 and Meter:AllowBelowMinRate is true
    When the options are validated
    Then validation succeeds
  @negative
  Scenario: AJ-033 Five hertz without the override is rejected with exit code 2
    Given Meter:SampleRateHz is 5 and Meter:AllowBelowMinRate is false
    When the CLI starts with these options
    Then the exit code is 2 and the message names Meter:SampleRateHz and Meter:AllowBelowMinRate
  @negative
  Scenario: AJ-033 An unknown method is rejected
    Given Meter:Method is "turbo"
    When the options are validated
    Then validation fails naming Meter:Method
  @negative
  Scenario: AJ-033 A negative GPU index is rejected
    Given Gpu:Index is -1
    When the options are validated
    Then validation fails naming Gpu:Index
  @negative
  Scenario: AJ-033 A zero baseline window is rejected
    Given Baseline:Seconds is 0
    When the options are validated
    Then validation fails naming Baseline:Seconds
  @negative
  Scenario: AJ-033 A zero rate is rejected even with the override
    Given Meter:SampleRateHz is 0 and Meter:AllowBelowMinRate is true
    When the options are validated
    Then validation fails naming Meter:SampleRateHz
