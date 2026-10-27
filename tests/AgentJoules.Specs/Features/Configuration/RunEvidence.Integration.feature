@integration @AJ-S150 @REQ-032 @DoD
Feature: AJ-150 Probe and dashboard dry run on the laptop
  @positive
  Scenario: AJ-150 The dashboard dry run prints a loopback URL
    Given the laptop GPU and the local runtime are up
    When "run --tier local --dashboard --dry-run" runs
    Then the exit code is 0 and a line starts with "Dashboard:" and contains "http://127.0.0.1:"
  @negative
  Scenario: AJ-150 The dashboard dry run without a local runtime fails with a code and prints no key
    Given the local runtime is down
    When "run --tier local --dashboard --dry-run" runs
    Then the exit code is 5 and the output names the endpoint
  @positive
  Scenario: AJ-150 The committed probe matches the driver now installed
    Given the laptop GPU and nvidia-smi
    When "probe --json" runs
    Then the exit code is 0 and driverVersion equals the driver of the committed probe file
  @negative
  Scenario: AJ-150 A changed driver version is reported
    Given the committed probe names driver 616.92 and the installed driver is another version
    When the comparison runs
    Then it reports "driver changed since the committed probe" and the probe is rerun and committed
