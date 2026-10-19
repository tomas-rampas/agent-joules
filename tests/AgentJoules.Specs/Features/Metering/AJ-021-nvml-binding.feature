@unit @AJ-S021 @REQ-003
Feature: Bind NVML through LibraryImport with a DllImport resolver
  @positive
  Scenario: AJ-021 Windows candidates are tried in the documented order
    Given the platform is Windows and ProgramW6432 is "C:\Program Files"
    When the candidate list is built
    Then it is "nvml.dll" followed by "C:\Program Files\NVIDIA Corporation\NVSMI\nvml.dll"
  @positive
  Scenario: AJ-021 The first loadable candidate wins
    Given an injected loader that fails the first candidate and succeeds on the second
    When TryLoadFirst runs
    Then it returns true with the second candidate's handle
  @negative
  Scenario: AJ-021 No loadable candidate returns false without an exception
    Given an injected loader that fails every candidate
    When TryLoadFirst runs
    Then it returns false and no exception escapes
  @negative
  Scenario: AJ-021 A missing ProgramW6432 does not produce a relative path
    Given ProgramW6432 is empty
    When the Windows candidate list is built
    Then it contains only "nvml.dll"
  @positive
  Scenario: AJ-021 Linux candidates are ordered soname first
    Given the platform is Linux
    When the candidate list is built
    Then it is "libnvidia-ml.so.1" followed by "libnvidia-ml.so"
  @negative
  Scenario: AJ-021 An unknown platform has no candidates
    Given the platform is neither Windows nor Linux
    When the candidate list is built
    Then it is empty
