@unit @AJ-S052 @EDGE-011
Feature: AJ-052 Exit 3 before any call when an API key is missing
  @positive
  Scenario: AJ-052 all required keys present passes the key check
    Given fake keys for NVIDIA_API_KEY and NEBIUS_API_KEY in the configuration override
    When "run --tier all --dry-run" is executed
    Then the key check passes and the exit code is not 3
  @positive
  Scenario: AJ-052 missing judge key outside dry-run only warns
    Given NEBIUS_API_KEY is present and the judge key is absent
    When "run --tier nebius" is executed with fake endpoints
    Then a warning names the judge key and the exit code is not 3
  @negative
  Scenario: AJ-052 dry-run lists every missing key in one message
    Given an empty environment
    When "run --tier all --dry-run" is executed
    Then the exit code is 3
    And one message names "NVIDIA_API_KEY" and "NEBIUS_API_KEY"
    And the message shows the user-secrets alternative with the placeholder "<value>"
  @negative
  Scenario: AJ-052 key value is never printed when the endpoint fails
    Given a runtime-built key starting with "nvapi-" and an endpoint that refuses the connection
    When "run --tier nvidia" is executed
    Then the output does not contain any character of the key after the nvapi- prefix
  @negative
  Scenario: AJ-052 no request is sent when a key is missing
    Given tier "nebius" selected and NEBIUS_API_KEY absent
    When "run --tier nebius" is executed
    Then the exit code is 3
    And the fake handler recorded 0 requests
  @negative
  Scenario: AJ-052 missing SSH variable for cloudgpu on the laptop exits 3
    Given tier "cloudgpu" enabled with RequireSshVariables true and CLOUDGPU_SSH_HOST absent
    When "run --tier cloudgpu --dry-run" is executed
    Then the exit code is 3
    And the message names "CLOUDGPU_SSH_HOST"
  @positive
  Scenario: AJ-052 harness run with only fakes performs no real request
    Given a harness run with fake keys and a fake models handler
    When "run --tier nvidia --dry-run" is executed
    Then the deny-all handler recorded 0 real requests
  @negative
  Scenario: AJ-052 unconfigured URL is denied naming the URL
    Given a harness run where a tier base URL has no fake route
    When a request to that URL is attempted
    Then the deny-all handler throws and the message names the URL
