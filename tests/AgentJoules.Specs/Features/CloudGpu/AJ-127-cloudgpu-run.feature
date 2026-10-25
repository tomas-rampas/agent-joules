@integration @AJ-S127 @REQ-031 @EDGE-015 @EDGE-016 @EDGE-017
Feature: Run the cloudgpu workload on the VM and copy the results back
  @positive
  Scenario: AJ-127 the dry-run passes on the VM
    Given the NIM container serves the model and the session environment is set
    When I run "./AgentJoules.Cli run --tier cloudgpu --dry-run" on the VM
    Then the exit code is 0
    And the output is recorded in docs/cloudgpu-run.md
  @positive
  Scenario: AJ-127 the full run completes with FP8 metadata
    Given the dry-run passed and the guard allows a run
    When I run "./AgentJoules.Cli run --tier cloudgpu --repeat 3" on the VM
    Then the run file has 36 steps, run.repeat 3 and run.status "completed"
    And environment.provider is "nebius-ai-cloud" and environment.precision is "FP8"
  @positive
  Scenario: AJ-127 the copied files validate on the laptop
    Given the run file was copied back with scp
    When I run "validate results/"
    Then the exit code is 0
    And the output contains "Validated" followed by the number of run files
  @positive
  Scenario: AJ-127 an SSH drop does not stop the run
    Given the run executes inside a tmux session on the VM
    When the SSH connection breaks and is re-established
    Then the run continues and finishes with status "completed"
  @negative
  Scenario: AJ-127 a lost VM leaves an excluded incomplete file
    Given the VM becomes unreachable during the run
    When the builder copies what exists and asks the user to check the console
    Then no retry is made past the box or the cap
    And the incident is recorded and the file is not used in any report
  @negative
  Scenario: AJ-127 the reported cost reaches 25 USD
    Given the user reports 25.00 USD observed on the billing page
    When the builder is about to start the next invocation
    Then no new invocation starts
    And the builder copies the existing results and tells the user to delete the VM
  @negative
  Scenario: AJ-127 the four-hour mark is reached
    Given 4 hours have passed since the handover
    When the builder is about to start the next invocation
    Then no new invocation starts and the stop reason "time-box" is recorded
  @negative
  Scenario: AJ-127 the judge key is missing on the VM
    Given the judge key is not set in the VM session
    When I run "./AgentJoules.Cli run --tier cloudgpu --dry-run" on the VM
    Then the exit code is 3
    And the output names "NVIDIA_API_KEY"
