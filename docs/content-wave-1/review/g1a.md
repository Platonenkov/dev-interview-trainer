# Wave 1 review sheet: g1a

Questions shipped: 57; rows below: 31 flagged by risk plus a seeded 10% sample (3) of the other 26.

| path | level | type | risk | verified | executed | actual_result | links | verdict_rounds | warnings | owner_read |
|---|---|---|---|---|---|---|---|---|---|---|
| content/dotnet/csharp/nullable-rollout-legacy-solution.yaml | lead | single | lead/architect, repaired | 2026-11-15 | no |  | ok: Nullable migration strategies - C# \| Microsoft Learn; ok: Nullable reference types - C# \| Microsoft Learn; ok: Compiler Options - errors and warnings - C# reference \| Microsoft Learn | r1 fix, r2 pass, r2 pass |  |  |
| content/dotnet/types-memory/struct-conversion-policy-for-team.yaml | lead | open | lead/architect, repaired | 2026-11-14 | no |  | ok: .NET API changes that affect compatibility - .NET \| Microsoft Learn; ok: Choosing Between Class and Struct - Framework Design Guidelines \| Microsoft Learn; ok: Struct Design - Framework Design Guidelines \| Microsoft Learn | r1 pass, r2 pass |  |  |
| content/dotnet/oop-solid/growing-switch-on-payment-provider.yaml | senior | situation | situation, repaired | 2026-11-14 | no |  | ok: Clean Coder Blog; ok: Architectural principles - .NET \| Microsoft Learn; ok: Dependency injection - .NET \| Microsoft Learn; ok: Replace Conditional with Polymorphism | r1 fix, r2 pass |  |  |
| content/dotnet/csharp/generic-static-field-per-closed-type.yaml | senior | output | owner-review, repaired | UNVERIFIED (accepted: Owner decision 2026-11-13: remaining minor or unsure issues are re-verified from signed-in user feedback after launch; PR merge is the owner's acceptance.) | yes | output: 2 1 | ok: Classes - C# language specification \| Microsoft Learn; ok: Generic Classes - C# \| Microsoft Learn; ok: Generics in the runtime - C# \| Microsoft Learn | r1 fix, r2 fix |  |  |
| content/dotnet/csharp/record-equality-with-array-member.yaml | middle | output | owner-review, repaired | UNVERIFIED (accepted: Owner decision 2026-11-13: remaining minor or unsure issues are re-verified from signed-in user feedback after launch; PR merge is the owner's acceptance.) | yes | output: False True | ok: csharplang/proposals/csharp-9.0/records.md at main · dotnet/csharplang · GitHub; ok: Object.Equals Method (System) \| Microsoft Learn; ok: Records - C# reference \| Microsoft Learn | r1 fix, r2 fix |  |  |
| content/dotnet/csharp/static-lambda-capture-compile-error.yaml | senior | output | owner-review, repaired | UNVERIFIED (accepted: Owner decision 2026-11-13: remaining minor or unsure issues are re-verified from signed-in user feedback after launch; PR merge is the owner's acceptance.) | yes | compile-error: None | ok: csharplang/proposals/csharp-9.0/static-anonymous-functions.md at main · dotnet/csharplang · GitHub; ok: Lambda expressions - Lambda expressions and anonymous functions - C# reference \| Microsoft Learn | r2 fix |  |  |
| content/dotnet/oop-solid/square-rectangle-substitution-area.yaml | middle | output | owner-review, repaired | UNVERIFIED (accepted: Owner decision 2026-11-13: remaining minor or unsure issues are re-verified from signed-in user feedback after launch; PR merge is the owner's acceptance.) | yes | output: 16 | ok: override modifier - C# reference \| Microsoft Learn; ok: virtual keyword - C# reference \| Microsoft Learn; ok: https://www.cs.cmu.edu/~wing/publications/LiskovWing94.pdf | r1 fix, r2 fix |  |  |
| content/dotnet/csharp/closure-lowering-display-class.yaml | senior | open | repaired | 2026-11-14 | no |  | ok: Expressions - C# language specification \| Microsoft Learn; ok: Lambda expressions - Lambda expressions and anonymous functions - C# reference \| Microsoft Learn | r1 fix, r2 pass |  |  |
| content/dotnet/csharp/is-not-or-pattern-precedence.yaml | middle | find-bug | repaired | 2026-11-14 | yes | output: 2 | ok: Patterns - Pattern matching using the is and switch expressions. - C# reference \| Microsoft Learn | r1 fix, r2 pass | repair: level changed from senior to middle on request |  |
| content/dotnet/csharp/is-var-pattern-matches-null.yaml | middle | output | repaired | 2026-11-14 | yes | output: True False | ok: The `is` operator - Match an expression against a type or constant pattern - C# reference \| Microsoft Learn; ok: Patterns - Pattern matching using the is and switch expressions. - C# reference \| Microsoft Learn | r1 fix, r2 pass |  |  |
| content/dotnet/csharp/multicast-func-returns-last-value.yaml | middle | output | repaired | 2026-11-14 | yes | output: 3 | ok: Delegates - C# language specification \| Microsoft Learn; ok: How to combine delegates (Multicast Delegates) - C# \| Microsoft Learn; ok: Using Delegates - C# \| Microsoft Learn | r1 fix, r2 pass |  |  |
| content/dotnet/csharp/null-forgiving-operator-hides-null.yaml | junior | find-bug | repaired | 2026-11-14 | yes | output: User not found | ok: ! (null-forgiving) operator - C# reference \| Microsoft Learn; ok: Nullable reference types - C# \| Microsoft Learn | r2 pass |  |  |
| content/dotnet/csharp/nullable-attributes-notnullwhen.yaml | senior | open | repaired | 2026-11-14 | no |  | ok: NotNullWhenAttribute Class (System.Diagnostics.CodeAnalysis) \| Microsoft Learn; ok: Nullable reference types - C# \| Microsoft Learn; ok: Attributes interpreted by the compiler: Nullable static analysis - C# reference \| Microsoft Learn | r1 fix, r2 pass |  |  |
| content/dotnet/csharp/nullable-enable-effect-is-compile-time.yaml | junior | single | repaired | 2026-11-14 | no |  | ok: Nullable reference types - C# \| Microsoft Learn; ok: Nullable reference types - C# reference \| Microsoft Learn | r1 pass, r2 pass |  |  |
| content/dotnet/csharp/record-inheritance-equality-contract.yaml | senior | output | repaired | 2026-11-14 | yes | output: True False False | ok: Records - C# reference \| Microsoft Learn | r1 fix, r2 pass |  |  |
| content/dotnet/csharp/record-value-equality-output.yaml | junior | output | repaired | 2026-11-14 | yes | output: True False | ok: Object.ReferenceEquals(Object, Object) Method (System) \| Microsoft Learn; ok: Records - C# reference \| Microsoft Learn | r1 fix, r2 pass |  |  |
| content/dotnet/csharp/record-with-expression-leaves-original.yaml | junior | output | repaired | 2026-11-14 | yes | output: 30 31 | ok: Records - C# reference \| Microsoft Learn; ok: The with expression - create new objects that are modified copies of existing objects - C# reference \| Microsoft Learn | r1 fix, r2 pass |  |  |
| content/dotnet/oop-solid/default-interface-method-via-class-reference.yaml | middle | output | repaired | 2026-11-14 | yes | compile-error: CS1061 | ok: Safely update interfaces using default interface methods - C# \| Microsoft Learn; ok: interface keyword - C# reference \| Microsoft Learn | r1 fix, r2 pass |  |  |
| content/dotnet/oop-solid/fragile-base-class-double-count.yaml | middle | find-bug | repaired | 2026-11-14 | yes | output: 3 | ok: Object-oriented programming - inheritance - C# \| Microsoft Learn; ok: Virtual Members - Framework Design Guidelines \| Microsoft Learn | r1 fix, r2 pass |  |  |
| content/dotnet/oop-solid/getter-exposes-mutable-list.yaml | junior | find-bug | repaired | 2026-11-15 | yes | output: 2 35 | ok: List<T>.AsReadOnly Method (System.Collections.Generic) \| Microsoft Learn; ok: Classes, structs, and records - C# \| Microsoft Learn; ok: Guidelines for Collections - Framework Design Guidelines \| Microsoft Learn | r1 fix, r2 pass, r2 pass |  |  |
| content/dotnet/oop-solid/interface-segregation-definition.yaml | junior | single | repaired | 2026-11-14 | no |  | ok: Interface segregation principle - Wikipedia; ok: IReadOnlyList<T> Interface (System.Collections.Generic) \| Microsoft Learn | r1 fix, r2 pass |  |  |
| content/dotnet/oop-solid/single-responsibility-reason-to-change.yaml | junior | open | repaired | 2026-11-14 | no |  | ok: Architectural principles - .NET \| Microsoft Learn | r1 fix, r2 pass |  |  |
| content/dotnet/oop-solid/virtual-call-from-base-constructor.yaml | middle | output | repaired | 2026-11-14 | yes | output: Derived:0 Derived:42 | ok: virtual keyword - C# reference \| Microsoft Learn; ok: CA2214: Do not call overridable methods in constructors (code analysis) - .NET \| Microsoft Learn | r1 fix, r2 pass |  |  |
| content/dotnet/oop-solid/when-composition-beats-inheritance.yaml | middle | open | repaired | 2026-11-14 | no |  | ok: Object-oriented programming - inheritance - C# \| Microsoft Learn; ok: Designing for Extensibility - Framework Design Guidelines \| Microsoft Learn; ok: Guidelines for Collections - Framework Design Guidelines \| Microsoft Learn; ok: Virtual Members - Framework Design Guidelines \| Microsoft Learn | r1 fix, r2 pass |  |  |
| content/dotnet/types-memory/large-struct-copy-cost-vs-class.yaml | senior | open | repaired | 2026-11-14 | no |  | ok: Avoid memory allocations and data copies - C# \| Microsoft Learn; ok: Structure types - C# reference \| Microsoft Learn; ok: Method parameters and modifiers - C# reference \| Microsoft Learn; ok: Choosing Between Class and Struct - Framework Design Guidelines \| Microsoft Learn | r1 fix, r2 pass |  |  |
| content/dotnet/types-memory/list-enumerator-mutable-struct-copy.yaml | senior | find-bug | repaired | 2026-11-14 | yes | output: [1,Ann][2,Bob] | ok: List<T>.Enumerator Struct (System.Collections.Generic) \| Microsoft Learn; ok: Method parameters and modifiers - C# reference \| Microsoft Learn | r1 fix, r2 pass |  |  |
| content/dotnet/types-memory/readonly-field-mutable-struct-defensive-copy.yaml | middle | find-bug | repaired | 2026-11-14 | yes | output: 3 | ok: Structure types - C# reference \| Microsoft Learn; ok: readonly keyword - C# reference \| Microsoft Learn; ok: Expressions - C# language specification \| Microsoft Learn | r1 fix, r2 pass |  |  |
| content/dotnet/types-memory/ref-local-keeps-old-array-element.yaml | middle | output | repaired | 2026-11-14 | yes | output: 10 99 | ok: The multiple uses of the `ref` keyword - C# reference \| Microsoft Learn; ok: Declaration statements - local variables and constants, var, local reference variables (ref locals) - C# reference \| Microsoft Learn | r1 pass, r2 pass |  |  |
| content/dotnet/types-memory/struct-interface-call-mutates-boxed-copy.yaml | senior | output | repaired | 2026-11-14 | yes | output: 2 1 | ok: OpCodes.Constrained Field (System.Reflection.Emit) \| Microsoft Learn; ok: Structure types - C# reference \| Microsoft Learn; ok: Boxing and Unboxing - C# \| Microsoft Learn | r1 fix, r2 pass |  |  |
| content/dotnet/types-memory/value-types-not-always-on-stack.yaml | junior | open | repaired | 2026-11-14 | no |  | ok: Value types - C# reference \| Microsoft Learn; ok: Boxing and Unboxing - C# \| Microsoft Learn; ok: Fundamentals of garbage collection - .NET \| Microsoft Learn | r1 fix, r2 pass |  |  |
| content/dotnet/oop-solid/dependency-inversion-abstraction-ownership.yaml | middle | single | not executed | 2026-11-13 | no |  | ok: Architectural principles - .NET \| Microsoft Learn; ok: Dependency injection - .NET \| Microsoft Learn | r1 pass |  |  |
| content/dotnet/csharp/record-class-generated-members.yaml | junior | multi |  | 2026-11-13 | no |  | ok: Records - C# reference \| Microsoft Learn; ok: The init keyword - init only properties - C# reference \| Microsoft Learn | r1 pass |  |  |
| content/dotnet/types-memory/nullable-boxing-null-or-underlying.yaml | senior | output |  | 2026-11-13 | yes | output: True Int32 | ok: Nullable<T> Struct (System) \| Microsoft Learn; ok: Nullable value types - C# reference \| Microsoft Learn | r1 pass |  |  |
| content/dotnet/types-memory/span-ref-struct-allowed-locations.yaml | middle | multi |  | 2026-11-13 | no |  | ok: Span<T> Struct (System) \| Microsoft Learn; ok: ref struct types - C# reference \| Microsoft Learn | r1 pass |  |  |

## Totals by topic and level (shipped + seeds / target)

| topic | junior | middle | senior | lead | architect |
|---|---|---|---|---|---|
| csharp | 9/9 | 9/8 | 5/6 | 1/1 | 0/0 |
| types-memory | 5/5 | 7/7 | 5/5 | 1/1 | 0/0 |
| oop-solid | 7/7 | 6/6 | 3/3 | 0/0 | 0/0 |

## Types (this group; whole wave + seeds vs the wave target)

| type | group | wave | target |
|---|---|---|---|
| open | 10 | 100 | 100 +-5 |
| single | 9 | 80 | 80 +-5 |
| multi | 5 | 49 | 50 +-5 |
| output | 25 | 70 | 70 +-5 |
| find-bug | 7 | 45 | 45 +-5 |
| situation | 1 | 54 | 55 +-5 |

## Not fact-check verified (owner-review, meta.verified 1970-01-01)

- content/dotnet/csharp/record-equality-with-array-member.yaml: accepted at the final gate: Owner decision 2026-11-13: remaining minor or unsure issues are re-verified from signed-in user feedback after launch; PR merge is the owner's acceptance.
- content/dotnet/csharp/static-lambda-capture-compile-error.yaml: accepted at the final gate: Owner decision 2026-11-13: remaining minor or unsure issues are re-verified from signed-in user feedback after launch; PR merge is the owner's acceptance.
- content/dotnet/csharp/generic-static-field-per-closed-type.yaml: accepted at the final gate: Owner decision 2026-11-13: remaining minor or unsure issues are re-verified from signed-in user feedback after launch; PR merge is the owner's acceptance.
- content/dotnet/oop-solid/square-rectangle-substitution-area.yaml: accepted at the final gate: Owner decision 2026-11-13: remaining minor or unsure issues are re-verified from signed-in user feedback after launch; PR merge is the owner's acceptance.

## Shortfall

- none

## Rejected

- none
