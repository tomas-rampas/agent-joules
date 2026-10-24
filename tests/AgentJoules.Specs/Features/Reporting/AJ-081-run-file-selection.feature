@unit @AJ-S081 @REQ-016
Feature: AJ-081 Select the run files a report may use
  @positive
  Scenario: AJ-081 newest completed file wins
    Given nvidia run files from 2026-10-22 and 2026-10-23 both completed
    When the latest nvidia file is selected
    Then the 2026-10-23 file is returned
  @positive
  Scenario: AJ-081 partial runs are accepted and baseline runs are returned separately
    Given a nebius file with status "partial" and a nebius baseline file
    When latest and latest baseline are selected for nebius
    Then the partial file is the latest and the baseline file is the latest baseline
  @negative
  Scenario: AJ-081 failed and running files are skipped
    Given the newest nvidia file has status "failed" and an older one is completed
    When the latest nvidia file is selected
    Then the older completed file is returned
  @negative
  Scenario: AJ-081 probe and calibration files are never selected
    Given a folder holding only a probe file and a calibration file
    When the latest local file is selected
    Then no file is returned and the tier is reported as not run
  @negative
  Scenario: AJ-081 a 10 s baseline run is skipped by Latest
    Given the newest local file carries run flag "non-publishable" and an older local file has no flags
    When the latest local file is selected
    Then the older file is returned
  @positive
  Scenario: AJ-081 a run with an empty flag list is selected
    Given the newest local file is completed and has an empty run.flags
    When the latest local file is selected
    Then the newest file is returned
