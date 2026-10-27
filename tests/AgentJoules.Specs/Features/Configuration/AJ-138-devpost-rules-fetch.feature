@integration @AJ-S138 @REQ-028
Feature: AJ-138 Official rules page fetch
  @positive
  Scenario: AJ-138 The rules page answers or the checklist says not verified
    Given network access
    When the builder requests https://nebiusglobalaihackathon.devpost.com/rules
    Then either the text is recorded with the fetch date or the checklist says "not verified" with the HTTP status
  @negative
  Scenario: AJ-138 A 403 answer is recorded and the user is asked to paste the rules
    Given the page answers HTTP 403
    When the checklist is written
    Then the Source and verification section contains "HTTP 403" and "not verified" and asks the user to paste the Rules tab text
