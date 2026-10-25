@integration @AJ-S103 @REQ-019 @EDGE-010
Feature: Run the NIM-local attempt within 90 minutes
  @positive
  Scenario: AJ-103 the attempt succeeds and nim becomes the local runtime
    Given Docker Desktop with GPU support is running and NGC_API_KEY is set
    When the builder pulls and starts a Nemotron NIM and GET /v1/models returns a model ID within 90 minutes
    Then the Outcome section records the model ID and the memory readings
    And the Decision section says "Local runtime: nim"
  @positive
  Scenario: AJ-103 the attempt fails and the local tier falls back to Ollama with a README link
    Given the NIM container exits during start-up with an error
    When the builder records the decision
    Then the Decision section says "Local runtime: ollama"
    And README.md links "docs/nim-local-attempt.md" and states why NIM-local was not used
  @negative
  Scenario: AJ-103 the box expires at 90 minutes
    Given 90 minutes have passed since the first catalog search without a successful models call
    When the builder applies the hard stop
    Then the container is stopped and removed
    And the document records the status, the errors and the switch to Ollama
  @negative
  Scenario: AJ-103 the NGC login is rejected
    Given the NGC login to nvcr.io returns an authorization error
    When the builder writes the Outcome section
    Then the error text is recorded without any key value
    And the grep of the document for NGC_API_KEY prints nothing
  @negative
  Scenario: AJ-103 the container runs out of GPU memory
    Given the container starts and exits with an out-of-memory error on the 16 GB GPU
    When the builder writes the Outcome section
    Then the memory readings and the error are recorded
    And the Decision section says "Local runtime: ollama"
  @negative
  Scenario: AJ-103 no supported image exists for this GPU
    Given the catalog search finds no Nemotron NIM that supports compute capability 8.6
    When the builder writes the Image section
    Then the section states that none was found and the Decision falls back to Ollama
