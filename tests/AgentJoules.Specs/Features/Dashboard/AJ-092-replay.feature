@unit @AJ-S092 @REQ-032
Feature: AJ-092 Replay a committed run file on its recorded timeline
  @positive
  Scenario: AJ-092 replay emits one step-completed event per step
    Given a fixture run file with 12 steps
    When "dashboard --replay <fixture> --speed 50 --exit-after 5" is executed and a client reads "/events"
    Then the client received 12 step-completed events
    And the exit code is 0
  @positive
  Scenario: AJ-092 speed divides the recorded delays
    Given a fixture whose first two steps start 8 seconds apart
    When the timeline is replayed at speed 4 on a fake clock
    Then the second step start is emitted 2 seconds after the first
  @positive
  Scenario: AJ-092 all replay events are marked
    Given a fixture run file
    When the timeline is built
    Then every event has replay true and the page shows the replay banner
  @negative
  Scenario: AJ-092 invalid run file exits 2
    Given a file whose name does not match the run-file pattern
    When "dashboard --replay <file>" is executed
    Then the exit code is 2 and the output names the file
  @negative
  Scenario: AJ-092 non-positive speed exits 2
    Given a fixture run file
    When "dashboard --replay <fixture> --speed 0" is executed
    Then the exit code is 2
  @negative
  Scenario: AJ-092 hosted replay shows power as N/A not zero
    Given a nvidia fixture run file
    When the timeline is built
    Then every power event has label "N/A" and a null value
  @negative
  Scenario: AJ-092 no free port exits 5
    Given listeners hold 20 consecutive ports starting at the preferred port
    When "dashboard --replay <fixture> --exit-after 1" is executed
    Then the exit code is 5
  @positive
  Scenario: AJ-092 replay waits for the first client
    Given a started replay of a fixture run file and no connected client
    When the clock advances 30 seconds
    Then no event was emitted
    And after a client connects the first event is emitted
  @positive
  Scenario: AJ-092 a reconnecting client is rebuilt from the snapshot
    Given a replay in progress after 6 of 12 steps
    When the client disconnects and reconnects
    Then its first item is a snapshot holding the state after 6 steps
  @negative
  Scenario: AJ-092 no client within the timeout exits 0 with a warning
    Given a started replay with no client and --exit-after 60
    When 60 seconds pass on the fake clock
    Then a warning is printed and the exit code is 0
  @positive
  Scenario: AJ-092 Ctrl+C during a replay exits 0
    Given a replay in progress with a connected client
    When the cancellation token is signalled
    Then the replay server stops by itself and the exit code is 0
    And no file was written
