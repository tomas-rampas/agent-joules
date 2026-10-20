@unit @AJ-S031 @REQ-005
Feature: Provide the deterministic fake meter
  @positive
  Scenario: AJ-031 The reference window yields the documented numbers
    Given a deterministic meter at constant 60 W, 10 Hz and a 20 W baseline
    When a window is opened, fake time advances 3 s and the window is closed
    Then gross is 180 J, net is 120 J, average is 60 W, peak is 60 W and sampleCount equals LastWindowSampleCount (31)
  @positive
  Scenario: AJ-031 A window under the baseline has negative net energy
    Given a deterministic meter at constant 15 W and a 20 W baseline
    When a 3 s window is closed
    Then net is -15 J and the flags contain "below-baseline"
  @positive
  Scenario: AJ-031 A short window keeps both edge samples
    Given a deterministic meter at 10 Hz
    When a 150 ms window is closed
    Then sampleCount is 3 and the flags contain "low-resolution"
  @negative
  Scenario: AJ-031 The baseline does not wait
    Given a deterministic meter with an alternating 20 W and 30 W function
    When MeasureBaselineAsync(30 s) is awaited without advancing time
    Then it completes and the flags contain "baseline-unstable"
  @negative
  Scenario: AJ-031 Forced counter without counter support is a precondition failure
    Given a deterministic meter with counter support off and method counter
    When the meter is constructed
    Then a MeasurementPreconditionException is thrown
  @negative
  Scenario: AJ-031 A window cannot be closed twice
    Given a closed window
    When Close is called again
    Then an InvalidOperationException is thrown
