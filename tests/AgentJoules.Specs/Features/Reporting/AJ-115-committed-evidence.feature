@unit @AJ-S115 @REQ-020 @REQ-021 @EDGE-010
Feature: Gate the committed local evidence and the README runtime statements
  @positive
  Scenario: AJ-115 consistent evidence passes
    Given a workspace with a local run file, endpoints, a calibration file and a README that agree
    When the evidence tests run
    Then all checks pass
  @positive
  Scenario: AJ-115 the README sentence matches the run file
    Given the run file has localRuntime "ollama" and the tag "nemotron-3-nano:30b"
    And README.md contains "Local numbers were produced with ollama running nemotron-3-nano:30b."
    When the evidence tests run
    Then the README check passes
  @negative
  Scenario: AJ-115 partial offload without the flag fails
    Given gpuOffloadPercent is 62 and run.flags is empty
    When the evidence tests run
    Then the check fails and names "partial-offload"
  @negative
  Scenario: AJ-115 a baseline shorter than 30 seconds fails
    Given baseline.seconds is 20
    When the evidence tests run
    Then the check fails and names baseline.seconds
  @negative
  Scenario: AJ-115 fewer than three repeats fails
    Given run.repeat is 2
    When the evidence tests run
    Then the check fails and names run.repeat
  @negative
  Scenario: AJ-115 the README names a different tag than the run file
    Given the README sentence names a tag that differs from the run file
    When the evidence tests run
    Then the README check fails and names both tags
  @negative
  Scenario: AJ-115 a fallback decision without the README link fails
    Given the Decision says "Local runtime: ollama" and README.md has no link to the attempt document
    When the evidence tests run
    Then the EDGE-010 check fails
  @negative
  Scenario: AJ-115 a fallback decision with a run file that says nim fails
    Given the Decision says "Local runtime: ollama" and the latest local run file says "nim"
    When the evidence tests run
    Then the EDGE-010 check fails
