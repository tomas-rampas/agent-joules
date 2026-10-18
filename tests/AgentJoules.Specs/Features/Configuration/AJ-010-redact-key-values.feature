@unit @AJ-S010 @REQ-029
Feature: Redact key values in any text output
  @positive
  Scenario: AJ-010 A registered key is masked to six characters plus stars
    Given a redactor registered with a runtime-built opaque key
    When I redact "request failed for <key> twice <key>"
    Then both occurrences become the first six characters plus "****" and the suffix is absent
  @positive
  Scenario: AJ-010 An unregistered nvapi value is masked by pattern
    Given a redactor with no registered values
    When I redact a runtime-built "nvapi-" plus 16 characters
    Then the result is "nvapi-****"
  @positive
  Scenario: AJ-010 A key split across two writes is masked
    Given a RedactingTextWriter over a StringWriter
    When "nvapi-" and "abcdef1234567890" are written separately and then a newline
    Then the inner writer receives only "nvapi-****" followed by the newline
  @negative
  Scenario: AJ-010 An empty configured value is ignored
    Given a redactor registered with null, empty and whitespace values
    When I redact "plain text"
    Then the text is unchanged and no exception is thrown
  @negative
  Scenario: AJ-010 A short configured value is fully masked
    Given a redactor registered with the 5-character value "abc12"
    When I redact "token abc12 failed"
    Then the output is "token **** failed" because six characters would reveal the whole value
  @negative
  Scenario: AJ-010 Null input yields an empty string
    Given a redactor
    When I redact null
    Then the result is an empty string and no exception is thrown
  @negative
  Scenario: AJ-010 Redaction is stable on already masked text
    Given the text "nvapi-****"
    When I redact it twice
    Then both results equal "nvapi-****"
