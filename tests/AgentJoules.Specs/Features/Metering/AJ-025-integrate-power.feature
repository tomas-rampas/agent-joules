@unit @AJ-S025 @REQ-004
Feature: Integrate power samples into joules
  @positive
  Scenario: AJ-025 Constant 50 W over two seconds is 100 J
    Given 21 samples of 50000 mW at t = 0.0, 0.1, ..., 2.0 s
    When the window 0.0 to 2.0 s is integrated
    Then the result is 100 J within 1 percent
  @positive
  Scenario: AJ-025 Irregular timestamps give the analytic integral
    Given a 40 W to 60 W linear ramp sampled at 0.0, 0.05, 0.40, 1.00, 1.75 and 2.0 s
    When the window 0.0 to 2.0 s is integrated
    Then the result is 100 J within 1 percent
  @positive
  Scenario: AJ-025 The first and last samples are held to the window edges
    Given two 50 W samples at 0.2 s and 0.8 s
    When the window 0.0 to 1.0 s is integrated
    Then the result is 50 J
  @positive
  Scenario: AJ-025 Actual timestamps beat the nominal interval
    Given samples at 0, 0.1, 0.5, 0.6 and 1.0 s following power = 100 W/s x t
    When the window 0 to 1 s is integrated
    Then the result is 50 J within 1 percent
  @negative
  Scenario: AJ-025 Non-monotonic timestamps are rejected
    Given samples at 0.0, 0.3 and 0.2 s
    When the integrator runs
    Then an ArgumentException is thrown
  @negative
  Scenario: AJ-025 One sample yields no value
    Given a single sample in the window
    When the integrator runs
    Then the result is null, not 0
  @negative
  Scenario: AJ-025 A sample outside the window is rejected
    Given a sample at 2.5 s and the window 0.0 to 2.0 s
    When the integrator runs
    Then an ArgumentException is thrown
