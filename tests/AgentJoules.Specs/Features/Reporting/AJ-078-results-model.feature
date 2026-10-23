@unit @AJ-S078 @REQ-015
Feature: AJ-078 Complete the results model and its serialisation
  @positive
  Scenario: AJ-078 document with every label kind round-trips unchanged
    Given a fixture document with measured, estimated, model-judged, N/A and not-reported metrics
    When it is serialised and read back
    Then the read-back document equals the original
  @positive
  Scenario: AJ-078 label strings are the documented vocabulary
    Given a fixture document
    When it is serialised
    Then the label values used are only "measured", "estimated", "model-judged", "N/A" and "not-reported"
  @negative
  Scenario: AJ-078 N/A metric with a number is rejected on read
    Given a results text where a metric has label "N/A" and value 0
    When it is read
    Then reading fails
  @negative
  Scenario: AJ-078 unknown property is rejected on read
    Given a results text with an extra property "surprise" in a step
    When it is read
    Then reading fails and names the property
  @negative
  Scenario: AJ-078 step output text is never written
    Given a step reading whose in-memory output text is "secret text"
    When the document is serialised
    Then the text "secret text" does not appear and outputSha256 and outputChars are present
  @positive
  Scenario: AJ-078 the non-publishable run flag round-trips
    Given a fixture document whose run.flags is ["non-publishable"]
    When it is serialised and read back
    Then the read-back run.flags equals ["non-publishable"]
  @positive
  Scenario: AJ-078 tool version and commit come from the informational version
    Given an informational version "0.1.0+abc1234"
    When the run info is created
    Then run.toolVersion is "0.1.0" and run.gitCommit is "abc1234"
  @negative
  Scenario: AJ-078 an unlisted run flag is rejected on read
    Given a results text whose run.flags contains "made-up"
    When it is read
    Then reading fails and names the flag
  @negative
  Scenario: AJ-078 a version without a commit suffix gives unknown
    Given an informational version "0.1.0"
    When the run info is created
    Then run.gitCommit is "unknown"
