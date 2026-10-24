@unit @AJ-S089 @REQ-032 @EDGE-019
Feature: AJ-089 Fan readings out to a dashboard sink that can never block the run
  @positive
  Scenario: AJ-089 a reading reaches both the in-memory sink and the dashboard sink
    Given a fan-out sink over an in-memory sink and a dashboard sink
    When one reading is published
    Then the in-memory snapshot holds 1 reading
    And the dashboard sink received a step-completed event
  @positive
  Scenario: AJ-089 without the dashboard the null live sink is used
    Given a run without --dashboard
    When the live sink is resolved
    Then it is the null live sink
  @negative
  Scenario: AJ-089 a throwing target does not break the other target
    Given a fan-out sink whose second target throws on every reading
    When 5 readings are published
    Then the in-memory sink holds 5 readings
    And no exception reached the caller
  @negative
  Scenario: AJ-089 a slow client keeps only the newest 1000 events
    Given a connected client that never reads
    When 1500 events are emitted
    Then the connection buffer holds 1000 events and they are the newest 1000
  @negative
  Scenario: AJ-089 emit returns while the pump is faulted
    Given a dashboard sink whose pump throws on the first event
    When 10 events are emitted
    Then every emit call returned normally
    And the pump error was logged once
  @positive
  Scenario: AJ-089 power interval within range is accepted
    Given Dashboard:PowerIntervalMs is set to 100
    When any command starts
    Then configuration validation passes
  @positive
  Scenario: AJ-089 power samples are forwarded at the configured interval without extra NVML calls
    Given a scripted NVML API and Dashboard:PowerIntervalMs 100 on a fake clock
    When a 1 second window is metered
    Then at most 10 power events were emitted
    And the NVML call count equals the count of a run without the dashboard
  @positive
  Scenario: AJ-089 streaming progress is throttled to ten events per second
    Given a streaming step that delivers 100 output chunks within 1 second on a fake clock
    When the step is metered
    Then at most 10 step-progress events were emitted and the last chunk count is monotonic
  @negative
  Scenario: AJ-089 power interval below 50 exits 2
    Given Dashboard:PowerIntervalMs is set to 25
    When any command starts
    Then the exit code is 2
    And the output names "Dashboard:PowerIntervalMs"
  @negative
  Scenario: AJ-089 power interval above 1000 exits 2
    Given Dashboard:PowerIntervalMs is set to 1001
    When any command starts
    Then the exit code is 2
  @negative
  Scenario: AJ-089 a buffered call emits no step-progress event
    Given a buffered step
    When the step is metered
    Then no step-progress event was emitted
  @negative
  Scenario: AJ-089 an interval shorter than the sampling period forwards every sample
    Given a meter sampling every 200 ms and Dashboard:PowerIntervalMs 100
    When a 1 second window is metered
    Then every sample is forwarded as a power event
    And one startup note says the interval is shorter than the sampling period
