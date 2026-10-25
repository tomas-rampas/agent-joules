@unit @AJ-S109 @REQ-021
Feature: Compute the counter versus sampling calibration
  @positive
  Scenario: AJ-109 both methods are written with the relative difference
    Given a fake meter whose counter reads 100 J and whose sampling integral reads 98 J for the window
    When I run "calibrate --seconds 60"
    Then the file has counterJoules 100, samplingJoules 98 and relativeDifference 0.02, each labelled "measured"
  @positive
  Scenario: AJ-109 the file name and schema are valid
    When I run "calibrate --seconds 60"
    Then a file matching "calibration-<yyyyMMddTHHmmssZ>.json" exists in the output folder
    And it validates against the calibration schema definition
  @positive
  Scenario: AJ-109 the default task is the first task in file order
    When I run "calibrate"
    Then the file names the first task file and seconds 60
  @negative
  Scenario: AJ-109 an unsupported counter is written as N/A with the reason
    Given the fake NVML returns NOT_SUPPORTED for the energy counter
    When I run "calibrate --seconds 60"
    Then counterJoules is null with label "N/A" and a non-empty reason
    And relativeDifference is null with label "N/A"
    And the exit code is 0
  @negative
  Scenario: AJ-109 a counter of zero gives an N/A difference
    Given the fake counter reads 0 J for the window
    When I run "calibrate --seconds 60"
    Then relativeDifference is null with reason "counter-zero"
  @negative
  Scenario: AJ-109 no GPU exits 6 and writes nothing
    Given the NVML loader is faked as missing
    When I run "calibrate"
    Then the exit code is 6
    And no calibration file exists
  @negative
  Scenario: AJ-109 a zero duration is refused
    When I run "calibrate --seconds 0"
    Then the exit code is 2
  @negative
  Scenario: AJ-109 an unknown task is refused
    When I run "calibrate --task no-such-task"
    Then the exit code is 2
