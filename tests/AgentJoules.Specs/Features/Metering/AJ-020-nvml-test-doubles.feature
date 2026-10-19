@unit @AJ-S020 @REQ-003 @REQ-004
Feature: Provide the NVML and time test doubles
  @positive
  Scenario: AJ-020 Scripted power follows the script over fake time
    Given a ScriptedNvmlApi that draws a constant 50 W and was initialised
    When the fake time advances to 1.0 s and power is read
    Then the return code is Success and the value is 50000 milliwatts
  @positive
  Scenario: AJ-020 The scripted counter integrates the scripted power
    Given a ScriptedNvmlApi at a constant 50 W with the counter supported
    When the fake time advances 2.0 s between two counter reads
    Then the difference is 100000 millijoules
  @negative
  Scenario: AJ-020 An unsupported counter returns rc 3
    Given a ScriptedNvmlApi with the counter switch off
    When the total energy counter is read
    Then the return code is NotSupported (3) and no value is produced
  @negative
  Scenario: AJ-020 Missing-library mode throws a typed exception
    Given a ScriptedNvmlApi in missing-library mode
    When Init is called
    Then a DllNotFoundException is thrown
  @negative
  Scenario: AJ-020 Reads before Init are rejected
    Given a ScriptedNvmlApi that was never initialised
    When power is read
    Then the return code is Uninitialized
