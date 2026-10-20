@unit @AJ-S027 @REQ-004 @EDGE-002
Feature: Choose the counter or the sampling method and disclose the fallback
  @positive
  Scenario: AJ-027 Auto uses the counter when it is supported
    Given a fake NVML whose counter is supported
    When the method is selected with Meter:Method auto
    Then the method is "counter" and IsFallback is false
  @positive
  Scenario: AJ-027 Auto falls back to sampling and flags every reading
    Given a fake NVML returning NOT_SUPPORTED for the counter
    When the method is selected with Meter:Method auto and a reading is computed
    Then the method is "sampling", the reading flags contain "sampling-fallback" and Describe says "Energy method: sampling (counter unsupported)"
  @positive
  Scenario: AJ-027 Forced sampling is not a fallback
    Given a fake NVML whose counter is supported
    When the method is selected with Meter:Method sampling
    Then the method is "sampling" and the flags do not contain "sampling-fallback"
  @negative
  Scenario: AJ-027 Forced counter on an unsupported GPU is a precondition failure
    Given a fake NVML returning NOT_SUPPORTED for the counter
    When the method is selected with Meter:Method counter
    Then a MeasurementPreconditionException with exit code 6 is thrown and its message names "Meter:Method=counter"
  @negative
  Scenario: AJ-027 An unknown method is a configuration error
    Given Meter:Method is "turbo"
    When the method is selected
    Then a ConfigurationException with exit code 2 is thrown
  @negative
  Scenario: AJ-027 Any non-zero counter return code means unsupported
    Given the counter read returns rc 999
    When the method is selected with Meter:Method auto
    Then the method is "sampling" with reason "counter-not-supported" including the code
