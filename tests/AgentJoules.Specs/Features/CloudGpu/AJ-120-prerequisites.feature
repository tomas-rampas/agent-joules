@integration @AJ-S120 @REQ-031
Feature: Create the AI Cloud account, card and SSH key pair
  @positive
  Scenario: AJ-120 the user confirms the funded account
    Given the user has created the AI Cloud account and project and paid the minimum first payment
    When the user confirms in the chat
    Then docs/build-log.md records a dated confirmation line without account details
  @positive
  Scenario: AJ-120 the key pair stays outside the repository
    Given the user ran "ssh-keygen -t ed25519" and stored the private key outside the repository
    When the builder lists the tracked files
    Then no tracked file name contains "id_ed25519", "id_rsa" or ".pem"
    And "git check-ignore" prints all three ignore patterns
  @negative
  Scenario: AJ-120 a private key inside the repository is rejected
    Given a file named "id_ed25519" exists in the repository working tree
    When the builder runs the key hygiene check
    Then the check fails and the file is reported without printing its content
  @negative
  Scenario: AJ-120 no card or payment blocks the tier
    Given the user has not added a card or made the first payment
    When the builder prepares the VM request
    Then the request is not sent
    And the final report lists the cloudgpu tier as blocked on step 10
  @negative
  Scenario: AJ-120 hackathon credits are assumed to cover AI Cloud
    Given the user expects Token Factory credits to pay for the VM
    When the builder states the cost rules
    Then the user is told that the credits do not apply and that the USD 25 payment is required
