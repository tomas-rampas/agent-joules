@unit @AJ-S026 @REQ-005 @EDGE-003
Feature: Compute gross, net, average and peak energy per window
  @positive
  Scenario: AJ-026 Three seconds at 60 W against a 20 W baseline
    Given 31 samples of 60 W over a 3 s window and a baseline of 20 W
    When the reading is computed
    Then gross is 180 J, net is 120 J, average is 60 W, peak is 60 W and sampleCount is 31
  @positive
  Scenario: AJ-026 A window below baseline keeps the negative net value
    Given a 3 s window at 15 W and a baseline of 20 W
    When the reading is computed
    Then net is -15 J and the flags contain "below-baseline"
  @positive
  Scenario: AJ-026 Counter method takes gross from the counter only
    Given method counter, a counter delta of 170000 mJ and samples at 60 W for 3 s
    When the reading is computed
    Then gross is 170 J and average is 60 W from the samples
  @positive
  Scenario: AJ-026 A 150 ms window is flagged low-resolution
    Given a 150 ms window at 10 Hz sampling
    When the reading is computed
    Then the flags contain "low-resolution" and the reading is kept
  @negative
  Scenario: AJ-026 A 200 ms window is not low-resolution
    Given a 200 ms window at 10 Hz sampling
    When the reading is computed
    Then the flags do not contain "low-resolution"
  @negative
  Scenario: AJ-026 No baseline leaves net not-reported
    Given a window and no baseline
    When the reading is computed
    Then net is label not-reported with reason "no-baseline" and value null
  @negative
  Scenario: AJ-026 A counter that went backwards is not-reported
    Given a counter delta of -5000 mJ
    When the reading is computed
    Then gross and net are not-reported with reason "counter-went-backwards" and no value is 0
  @negative
  Scenario: AJ-026 Fewer than two samples is not-reported
    Given a window with one sample and method sampling
    When the reading is computed
    Then gross, average and peak are not-reported with reason "insufficient-samples"
