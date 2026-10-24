@unit @AJ-S091 @REQ-032 @EDGE-018
Feature: AJ-091 Serve the dashboard on loopback with SSE and a port fallback
  @positive
  Scenario: AJ-091 dry-run prints the dashboard URL and exits 0
    Given the run command with fake keys, a scripted GPU and the dashboard enabled
    When "run --tier local --dashboard --dry-run" is executed
    Then the output has a line starting "Dashboard:" containing "http://127.0.0.1:"
    And the exit code is 0
  @positive
  Scenario: AJ-091 events are sent as raw data lines
    Given a started dashboard server with one emitted step event
    When a client reads "/events"
    Then the response content type is "text/event-stream"
    And the first item is a snapshot and a later line starts with "data: {"
  @positive
  Scenario: AJ-091 occupied preferred port falls back to the next free port
    Given a listener holds the preferred port
    When the dashboard starts during a run
    Then it reports a different port and the run completes with exit code 0
  @negative
  Scenario: AJ-091 all twenty ports busy lets the run continue without a dashboard
    Given listeners hold 20 consecutive ports starting at the preferred port
    When "run --tier nvidia --dashboard --repeat 1" is executed
    Then a warning is printed and no "Dashboard:" line appears
    And the exit code is 0 and the results file is complete
  @negative
  Scenario: AJ-091 no dashboard flag means no listener
    Given the run command without --dashboard
    When "run --tier nvidia --repeat 1" is executed
    Then no "Dashboard:" line appears and no port was opened
  @negative
  Scenario: AJ-091 invalid dashboard port exits 2
    Given the run command
    When "run --tier nvidia --dashboard --dashboard-port 70000 --dry-run" is executed
    Then the exit code is 2
  @positive
  Scenario: AJ-091 every response carries the CSP and nosniff headers
    Given a started dashboard server
    When a client requests "/" and "/events"
    Then both responses carry the Content-Security-Policy and X-Content-Type-Options "nosniff" headers
  @negative
  Scenario: AJ-091 a foreign Host header gets 421
    Given a started dashboard server
    When a client sends a request with Host "evil.example"
    Then the status is 421 and no event data is returned
  @positive
  Scenario: AJ-091 every live event arrives within 250 ms on loopback
    Given a started dashboard server, a headless SSE client and the system clock
    When a fixture run emits at least 50 events including power events at the default interval
    Then each event was received within 250 ms of its atUtc
  @negative
  Scenario: AJ-091 the latency check detects a delayed delivery
    Given a dashboard server variant that delays delivery by 400 ms
    When a fixture run emits its events
    Then the latency check fails and reports a maximum above 250 ms
