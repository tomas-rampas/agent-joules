@integration @AJ-S137 @REQ-027
Feature: AJ-137 Article citations resolve
  @positive
  Scenario: AJ-137 Each cited link answers
    Given the four links of the article
    When each link is requested
    Then each answers HTTP 200 and the date is recorded in docs/build-log.md
  @negative
  Scenario: AJ-137 A link that answers 404 is reported
    Given a cited link that answers HTTP 404
    When the link check runs
    Then it fails naming the URL
