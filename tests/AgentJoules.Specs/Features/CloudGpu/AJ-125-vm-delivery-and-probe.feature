@integration @AJ-S125 @REQ-031
Feature: Create the VM when asked, deliver the CLI and probe the GPU over SSH
  @positive
  Scenario: AJ-125 the self-contained publish is ready before the VM exists
    Given the local Definition of Done commit exists
    When I run "dotnet publish src/AgentJoules.Cli -c Release -r linux-x64 --self-contained true -o artifacts/linux-x64"
    Then the exit code is 0
    And "git check-ignore artifacts/linux-x64" prints the path
  @positive
  Scenario: AJ-125 the VM matches the request and the probe is committed
    Given the user created the VM as requested and set CLOUDGPU_SSH_HOST and CLOUDGPU_SSH_USER
    When the builder copies the CLI and runs "./AgentJoules.Cli probe --json --save --suffix cloudgpu" on the VM
    Then the probe file has an accepted GPU name, a gpuUuid, migMode "disabled" or "not-supported" and a boolean energyCounterSupported
    And the file validates against the probe schema definition
  @positive
  Scenario: AJ-125 a fallback preset is accepted and recorded
    Given the preferred preset is unavailable and the user created preset "1gpu-8vcpu-32gb" on the same platform
    When the builder records the instance
    Then the Instance section names the preset used and why the fallback was needed
  @negative
  Scenario: AJ-125 a GPU class other than L40S stops the story
    Given nvidia-smi on the VM reports a GPU that is not an L40S
    When the builder checks the instance
    Then the builder stops and asks the user for a new decision
  @negative
  Scenario: AJ-125 a boot disk below 150 GB stops the story
    Given df on the VM reports a boot disk below 150 GB
    When the builder checks the instance
    Then the builder stops and asks the user to recreate the VM with a larger disk
  @negative
  Scenario: AJ-125 MIG enabled stops the story
    Given the VM probe reports migMode "enabled"
    When I run "run --tier cloudgpu --dry-run" on the VM
    Then the exit code is 6
    And the builder asks the user to disable MIG or recreate the VM
  @negative
  Scenario: AJ-125 the SSH variables are missing on the laptop
    Given CLOUDGPU_SSH_HOST is unset on the laptop
    When the builder tries to copy the CLI
    Then no scp command runs and the user is asked to set the variable
  @negative
  Scenario: AJ-125 Docker is absent from the image
    Given "docker --version" fails on the VM
    When the builder prepares the container runtime
    Then the install commands are recorded in the Commands section and counted in the box
