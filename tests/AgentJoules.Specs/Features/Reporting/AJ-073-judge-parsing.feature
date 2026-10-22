@unit @AJ-S073 @REQ-010
Feature: AJ-073 Parse judge replies into an integer score from 1 to 5
  @positive
  Scenario: AJ-073 plain integer reply parses
    Given a judge reply " 4\n"
    When the reply is parsed
    Then the quality is 4 with label "model-judged"
  @positive
  Scenario: AJ-073 think block is removed before parsing
    Given a judge reply "<think>the answer is fine</think>5"
    When the reply is parsed
    Then the quality is 5 with label "model-judged"
  @negative
  Scenario Outline: AJ-073 invalid replies give N/A with a parse error
    Given a judge reply "<reply>"
    When the reply is parsed
    Then the quality has label "N/A" and a null value
    And the reason starts with "parse-error:"

    Examples:
      | reply                    |
      | 4/5                      |
      | 6                        |
      | 0                        |
      | 3.5                      |
      | four                     |
      | 4 because it is good     |
  @negative
  Scenario: AJ-073 excerpt is capped at 60 characters
    Given a judge reply of 200 characters that is not an integer
    When the reply is parsed
    Then the recorded excerpt has at most 60 characters
