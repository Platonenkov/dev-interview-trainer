# ADR 0006: Application references Contracts

## Status

Accepted (approved by the owner). It changes the reference rules of design spec section 8.1, where `Trainer.Application`
references `Trainer.Domain` only.

## Context

The practice islands run in WebAssembly and exchange JSON with the server through the DTOs of `Trainer.Contracts`
(questions with correctness and per-line code, session requests and responses, answer batches, profile, errors). The
session query services in `Trainer.Application` produce exactly those shapes: a question for a session is the question
page model plus the code lines, minus the page-only fields.

If Application kept its own model and Infrastructure or Web mapped it to DTOs, the model would be a field-for-field copy
of `QuestionDto`, `SessionDto` and `MockSessionDto`, and every change to the wire format would touch both copies and the
mapping between them.

`Trainer.Contracts` is a leaf project: it references nothing, holds only records, constants and the source-generated
`TrainerJsonContext`, and is trimming-safe because the WebAssembly client shares it.

## Decision

`Trainer.Application` references `Trainer.Contracts`. The session query interface (`ISessionQueries`) returns the wire
DTOs directly. Reference rules after this change:

- Domain and Contracts reference nothing.
- Application references Domain and Contracts.
- Web.Client references Domain and Contracts, never Application, Infrastructure or Content.
- Contracts never references any other project, so the dependency cannot become a cycle.

Query criteria stay Application types (`PracticeCriteria`, `MockCriteria`, `ReviewCriteria`) built from Domain value types;
the request DTOs are parsed into them at the API edge, so wire strings never reach the query services.

## Consequences

- One definition of every wire shape; the query services need no mapping layer to DTOs.
- A change to a DTO is a wire-format change and is reviewed as one; Application code that returns DTOs changes with it.
- Application now has a (leaf, framework-free) dependency beyond Domain; the layering rule "dependencies point inward"
  still holds because Contracts depends on nothing.
