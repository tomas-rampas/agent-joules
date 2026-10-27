@integration @AJ-S156 @DoD
Feature: AJ-156 Remote state
  @positive
  Scenario: AJ-156 The remote main still points at the recorded base
    Given network access and the recorded base hash
    When "git ls-remote origin main" runs
    Then it prints the recorded hash
  @negative
  Scenario: AJ-156 A moved remote main is reported
    Given the remote main differs from the recorded base
    When the check runs
    Then it reports "remote main moved" and asks the user whether they pushed
