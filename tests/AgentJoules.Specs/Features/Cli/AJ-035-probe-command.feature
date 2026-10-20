@unit @AJ-S035 @REQ-006 @EDGE-001
Feature: Print the probe result as text and JSON, including the no-GPU path
  @positive
  Scenario: AJ-035 Text probe lists every field of the scripted GPU
    Given a scripted GPU named "Scripted GPU 3080" with driver "616.92" and a counter
    When I run "probe"
    Then stdout has the ten labelled lines and the exit code is 0
  @positive
  Scenario: AJ-035 JSON probe has the documented keys
    Given the same scripted GPU
    When I run "probe --json"
    Then stdout is one JSON object with gpuPresent true and a boolean energyCounterSupported, and the exit code is 0
  @positive
  Scenario: AJ-035 Missing NVML prints no GPU in text
    Given the NVML loader is faked as missing
    When I run "probe"
    Then stdout is the single line "no GPU" and the exit code is 0
  @positive
  Scenario: AJ-035 Missing NVML prints null fields in JSON
    Given the NVML loader is faked as missing
    When I run "probe --json"
    Then gpuPresent is false, every other field is null and the exit code is 0
  @negative
  Scenario: AJ-035 Log lines never pollute the JSON on stdout
    Given a scripted GPU whose counter is unsupported under method auto
    When I run "probe --json"
    Then stdout parses as one JSON object and the fallback disclosure appears only on stderr
  @negative
  Scenario: AJ-035 Missing VBIOS is N/A in text and null in JSON
    Given the scripted VBIOS query returns an error
    When I run "probe" and "probe --json"
    Then the text line is "VBIOS version: N/A" and the JSON value is null
  @negative
  Scenario: AJ-035 Forced counter on an unsupported GPU still prints facts
    Given the counter is unsupported and Meter:Method is counter
    When I run "probe --json"
    Then method is null, the exit code is 0 and stdout is still valid JSON
  @negative
  Scenario: AJ-035 A missing config file path is an argument error
    Given the option "--config" points at a file that does not exist
    When I run "probe --config nope.json"
    Then the exit code is 2
