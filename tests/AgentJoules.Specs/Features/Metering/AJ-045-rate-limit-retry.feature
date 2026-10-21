@unit @AJ-S045 @EDGE-006
Feature: AJ-045 Retry HTTP 429 with backoff and keep the wait out of latency
  @positive
  Scenario: AJ-045 two 429 responses then success are retried
    Given an endpoint that answers 429, 429 and then 200
    When a metered call is made with a fake clock
    Then the reading has retries 2 and outcome "ok"
    And the sink holds exactly 1 reading
  @positive
  Scenario: AJ-045 wait time is excluded from latency
    Given an endpoint that answers 429 once with Retry-After 2 and then 200 after a 100 ms call
    When a metered call is made with a fake clock
    Then waitMs is 2000
    And latencyMs is 100
  @positive
  Scenario: AJ-045 a 429 before the first stream update is retried
    Given a streaming endpoint that answers 429 and then streams normally
    When the stream is consumed
    Then the reading has retries 1 and outcome "ok"
  @negative
  Scenario: AJ-045 always-429 stops after 5 attempts
    Given an endpoint that answers 429 on every call
    When a metered call is made with a fake clock
    Then exactly 5 HTTP requests were recorded
    And the reading has outcome "error" and retries 4
  @negative
  Scenario: AJ-045 a 500 response is not retried
    Given an endpoint that answers 500
    When a metered call is made
    Then exactly 1 HTTP request was recorded
    And the reading has outcome "error" and retries 0
  @negative
  Scenario: AJ-045 a failure after a stream update is not retried
    Given a streaming endpoint that yields one update and then fails with 429
    When the stream is consumed
    Then exactly 1 HTTP request was recorded
    And the reading has outcome "error"
