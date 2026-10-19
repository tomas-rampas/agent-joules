@unit @AJ-S022 @REQ-003
Feature: Open, read and dispose an NVML device with a typed result
  @positive
  Scenario: AJ-022 A scripted GPU opens with its identity
    Given a ScriptedNvmlApi with one device named "Scripted GPU 3080" and a 85 W power limit
    When the factory opens index 0
    Then the result is Available and the identity shows the name and a power limit of 85 W
  @positive
  Scenario: AJ-022 MIG not supported is reported as not-supported
    Given the scripted MIG query returns rc 3
    When the factory opens index 0
    Then the identity MIG mode is "not-supported"
  @positive
  Scenario: AJ-022 Dispose shuts NVML down exactly once
    Given an open source
    When Dispose is called twice
    Then Shutdown was called exactly once
  @negative
  Scenario: AJ-022 A missing library yields Unavailable naming the library
    Given the scripted API is in missing-library mode on Windows
    When the factory opens index 0
    Then the result is Unavailable, the reason contains "nvml.dll" and no exception escapes
  @negative
  Scenario: AJ-022 A failing init is reported with its return code
    Given nvmlInit_v2 returns rc 9
    When the factory opens index 0
    Then the result is Unavailable with reason "nvmlInit_v2 returned 9"
  @negative
  Scenario: AJ-022 An out-of-range index is Unavailable
    Given the device count is 1
    When the factory opens index 1
    Then the result is Unavailable with reason "no device at index 1"
  @negative
  Scenario: AJ-022 A read after dispose throws
    Given a disposed source
    When ReadPowerWatts is called
    Then an ObjectDisposedException is thrown
