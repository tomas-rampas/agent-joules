@integration @AJ-S111 @REQ-021
Feature: Run the calibration on the laptop
  @positive
  Scenario: AJ-111 the calibration file is written with three label objects
    Given the preflight of AJ-110 passed and Ollama serves the local model
    When I run "calibrate --seconds 60"
    Then the exit code is 0
    And the new calibration file has counterJoules, samplingJoules and relativeDifference as label objects
  @positive
  Scenario: AJ-111 the stored difference matches the recomputed value
    Given the committed calibration file with counterJoules and samplingJoules both measured
    When the difference is recomputed as (counter - sampling) / counter
    Then it equals the stored relativeDifference within 1e-9
  @negative
  Scenario: AJ-111 Ollama is not running
    Given the Ollama service is stopped
    When I run "calibrate --seconds 60"
    Then the exit code is 5
    And no calibration file is written
  @negative
  Scenario: AJ-111 the counter is unsupported on this GPU
    Given the committed probe has energyCounterSupported false
    When I run "calibrate --seconds 60"
    Then counterJoules has label "N/A" with a reason and the exit code is 0
  @negative
  Scenario: AJ-111 the laptop runs on battery
    Given the probe reports power source "battery"
    When the builder evaluates the calibration precondition
    Then the calibration is not run until mains power is connected
