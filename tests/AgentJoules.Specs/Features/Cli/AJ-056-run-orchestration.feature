@unit @AJ-S056 @REQ-017 @REQ-013
Feature: AJ-056 Orchestrate a tier run over tasks, repeats and modes
  @positive
  Scenario: AJ-056 run produces tasks x repeats x agents readings
    Given fake clients, 3 committed tasks and tier "nvidia"
    When "run --tier nvidia --repeat 2" is executed
    Then the results file has 24 steps
    And run.status is "completed" and the exit code is 0
  @positive
  Scenario: AJ-056 tier all writes one file per enabled tier in fixed order
    Given the committed configuration with cloudgpu disabled and fakes for the other tiers
    When "run --tier all --repeat 1" is executed
    Then 3 run files exist for tiers "local", "nvidia" and "nebius"
  @positive
  Scenario: AJ-056 step line uses labels instead of zero
    Given a nvidia step whose response has no usage
    When the run prints the step line
    Then the line contains "netJ=N/A", "in=not-reported" and "out=not-reported"
  @negative
  Scenario: AJ-056 unknown task name exits 2
    Given the committed tasks
    When "run --tier nvidia --task bogus" is executed
    Then the exit code is 2
  @negative
  Scenario: AJ-056 missing key for any selected tier stops before the first call
    Given NEBIUS_API_KEY is absent and tiers "nvidia" and "nebius" are selected
    When "run --tier all" is executed
    Then the exit code is 3
    And no run file exists and the fake handler recorded 0 requests
  @negative
  Scenario: AJ-056 invalid mode value exits 2
    Given the run command
    When "run --tier nvidia --mode parallel" is executed
    Then the exit code is 2
  @positive
  Scenario: AJ-056 Ctrl+C mid-step leaves an incomplete file and exits 5
    Given a nvidia run whose second step is in flight
    When the cancellation token is signalled
    Then the results file has run.status "incomplete" and the in-flight step has outcome "incomplete"
    And the file was written before the termination timeout and the exit code is 5
  @negative
  Scenario: AJ-056 a run that completes normally never carries incomplete
    Given a nvidia run that finishes without interruption
    When the results file is read
    Then run.status is "completed" and no step has outcome "incomplete"
