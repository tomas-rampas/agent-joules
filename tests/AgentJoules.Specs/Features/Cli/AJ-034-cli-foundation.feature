@unit @AJ-S034 @REQ-006
Feature: Build the CLI foundation with exit codes and redacted output
  @positive
  Scenario: AJ-034 Help exits with code 0
    Given the CLI harness with no overrides
    When I run "probe --help"
    Then the exit code is 0 and stdout lists the probe options
  @positive
  Scenario: AJ-034 A precondition failure maps to exit code 6
    Given a service override that throws MeasurementPreconditionException
    When I run "probe"
    Then the exit code is 6 and stderr has one line starting "error:"
  @positive
  Scenario: AJ-034 A configuration failure maps to exit code 2
    Given Meter:SampleRateHz is 5 without the override in the in-memory configuration
    When I run "probe"
    Then the exit code is 2
  @negative
  Scenario: AJ-034 An unknown option exits with code 2, not 1
    Given the CLI harness
    When I run "probe --bogus"
    Then the exit code is 2
  @negative
  Scenario: AJ-034 An unexpected exception exits with code 1
    Given a service override that throws InvalidOperationException
    When I run "probe"
    Then the exit code is 1 and stderr shows "error: <message> (InvalidOperationException)"
  @negative
  Scenario: AJ-034 A key inside an error message is redacted
    Given a service override that throws an exception whose message contains a runtime-built nvapi key
    When I run "probe"
    Then stderr contains "nvapi-****" and does not contain the key suffix
  @negative
  Scenario: AJ-034 The laptop's real keys never reach a test
    Given NVIDIA_API_KEY is set in the process environment
    When the harness runs with the default overrides
    Then the configuration seen by the command has no NVIDIA_API_KEY value
  @positive
  Scenario: AJ-034 Host shutdown cancels the running command and exits 5
    Given a fake long-running command that waits on its cancellation token
    When the host raises ApplicationStopping (the Ctrl+C, SIGTERM and SIGHUP path)
    Then the command sees the cancellation, the exit code is 5 and ProcessTerminationTimeout is 15 s (the incomplete run state is written by AJ-056-T6a, not asserted here)
  @negative
  Scenario: AJ-034 A cancellation that is not a shutdown exits 1
    Given a command that throws OperationCanceledException with no shutdown requested
    When I run it through the harness
    Then the exit code is 1 and stderr has one "error:" line
  @positive
  Scenario: AJ-034 Output without a trailing newline is drained at exit
    Given a fake command that writes a JSON document with Write and no newline
    When I run it through the harness
    Then stdout holds the complete JSON after RunAsync returns
  @negative
  Scenario: AJ-034 An exception escaping a worker thread is redacted and exits 1
    Given the Cli started as a child process with the hidden worker-throw switch and a runtime-built nvapi key in the message
    When the worker thread throws
    Then the process exits 1, stderr contains "error:" and "nvapi-****" and does not contain the key suffix
  @negative
  Scenario: AJ-034 An unregistrable key value warns by variable name only
    Given the in-memory configuration sets NVIDIA_API_KEY to a four-character value
    When I run "probe"
    Then stderr contains "warning" and "NVIDIA_API_KEY" and does not contain the value
