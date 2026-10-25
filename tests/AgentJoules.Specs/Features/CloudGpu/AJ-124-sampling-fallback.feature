@unit @AJ-S124 @EDGE-015
Feature: Use power sampling when the energy counter is unsupported on the VM
  @positive
  Scenario: AJ-124 a supported counter keeps the counter method
    Given the fake NVML supports the energy counter and the tier is "cloudgpu"
    When I run "run --tier cloudgpu --repeat 1 --task incident-brief"
    Then every reading has method "counter" and no "sampling-fallback" flag
  @positive
  Scenario: AJ-124 the suffix names the probe and calibration files
    Given the tier is "cloudgpu"
    When I run "probe --json --save --suffix cloudgpu" and "calibrate --seconds 60"
    Then the files match "probe-<yyyyMMddTHHmmssZ>-cloudgpu.json" and "calibration-<yyyyMMddTHHmmssZ>-cloudgpu.json"
  @positive
  Scenario: AJ-124 an unsupported counter falls back to sampling with the flag
    Given the fake NVML returns NOT_SUPPORTED for the counter and the tier is "cloudgpu"
    When I run "run --tier cloudgpu --repeat 1 --task incident-brief"
    Then every reading has method "sampling" and the flag "sampling-fallback"
    And the console shows the fallback line
  @positive
  Scenario: AJ-124 the calibration states the counter as N/A with the probe reason
    Given the fake NVML returns NOT_SUPPORTED for the counter
    When I run "calibrate --seconds 60"
    Then counterJoules is null with label "N/A" and a non-empty reason
    And relativeDifference is null with label "N/A"
  @positive
  Scenario: AJ-124 the report and README fixture disclose sampling
    Given a cloudgpu run file with method "sampling"
    When the report and the README block are rendered
    Then the header contains "Energy method: sampling (counter unsupported)"
    And the cloudgpu README row contains "sampling"
  @negative
  Scenario: AJ-124 a forced counter method with no counter exits 6
    Given Meter:Method is "counter" and the counter is unsupported
    When I run "run --tier cloudgpu --dry-run"
    Then the exit code is 6
  @negative
  Scenario: AJ-124 an unsupported counter never yields a zero counter value
    Given the fake NVML returns NOT_SUPPORTED for the counter
    When I run "calibrate --seconds 60"
    Then counterJoules has a null value and the file contains no counterJoules value of 0
  @negative
  Scenario: AJ-124 a forced sampling method is not labelled as a fallback
    Given Meter:Method is "sampling" and the counter is supported
    When I run "run --tier cloudgpu --repeat 1 --task incident-brief"
    Then every reading has method "sampling" and no "sampling-fallback" flag
