@unit @AJ-S030 @REQ-005 @EDGE-005
Feature: Measure the idle baseline and flag an unstable one
  @positive
  Scenario: AJ-030 A steady 20 W baseline is stable
    Given 30 s of samples at a constant 20 W
    When the baseline is calculated with threshold 0.10
    Then mean is 20 W, standard deviation is 0 W and there are no flags
  @positive
  Scenario: AJ-030 An alternating baseline is flagged and warned about
    Given samples alternating 20 W and 30 W
    When the baseline is calculated with threshold 0.10
    Then mean is 25 W, standard deviation is 5 W, the flags contain "baseline-unstable" and the console line is "Warning: idle baseline unstable (CV 0.20 > 0.10)"
  @positive
  Scenario: AJ-030 The meter waits for the baseline duration on fake time
    Given a started meter at 10 Hz
    When MeasureBaselineAsync(30 s) runs and the fake clock advances 30 s
    Then the baseline seconds is 30 and no real time was spent waiting
  @negative
  Scenario: AJ-030 A coefficient of variation equal to the threshold is not flagged
    Given a baseline whose CV is exactly 0.10
    When the baseline is calculated with threshold 0.10
    Then the flags are empty
  @negative
  Scenario: AJ-030 A zero-mean baseline is not-reported
    Given samples that are all 0 W
    When the baseline is calculated
    Then meanW is not-reported with reason "baseline-mean-nonpositive" and no CV is divided by zero
  @negative
  Scenario: AJ-030 One baseline sample is not enough
    Given a single baseline sample
    When the baseline is calculated
    Then meanW is not-reported with reason "insufficient-samples"
  @negative
  Scenario: AJ-030 The population standard deviation is used
    Given samples 10 W and 20 W
    When the baseline is calculated
    Then the standard deviation is 5 W, not 7.07 W
