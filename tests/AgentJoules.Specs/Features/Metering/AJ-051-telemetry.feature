@unit @AJ-S051 @REQ-014
Feature: AJ-051 Record OpenTelemetry instruments and the activity source
  @positive
  Scenario: AJ-051 one local call records all five instruments with three attributes
    Given an in-memory metric exporter and a metered local fake call with measured energy and usage
    When the call completes and the meter provider is flushed
    Then the instruments energy, power, tokens, latency and cost each have a measurement
    And every measurement has attributes gen_ai.agent.name, aj.tier and gen_ai.request.model
  @positive
  Scenario: AJ-051 OTLP exporter is added when the endpoint variable is set
    Given the environment variable OTEL_EXPORTER_OTLP_ENDPOINT is set in the configuration override
    When the telemetry setup is built
    Then an OTLP exporter is registered
  @negative
  Scenario: AJ-051 hosted-tier call records no energy measurement
    Given an in-memory metric exporter and a metered fake call on tier "nvidia"
    When the call completes and the meter provider is flushed
    Then the energy and power instruments have no measurement
  @negative
  Scenario: AJ-051 call without usage records no token measurement
    Given an in-memory metric exporter and a metered fake call whose response has no usage
    When the call completes and the meter provider is flushed
    Then the tokens instrument has no measurement and no zero value
  @negative
  Scenario: AJ-051 console exporter is off with the opt-out flag
    Given the run command with --no-console-telemetry
    When the telemetry setup is built
    Then no console exporter is registered
