@unit @AJ-S041 @REQ-007
Feature: AJ-041 Meter buffered and streaming calls with exactly one reading per call
  @positive
  Scenario: AJ-041 buffered call yields one reading with ttft N/A
    Given a metered client for agent "Planner" over a fake client that answers in one response
    When the client is called with GetResponseAsync
    Then the sink holds exactly 1 reading for agent "Planner"
    And the reading has ttftMs label "N/A"
  @positive
  Scenario: AJ-041 streaming call records time to first content update
    Given a metered client for agent "Writer" over a fake stream whose first update arrives after 300 ms
    When the stream is consumed to completion
    Then the sink holds exactly 1 reading with outcome "ok"
    And ttftMs is within 20 ms of 300
  @positive
  Scenario: AJ-041 decorator composes through ChatClientBuilder
    Given a fake inner client
    When the decorator is added with ChatClientBuilder.Use and built
    Then calling the built client publishes 1 reading
  @negative
  Scenario: AJ-041 cancelled stream yields one cancelled reading
    Given a metered client over a fake stream with 5 updates
    When the caller cancels after the second update
    Then the sink holds exactly 1 reading with outcome "cancelled"
  @negative
  Scenario: AJ-041 failing stream yields one error reading and rethrows
    Given a metered client over a fake stream that throws after the first update
    When the stream is consumed
    Then the exception reaches the caller
    And the sink holds exactly 1 reading with outcome "error"
  @negative
  Scenario: AJ-041 overlapping call on one instance is rejected
    Given a metered client with a call in flight
    When a second call starts on the same instance
    Then InvalidOperationException is thrown
