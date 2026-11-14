# ADR 0001: Grading lives in Trainer.Domain

## Status

Accepted (approved by the owner).

## Context

Design spec section 8.1 lists `IAnswerGrader` under the Application layer. Spec section 11.5 requires that
single, multi, output and find-bug questions are graded in the WebAssembly client, so that answers work offline
and the server never receives raw picks. The WASM client (`Trainer.Web.Client`) references `Trainer.Domain` and
`Trainer.Contracts` only; it must not reference `Trainer.Application`, `Trainer.Infrastructure` or
`Trainer.Content`. Grading placed in Application would therefore be unreachable from the client.

## Decision

Grading and `IAnswerGrader` live in `Trainer.Domain` (namespace `Trainer.Domain.Grading`):

- `Grade` (`DidntKnow = 0`, `Partial = 1`, `Knew = 2`) and `AnswerMode` (`Practice = 1`, `Mock = 2`, `Review = 3`)
  with case-sensitive wire keys (`didnt-know`, `partial`, `knew`; `practice`, `mock`, `review`).
- `AutoGrading`: pure functions for single, multi, output and find-bug questions. A used hint caps the result at
  `Partial`.
- `OutputText.Normalise`: the line-ending and trim rule that the content validator already applied to accepted
  outputs, now shared so the client compares typed output exactly as the validator checked it.
- `IAnswerGrader` and `SelfAssessmentGrader` for open and situation questions. `SelfGradedAnswer.Text` is
  reserved for a future LLM grader and ignored in v1.

## Consequences

- The client and the server run the same grading code; there is no second implementation to keep in sync.
- Domain stays free of IO and framework dependencies, so it is safe to ship in the WASM payload.
- A future LLM-based `IAnswerGrader` needs network access and belongs in Application or Infrastructure; it plugs
  in behind the same interface without moving the Domain types.
- Spec section 8.1 differs from the implementation on this one point; this ADR is the record of the deviation.
