@unit @AJ-S029 @REQ-004 @REQ-005
Feature: Sample power and serve measurement windows
  @positive
  Scenario: AJ-029 Ten hertz for two seconds takes 21 reads
    Given a started meter at 10 Hz over a fake NVML
    When the fake clock advances 2.0 s
    Then the fake recorded exactly 21 power reads
  @positive
  Scenario: AJ-029 A three second window at 60 W is 180 J
    Given a started meter and a scripted constant 60 W
    When a window is opened, the clock advances 3 s and the window is closed
    Then gross is 180 J within 1 percent and average is 60 W
  @positive
  Scenario: AJ-029 The counter supplies gross while samples supply average
    Given a started meter on the counter method
    When a window spans 3 s at 60 W
    Then gross is 180 J from the counter delta and sampleCount comes from the samples
  @positive
  Scenario: AJ-029 Overlapping windows report their own energy
    Given windows opened at 0 s and 1 s on one meter
    When both close at 3 s at a constant 60 W
    Then the first reports 180 J and the second reports 120 J
  @negative
  Scenario: AJ-029 Opening a window before start is rejected
    Given a meter that was not started
    When OpenWindow is called
    Then an InvalidOperationException is thrown
  @negative
  Scenario: AJ-029 Closing a window twice is rejected
    Given a closed window
    When Close is called again
    Then an InvalidOperationException is thrown
  @negative
  Scenario: AJ-029 A failed power read is skipped, not recorded as zero
    Given the fake throws on the third power read
    When the clock advances 1 s
    Then no sample with 0 W exists and the loop is still running
  @negative
  Scenario: AJ-029 Disposing the meter stops the sampling
    Given a started meter
    When the meter is disposed and the clock advances 5 s
    Then the fake records no further power reads
