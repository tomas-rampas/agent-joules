@unit @AJ-S150 @REQ-032 @DoD
Feature: AJ-150 Laptop and hosted run evidence
  @positive
  Scenario: AJ-150 Each tier has a completed run file with at least three repeats
    Given the committed results folder
    When the newest non-baseline file per tier is read
    Then local, nvidia and nebius each have status "completed" and run.repeat 3 or more
  @positive
  Scenario: AJ-150 The local file carries baseline and runtime evidence
    Given the newest local run file
    When baseline.seconds, modelLoadSeconds, powerSource, localRuntime and gpuOffloadPercent are read
    Then baseline.seconds is 30 or more and the other four fields are present and valid
  @positive
  Scenario: AJ-150 Validate counts every committed result file
    Given the committed results folder
    When "validate results/" runs
    Then the exit code is 0 and "Validated <n> files" equals the number of run files
  @negative
  Scenario: AJ-150 A tier with run.repeat 1 is rejected
    Given a results folder whose newest nvidia file has run.repeat 1
    When the evidence check runs
    Then it fails with "run.repeat 1 is below 3: nvidia"
  @negative
  Scenario: AJ-150 A missing tier file is rejected
    Given a results folder without a nebius run file
    When the evidence check runs
    Then it fails with "no run file for tier: nebius"
  @negative
  Scenario: AJ-150 A hosted energy value of 0 is rejected
    Given a nebius fixture whose grossJoules is {value:0,label:"measured"}
    When "validate" runs on the folder
    Then the exit code is 2 and the failure names the hosted step
  @negative
  Scenario: AJ-150 A baseline of 20 seconds is rejected
    Given a local fixture with baseline.seconds 20
    When the evidence check runs
    Then it fails with "baseline.seconds 20 is below 30"
  @positive
  Scenario: AJ-150 Replay of the committed nebius file emits one event per step
    Given the newest committed nebius run file
    When "dashboard --replay <file> --speed 50 --exit-after 5" runs in process
    Then the exit code is 0 and the number of events equals the number of steps in the file
  @negative
  Scenario: AJ-150 Replay of a file that does not exist is rejected
    Given no file at results/absent.json
    When "dashboard --replay results/absent.json" runs
    Then the exit code is not 0 and the message names the file
  @negative
  Scenario: AJ-150 The build log lacks the no-browser statement and is rejected
    Given a build log without the sentence that the published runs were recorded without a browser open
    When the evidence check runs
    Then it fails with "missing statement: published runs recorded without a browser"
  @negative
  Scenario: AJ-150 A leftover partial file is rejected
    Given a results folder containing "x.json.partial"
    When the evidence check runs
    Then it fails with "stray file: x.json.partial"
