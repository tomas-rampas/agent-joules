@unit @AJ-S032 @REQ-005
Feature: Share one meter gate per process
  @positive
  Scenario: AJ-032 The second acquire waits for the first release
    Given a serialized gate held by task A
    When task B calls AcquireAsync
    Then task B is still waiting until task A disposes its releaser, and then completes
  @positive
  Scenario: AJ-032 Serialized windows never overlap
    Given three tasks that each acquire the serialized gate around a deterministic window
    When all tasks finish
    Then the three window spans are pairwise disjoint
  @positive
  Scenario: AJ-032 The no-op gate admits everyone at once
    Given a no-op gate
    When two tasks acquire it at the same time
    Then both complete immediately
  @negative
  Scenario: AJ-032 Cancelling a waiter does not leak the gate
    Given a serialized gate held by task A and a waiting task B that is cancelled
    When task A releases and task C acquires
    Then task B ended with OperationCanceledException and task C acquired successfully
  @negative
  Scenario: AJ-032 A double release admits only one waiter
    Given a serialized gate held by task A with tasks B and C waiting
    When task A disposes its releaser twice
    Then only task B is admitted
