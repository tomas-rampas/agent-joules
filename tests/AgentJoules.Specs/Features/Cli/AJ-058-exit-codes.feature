@unit @AJ-S058 @REQ-017
Feature: AJ-058 Map every failure to the documented exit code
  @positive
  Scenario: AJ-058 exception classes carry their documented code
    Given the exception types ConfigurationException, MissingKeyException, ModelNotFoundException, EndpointUnreachableException, MeasurementPreconditionException and ContentDiffersException
    When each is mapped by the exit code mapper
    Then the codes are 2, 3, 4, 5, 6 and 7 in that order
  @positive
  Scenario: AJ-058 successful command exits 0
    Given fake keys and fake endpoints
    When "run --tier nvidia --dry-run" is executed
    Then the exit code is 0
  @negative
  Scenario Outline: AJ-058 failure class maps to its exit code
    Given the failure "<failure>"
    When the command runs through the harness
    Then the exit code is <code>
    And stderr has exactly one line starting with "error:"

    Examples:
      | failure                         | code |
      | unexpected exception            | 1    |
      | unknown tier value              | 2    |
      | no API key                      | 3    |
      | model absent from models list   | 4    |
      | connection refused              | 5    |
      | GPU unavailable on local        | 6    |
      | content differs exception       | 7    |
  @negative
  Scenario: AJ-058 error line is redacted
    Given a failing endpoint and a runtime-built key starting with "nvapi-"
    When the command fails
    Then stderr does not contain any character of the key after the nvapi- prefix
  @negative
  Scenario: AJ-058 stack trace is hidden at the default log level
    Given an unexpected exception
    When the command fails at the default log level
    Then stderr has no stack frame lines
