@integration @AJ-S131 @REQ-025 @REQ-032
Feature: AJ-131 Prior-art citations resolve
  @positive
  Scenario: AJ-131 Each prior-art link answers
    Given the links in the Prior art section
    When each link is requested
    Then each answers HTTP 200 and the check date is written to docs/build-log.md
  @negative
  Scenario: AJ-131 A dead prior-art link is reported
    Given a link in the Prior art section that answers HTTP 404
    When the link check runs
    Then it fails naming the URL and the README is not marked verified
