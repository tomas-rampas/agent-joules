@unit @AJ-S046 @REQ-013
Feature: AJ-046 Serialize metered windows with the process-wide meter gate
  @positive
  Scenario: AJ-046 two parallel branches run one after the other in serialized mode
    Given serialized mode and two metered clients that each take 1 second on a fake clock
    When both clients are called at the same time
    Then the two metering windows do not overlap
    And neither reading has flag "shared-window"
  @positive
  Scenario: AJ-046 gate wait is not counted as latency
    Given serialized mode and a first call that holds the gate for 1 second
    When a second call waits for the gate and then takes 200 ms
    Then the second reading has latencyMs 200
  @positive
  Scenario: AJ-046 concurrent mode uses the no-op gate
    Given concurrent mode
    When the gate is resolved from the service collection
    Then it is the no-op gate
  @negative
  Scenario: AJ-046 failing call releases the gate
    Given serialized mode and a first call that throws
    When a second call is started after the first failed
    Then the second call completes with outcome "ok"
  @negative
  Scenario: AJ-046 cancelled call releases the gate
    Given serialized mode and a first call that is cancelled mid-flight
    When a second call is started
    Then the second call completes with outcome "ok"
