@unit @AJ-S028 @EDGE-013
Feature: Detect GPU throttling during a window
  @positive
  Scenario: AJ-028 A power cap during the window flags the reading
    Given a fake NVML reporting clock event bit 0x4 for the whole window
    When the reading is computed
    Then the flags contain "throttled" and throttleReasons contains "SwPowerCap"
  @positive
  Scenario: AJ-028 A thermal slowdown in one sample is enough
    Given bit 0x40 appears only in the last sample of the window
    When the reading is computed
    Then the flags contain "throttled" and throttleReasons contains "HwThermalSlowdown"
  @positive
  Scenario: AJ-028 The deprecated export is used when the new one is missing
    Given the fake has no nvmlDeviceGetCurrentClocksEventReasons export but has the throttle-reasons export
    When throttle reasons are read
    Then the bits are returned from the deprecated export
  @negative
  Scenario: AJ-028 An idle bit alone is not throttling
    Given the fake reports only the GpuIdle bit 0x1
    When the reading is computed
    Then the flags do not contain "throttled" and throttleReasons is an empty list
  @negative
  Scenario: AJ-028 An unsupported API yields null reasons and no flag
    Given both throttle exports are missing
    When the reading is computed
    Then throttleReasons is null, the flags do not contain "throttled" and nothing claims the GPU was not throttled
  @integration @negative
  Scenario: AJ-028 The laptop result is recorded either way
    Given AGENTJOULES_INTEGRATION is "1"
    When the throttle API is queried on the laptop
    Then docs/build-log.md records "supported" or "unsupported" with the UTC date
