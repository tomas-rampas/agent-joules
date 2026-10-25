@unit @AJ-S121 @REQ-031
Feature: Gate the cloudgpu tier on SSH variables, MIG and the tier flag
  @positive
  Scenario: AJ-121 both SSH variables set passes the gate
    Given CloudGpu:RequireSshVariables is true and both SSH variables are set to runtime-built fake values
    When I run "run --tier cloudgpu --dry-run"
    Then the SSH variable check passes
  @positive
  Scenario: AJ-121 the VM does not require the SSH variables
    Given CloudGpu:RequireSshVariables is false and the SSH variables are unset
    When I run "run --tier cloudgpu --dry-run"
    Then the SSH variable check passes
  @positive
  Scenario: AJ-121 MIG not supported counts as MIG off
    Given the fake NVML returns return code 3 for the MIG mode
    When I run "run --tier cloudgpu --dry-run"
    Then the MIG check passes and the probe reports migMode "not-supported"
  @positive
  Scenario: AJ-121 an enabled tier is placed second in the all-tier order
    Given Tiers:cloudgpu:Enabled is true
    When I run "run --tier all --dry-run"
    Then the tiers are listed in the order local, cloudgpu, nvidia, nebius
  @negative
  Scenario: AJ-121 a missing host variable exits 3
    Given CloudGpu:RequireSshVariables is true and CLOUDGPU_SSH_HOST is unset
    When I run "run --tier cloudgpu --dry-run"
    Then the exit code is 3
    And the output names "CLOUDGPU_SSH_HOST"
  @negative
  Scenario: AJ-121 both SSH variables missing are reported in one message
    Given both SSH variables are unset and RequireSshVariables is true
    When I run "run --tier cloudgpu --dry-run"
    Then the exit code is 3
    And the output names "CLOUDGPU_SSH_HOST" and "CLOUDGPU_SSH_USER" and prints no value
  @negative
  Scenario: AJ-121 MIG enabled exits 6
    Given the fake NVML reports MIG mode enabled
    When I run "run --tier cloudgpu --dry-run"
    Then the exit code is 6
    And the output names MIG
  @negative
  Scenario: AJ-121 the committed configuration leaves cloudgpu out of the all-tier run
    Given the committed configuration with Tiers:cloudgpu:Enabled false
    When I run "run --tier all --dry-run"
    Then the exit code is 0
    And no line for "cloudgpu" is printed
