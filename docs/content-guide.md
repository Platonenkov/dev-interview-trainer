# Content guide

How to write a question for Dev Interview Trainer, what the validator checks,
and how to verify a question before you open a pull request.

The question bank lives in `content/` and is licensed under CC BY-SA 4.0
(`content/LICENSE`). By submitting a question you agree to publish it under that
licence. Write the text yourself: do not paste text from books, blogs, courses
or interview-question sites.

## Contents

1. [Before you start](#before-you-start)
2. [Layout of `content/`](#layout-of-content)
3. [File format rules](#file-format-rules)
4. [Create a question](#create-a-question)
5. [Field reference](#field-reference)
6. [Rules per question type](#rules-per-question-type)
7. [Complete examples](#complete-examples)
8. [Code, expectations and the snippet sandbox](#code-expectations-and-the-snippet-sandbox)
9. [Markdown in text fields](#markdown-in-text-fields)
10. [Quality bar](#quality-bar)
11. [Ids, slugs, renames, redirects and retiring](#ids-slugs-renames-redirects-and-retiring)
12. [Stacks, topics and redirects files](#stacks-topics-and-redirects-files)
13. [Verify locally](#verify-locally)
14. [Editor setup](#editor-setup)
15. [Issue codes](#issue-codes)
16. [Pull request checklist](#pull-request-checklist)

## Before you start

- A question is one YAML file. It carries both languages: Russian (`ru`) and
  English (`en`). Every new question needs both.
- You need the .NET SDK pinned in `global.json` to run `validate`. Docker is
  needed only for `run-snippets`.
- Run every command in this guide from the repository root.
- Accuracy matters more than volume. A question that is wrong is worse than a
  question that is missing.

## Layout of `content/`

```text
content/
  LICENSE                          CC BY-SA 4.0 notice
  schema-version                   content format version, currently 1
  stacks.yaml                      list of stacks with localised titles
  schema/*.schema.json             JSON schemas for editors, generated from the C# model
  dotnet/                          one directory per stack (the stack key)
    topics.yaml                    topics of the stack with localised titles
    redirects.yaml                 renamed questions, optional
    async/                         one directory per topic (the topic key)
      result-deadlock-ui-thread.yaml
```

A question file must be stored at `{stack}/{topic}/{slug}.yaml`. The stack,
topic and slug inside the file must match the path (`path-mismatch`).

Directories whose name starts with a dot and the `schema` directory are not
read as stacks.

## File format rules

The reader is strict on purpose, so a typo cannot silently drop data.

- The file is UTF-8. A BOM and CRLF line endings are accepted and ignored: the
  content hash is computed after removing them. The repository stores `*.yaml`
  with LF (`.gitattributes`).
- One YAML document per file. Anchors, aliases, merge keys (`<<`) and explicit
  tags (`!!str`) are rejected (`yaml-alias-forbidden`, `content-structure`).
- Mapping keys are camelCase (`bugLineSets`, `acceptedOutputs`, `fixedSource`,
  `optionExplanations`, `linkTitles`, `dotnetVersion`, `noRunReason`). An
  unknown key is an error (`content-structure`).
- Plain (unquoted) scalars are typed narrowly: `true` and `false` are booleans,
  an empty value, `null` and `~` are null, and a whole number such as `333` or
  `10` is an integer. Everything else is text, including `1.5`, `012` and
  `2026-11-11`.
- Fields that hold text must be text. **Quote anything that looks like a
  number**: `value: "333"`, `dotnetVersion: "10"`, an option text `"012"`.
  An unquoted `333` where text is expected is a `content-structure` error.
- A required field must not be null or empty. Lists that may be empty are
  written `[]`.
- Use `|` block scalars for Markdown and code. Indent the block consistently.

## Create a question

1. Pick the stack, the topic from `content/{stack}/topics.yaml`, and a slug in
   lowercase kebab-case, at most 80 characters. The slug becomes part of the
   URL and never changes.
2. Generate the skeleton. The command creates the file at the right path with a
   new ULID, both locale blocks and valid structure:

   ```bash
   dotnet run --project tools/Trainer.ContentCli -- new-question dotnet async my-new-slug single
   ```

   The last argument is the type: `open`, `single`, `multi`, `output`,
   `find-bug` or `situation`. An optional fifth argument is the content root
   (default `content`). The command refuses to overwrite an existing file.
3. Replace every placeholder text ("Replace this text"), the placeholder link,
   the author and the level. Set `meta.verified` to the date you checked the
   facts.
4. Validate (see [Verify locally](#verify-locally)). For `output` and
   `find-bug` questions also run the snippets.
5. Open a pull request with the `content:` commit prefix.

Never invent an `id` by hand. Never copy the `id` of another question: a
duplicate is `id-duplicate`.

## Field reference

### Top level

| Field | Required | Meaning |
|---|---|---|
| `id` | yes | ULID: 26 characters of uppercase Crockford base32, first character `0` to `7`. Immutable, never reused. |
| `slug` | yes | URL part, lowercase kebab-case, at most 80 characters, unique per stack, the same for all languages. |
| `stack` | yes | Stack key from `stacks.yaml`. |
| `topic` | yes | Topic key from the stack `topics.yaml`. |
| `level` | yes | `junior`, `middle`, `senior`, `lead` or `architect`. |
| `type` | yes | `open`, `single`, `multi`, `output`, `find-bug` or `situation`. |
| `grading` | yes | `self` for `open` and `situation`, `auto` for the other types. |
| `tags` | yes | List of lowercase kebab-case strings. May be `[]`. |
| `code` | depends | Code shown with the question, see below. Required for `output` and `find-bug`. |
| `bugLineSets` | find-bug | Accepted selections of buggy lines. |
| `fixedSource` | no | find-bug: corrected code shown in the explanation. |
| `fixedExpect` | with `fixedSource` | What the corrected code produces. |
| `expect` | output | The machine-checked result of `code`. |
| `acceptedOutputs` | output | Accepted typed answers. |
| `options` | by type | Answer options, language-neutral part. |
| `links` | yes | Sources. At least one is required (`links-required`). |
| `locales` | yes | Text per language, `ru` and `en`. |
| `meta` | yes | Authorship and freshness. |

### `code`

| Field | Required | Meaning |
|---|---|---|
| `language` | yes | Language of the snippet, for example `csharp`. Highlighting is applied for known languages, other values render as plain text. Only `csharp` snippets are executed. |
| `harness` | yes | `statements`, `program` or `none`. See [the sandbox section](#code-expectations-and-the-snippet-sandbox). |
| `source` | yes | The code. Comments in code are in English: code is not translated. |

### `expect`, `fixedExpect` and `options[].expect`

| Field | Required | Meaning |
|---|---|---|
| `kind` | yes | `output`, `exception`, `compile-error` or `nondeterministic`. |
| `value` | by kind | `output`: the console output, required. `exception`: the full exception type name, required, for example `System.InvalidOperationException`. `compile-error`: the compiler error code such as `CS0103`, optional (omitted means any compile error). `nondeterministic`: no value. |

### `options[]`

| Field | Required | Meaning |
|---|---|---|
| `id` | yes | Short id such as `a`, `b`. Unique inside the question. Locale blocks refer to it. |
| `correct` | yes | `true` or `false`. |
| `expect` | output | Machine-readable result of this option, so that CI can verify that exactly the correct option matches the real run. |

### `links[]`

| Field | Required | Meaning |
|---|---|---|
| `url` | yes | Absolute `https:` URL. |
| `lang` | yes | Language of the linked page: `ru` or `en`. A Russian question may link an English page; the site marks the link language. |
| `kind` | yes | `docs`, `spec`, `article`, `video` or `book`. |

### `locales.{ru,en}`

| Field | Required | Meaning |
|---|---|---|
| `title` | yes | Page title. A question, phrased so that it makes sense in search results. |
| `prompt` | yes | The task, Markdown. |
| `hint` | no | Markdown. Nudges toward the answer without giving it away. Shown folded. |
| `options` | by type | Map from option id to option text. Every option id needs a text in every locale. |
| `optionExplanations` | by type | Map from option id to "why this is wrong". Every incorrect option needs one in every locale. |
| `answer` | yes | The correct answer: a short statement, or the reference answer for `open` and `situation`. Markdown. |
| `explanation` | yes | The reasoning behind the answer. Markdown. |
| `linkTitles` | yes | One title per link, in the order of `links`. The count must match. |

A question is published in a culture only when its locale block exists and is
complete. There is no fallback to another language: a missing locale means that
the page answers 404 in that culture, the catalog omits the question there, and
`hreflang` lists only the languages that exist.

### `meta`

| Field | Required | Meaning |
|---|---|---|
| `authors` | yes | List of author handles. |
| `verified` | yes | Date (`yyyy-MM-dd`) when you last checked the content against sources. A date in the future is an error (`verified-in-future`). |
| `dotnetVersion` | no | Version the answer is true for, as text: `"10"`. |
| `noRunReason` | when `harness: none` | Why the code cannot run in the sandbox. |

## Rules per question type

| Type | Grading | `code` | `options` | Other rules |
|---|---|---|---|---|
| `open` | `self` | optional | forbidden | The reader reveals `answer` and judges themselves. |
| `situation` | `self` | optional | forbidden | A scenario; `answer` is a reference plan. |
| `single` | `auto` | optional | at least 2, exactly 1 correct | Each incorrect option needs an explanation in every locale. |
| `multi` | `auto` | optional | at least 2, at least 1 correct | Same as `single`. |
| `output` | `auto` | required, harness not `none` | optional; if present exactly 1 correct and every option has `expect` | `expect` required. With `expect.kind: output` the question needs `options` or `acceptedOutputs`. If both `acceptedOutputs` and `expect.value` are set, `expect.value` must be one of the accepted values after trimming and LF normalisation. |
| `find-bug` | `auto` | required | forbidden | `bugLineSets` lists the accepted selections; each number is a 1-based line of `code.source`. `bugLineSets` is required and must contain at least one non-empty set (`buglines-required`). `fixedSource` requires `fixedExpect`. |

The grading mode follows the type; a mismatch is `grading-type-mismatch`.

## Complete examples

Each block below is a complete question file for one type. A test
(`ContentGuide_ExamplesAreValid`) runs every block marked `yaml` through the real
reader and validator, so these examples cannot drift from the schema. Blocks
marked `yaml fragment` elsewhere in this guide are not checked this way.

### `open`

```yaml
# yaml-language-server: $schema=../../schema/question.schema.json
id: 01JD4X7Q8N9V2K3M4P5R6S7T80
slug: ienumerable-vs-iqueryable
stack: dotnet
topic: collections-linq
level: middle
type: open
grading: self
tags: [linq, iqueryable, deferred-execution]
links:
  - url: https://learn.microsoft.com/en-us/dotnet/api/system.linq.iqueryable
    lang: en
    kind: docs
  - url: https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.ienumerable-1
    lang: en
    kind: docs
locales:
  ru:
    title: В чём разница между IEnumerable<T> и IQueryable<T>?
    prompt: |
      Чем `IEnumerable<T>` отличается от `IQueryable<T>` при написании запросов LINQ и к чему приводит
      ранний вызов `AsEnumerable()` в запросе к базе данных?
    hint: Что делает оператор LINQ с переданной лямбдой - выполняет её или запоминает?
    answer: |
      - `IEnumerable<T>`: операторы принимают делегаты (`Func<T, bool>`) и выполняются в памяти приложения, элемент за элементом.
      - `IQueryable<T>`: операторы принимают деревья выражений (`Expression<Func<T, bool>>`). Провайдер,
        например EF Core, переводит их в запрос к источнику данных, и запрос выполняется при перечислении результата.
      - `AsEnumerable()` посреди запроса переключает остаток цепочки на выполнение в памяти: фильтры после него
        применяются уже к загруженным строкам.
    explanation: |
      Разница в том, что хранит оператор: делегат, который можно только вызвать, или дерево выражений, которое
      провайдер может проанализировать и перевести. Поэтому `Where` над `IQueryable<T>` превращается в условие
      `WHERE` на стороне базы, а над `IEnumerable<T>` - в цикл в памяти.

      Типичная ошибка - вызвать `AsEnumerable()` или `ToList()` до фильтрации: база отдаёт все строки, а отбор
      выполняется в приложении.
    linkTitles:
      - "Интерфейс IQueryable"
      - "Интерфейс IEnumerable<T>"
  en:
    title: What is the difference between IEnumerable<T> and IQueryable<T>?
    prompt: |
      How does `IEnumerable<T>` differ from `IQueryable<T>` when you write LINQ queries, and what happens
      if you call `AsEnumerable()` early in a database query?
    hint: What does a LINQ operator do with the lambda it receives - run it or remember it?
    answer: |
      - `IEnumerable<T>`: operators take delegates (`Func<T, bool>`) and run in the application's memory, item by item.
      - `IQueryable<T>`: operators take expression trees (`Expression<Func<T, bool>>`). A provider such as
        EF Core translates them into a query for the data source, and the query runs when the result is enumerated.
      - `AsEnumerable()` in the middle of a query switches the rest of the chain to in-memory execution: filters
        after it are applied to rows that are already loaded.
    explanation: |
      The difference is what the operator stores: a delegate that can only be invoked, or an expression tree
      that a provider can inspect and translate. That is why `Where` over `IQueryable<T>` becomes a `WHERE`
      clause in the database, while over `IEnumerable<T>` it becomes a loop in memory.

      A common mistake is calling `AsEnumerable()` or `ToList()` before filtering: the database returns every
      row and the application does the filtering.
    linkTitles:
      - "IQueryable interface"
      - "IEnumerable<T> interface"
meta:
  authors: [example-author]
  verified: 2026-11-11
  dotnetVersion: "10"
```

### `single`

```yaml
# yaml-language-server: $schema=../../schema/question.schema.json
id: 01JD4X7Q8N9V2K3M4P5R6S7T81
slug: which-type-is-reference
stack: dotnet
topic: types-memory
level: junior
type: single
grading: auto
tags: [value-types, reference-types]
options:
  - id: a
    correct: false
  - id: b
    correct: false
  - id: c
    correct: true
  - id: d
    correct: false
links:
  - url: https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/reference-types
    lang: en
    kind: docs
  - url: https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/value-types
    lang: en
    kind: docs
locales:
  ru:
    title: Какой из этих типов является ссылочным?
    prompt: |
      Какой из перечисленных типов в .NET является ссылочным?
    hint: Какие из этих типов объявлены как `struct`?
    options:
      a: "`int`"
      b: "`DateTime`"
      c: "`string`"
      d: "`Guid`"
    optionExplanations:
      a: "`int` - это псевдоним `System.Int32`, структура, то есть тип значения."
      b: "`DateTime` объявлен как `struct`, поэтому это тип значения."
      d: "`Guid` объявлен как `struct`, поэтому это тип значения."
    answer: "`string`"
    explanation: |
      `string` - это запечатанный класс `System.String`, то есть ссылочный тип. Он неизменяемый, поэтому
      сравнивается и присваивается так, будто это значение, но переменная хранит ссылку на объект в куче.
    linkTitles:
      - "Ссылочные типы"
      - "Типы значений"
  en:
    title: Which of these types is a reference type?
    prompt: |
      Which of these .NET types is a reference type?
    hint: Which of these types are declared as `struct`?
    options:
      a: "`int`"
      b: "`DateTime`"
      c: "`string`"
      d: "`Guid`"
    optionExplanations:
      a: "`int` is an alias of `System.Int32`, a struct, so it is a value type."
      b: "`DateTime` is declared as a `struct`, so it is a value type."
      d: "`Guid` is declared as a `struct`, so it is a value type."
    answer: "`string`"
    explanation: |
      `string` is the sealed class `System.String`, so it is a reference type. It is immutable, which is
      why it compares and assigns as if it were a value, but a variable holds a reference to an object on the heap.
    linkTitles:
      - "Reference types"
      - "Value types"
meta:
  authors: [example-author]
  verified: 2026-11-11
  dotnetVersion: "10"
```

### `multi`

```yaml
# yaml-language-server: $schema=../../schema/question.schema.json
id: 01JD4X7Q8N9V2K3M4P5R6S7T82
slug: linq-deferred-operators
stack: dotnet
topic: collections-linq
level: middle
type: multi
grading: auto
tags: [linq, deferred-execution]
options:
  - id: a
    correct: true
  - id: b
    correct: false
  - id: c
    correct: true
  - id: d
    correct: false
links:
  - url: https://learn.microsoft.com/en-us/dotnet/standard/linq/deferred-execution-lazy-evaluation
    lang: en
    kind: docs
locales:
  ru:
    title: Какие операторы LINQ выполняются отложенно?
    prompt: |
      Выберите все операторы LINQ to Objects, которые не выполняют запрос в момент вызова, а откладывают его
      до перечисления результата.
    hint: Какие операторы возвращают последовательность, а какие - готовое значение или коллекцию?
    options:
      a: "`Where`"
      b: "`ToList`"
      c: "`Select`"
      d: "`Count`"
    optionExplanations:
      b: "`ToList` перечисляет последовательность сразу и возвращает готовый список."
      d: "`Count` перечисляет последовательность сразу и возвращает число."
    answer: "`Where` и `Select`."
    explanation: |
      `Where` и `Select` возвращают объект-запрос и ничего не вычисляют, пока результат не начнут перечислять
      (`foreach`, `ToList`, `Count` и так далее). `ToList` и `Count` сами перечисляют источник, поэтому
      выполняются немедленно.
    linkTitles:
      - "Отложенное выполнение и ленивые вычисления в LINQ"
  en:
    title: Which LINQ operators use deferred execution?
    prompt: |
      Select all LINQ to Objects operators that do not run the query when they are called, but defer it
      until the result is enumerated.
    hint: Which operators return a sequence, and which return a finished value or collection?
    options:
      a: "`Where`"
      b: "`ToList`"
      c: "`Select`"
      d: "`Count`"
    optionExplanations:
      b: "`ToList` enumerates the sequence immediately and returns a finished list."
      d: "`Count` enumerates the sequence immediately and returns a number."
    answer: "`Where` and `Select`."
    explanation: |
      `Where` and `Select` return a query object and compute nothing until the result is enumerated
      (`foreach`, `ToList`, `Count` and so on). `ToList` and `Count` enumerate the source themselves, so
      they run immediately.
    linkTitles:
      - "Deferred execution and lazy evaluation in LINQ"
meta:
  authors: [example-author]
  verified: 2026-11-11
  dotnetVersion: "10"
```

### `output`

```yaml
# yaml-language-server: $schema=../../schema/question.schema.json
id: 01JD4X7Q8N9V2K3M4P5R6S7T83
slug: integer-division-and-remainder
stack: dotnet
topic: csharp
level: junior
type: output
grading: auto
tags: [operators, integer-division]
code:
  language: csharp
  harness: statements
  source: |
    int a = 7;
    int b = 2;
    Console.Write($"{a / b} {a % b}");
expect:
  kind: output
  value: "3 1"
acceptedOutputs: ["3 1"]
options:
  - id: a
    correct: true
    expect: { kind: output, value: "3 1" }
  - id: b
    correct: false
    expect: { kind: output, value: "3.5 1" }
  - id: c
    correct: false
    expect: { kind: output, value: "4 1" }
  - id: d
    correct: false
    expect: { kind: exception, value: System.DivideByZeroException }
  - id: e
    correct: false
    expect: { kind: compile-error }
links:
  - url: https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/operators/arithmetic-operators
    lang: en
    kind: docs
locales:
  ru:
    title: Что выведет деление целых чисел с остатком?
    prompt: |
      Что выведет эта программа в консоль?
    hint: Каков тип результата деления двух `int`?
    options:
      a: "3 1"
      b: "3.5 1"
      c: "4 1"
      d: Исключение `DivideByZeroException`
      e: Ошибка компиляции
    optionExplanations:
      b: Деление двух `int` даёт `int`, дробная часть отбрасывается, поэтому `3.5` получить нельзя.
      c: Деление целых чисел отбрасывает дробную часть, а не округляет к ближайшему.
      d: Делитель равен 2, а не нулю, поэтому исключения не будет.
      e: Код корректен. Интерполяция строк принимает любые выражения.
    answer: "3 1"
    explanation: |
      Оба операнда имеют тип `int`, поэтому `7 / 2` - целочисленное деление с отбрасыванием дробной части и
      результатом `3`. Оператор `%` возвращает остаток от деления, то есть `1`.
    linkTitles:
      - "Арифметические операторы"
  en:
    title: What does integer division with a remainder print?
    prompt: |
      What does this program print to the console?
    hint: What is the type of the result of dividing two `int` values?
    options:
      a: "3 1"
      b: "3.5 1"
      c: "4 1"
      d: "`DivideByZeroException`"
      e: Compile error
    optionExplanations:
      b: Dividing two `int` values gives an `int` and drops the fraction, so `3.5` is impossible.
      c: Integer division truncates the fraction, it does not round to the nearest value.
      d: The divisor is 2, not zero, so no exception is thrown.
      e: The code is valid. String interpolation accepts any expression.
    answer: "3 1"
    explanation: |
      Both operands are `int`, so `7 / 2` is integer division: the fraction is dropped and the result is `3`.
      The `%` operator returns the remainder, which is `1`.
    linkTitles:
      - "Arithmetic operators"
meta:
  authors: [example-author]
  verified: 2026-11-11
  dotnetVersion: "10"
```

### `find-bug`

```yaml
# yaml-language-server: $schema=../../schema/question.schema.json
id: 01JD4X7Q8N9V2K3M4P5R6S7T84
slug: list-remove-inside-foreach
stack: dotnet
topic: collections-linq
level: middle
type: find-bug
grading: auto
tags: [list, foreach, invalid-operation]
code:
  language: csharp
  harness: statements
  source: |
    List<int> numbers = new() { 1, 2, 3, 4 };
    foreach (int number in numbers)
    {
        if (number % 2 == 0)
        {
            numbers.Remove(number);
        }
    }
    Console.WriteLine(numbers.Count);
bugLineSets: [[6]]
fixedSource: |
  List<int> numbers = new() { 1, 2, 3, 4 };
  numbers.RemoveAll(number => number % 2 == 0);
  Console.WriteLine(numbers.Count);
fixedExpect: { kind: output, value: "2" }
links:
  - url: https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.list-1.removeall
    lang: en
    kind: docs
locales:
  ru:
    title: Найдите строку, которая ломает перебор списка
    prompt: |
      Программа должна оставить в списке только нечётные числа и вывести `2`, но завершается с исключением.
      Щёлкните по строке с дефектом.
    hint: Что происходит с перечислителем списка, если список изменили во время `foreach`?
    answer: Строка 6, вызов `numbers.Remove(number)` внутри `foreach`.
    explanation: |
      Перечислитель `List<T>` запоминает версию списка. После `Remove` версия меняется, и следующий вызов
      `MoveNext` выбрасывает `InvalidOperationException`: коллекция изменена во время перебора.

      Исправление - не менять список во время перебора. `RemoveAll` удаляет все подходящие элементы за один
      вызов и не использует перечислитель.
    linkTitles:
      - "Метод List<T>.RemoveAll"
  en:
    title: Find the line that breaks the iteration over a list
    prompt: |
      The program should keep only the odd numbers in the list and print `2`, but it ends with an exception.
      Click the line that contains the defect.
    hint: What happens to a list enumerator when the list is modified during `foreach`?
    answer: Line 6, the call `numbers.Remove(number)` inside `foreach`.
    explanation: |
      The `List<T>` enumerator remembers the version of the list. After `Remove` the version changes, and the
      next `MoveNext` call throws `InvalidOperationException`: the collection was modified during enumeration.

      The fix is not to modify the list while iterating it. `RemoveAll` removes every matching element in one
      call and does not use the enumerator.
    linkTitles:
      - "List<T>.RemoveAll method"
meta:
  authors: [example-author]
  verified: 2026-11-11
  dotnetVersion: "10"
```

### `situation`

```yaml
# yaml-language-server: $schema=../../schema/question.schema.json
id: 01JD4X7Q8N9V2K3M4P5R6S7T85
slug: orders-list-n-plus-one
stack: dotnet
topic: ef-core
level: senior
type: situation
grading: self
tags: [ef-core, n-plus-one, performance]
links:
  - url: https://learn.microsoft.com/en-us/ef/core/performance/efficient-querying
    lang: en
    kind: docs
  - url: https://learn.microsoft.com/en-us/ef/core/querying/single-split-queries
    lang: en
    kind: docs
locales:
  ru:
    title: Страница со списком из 200 заказов медленная, в логе SQL 201 запрос
    prompt: |
      После релиза страница списка заказов открывается за несколько секунд. В логе SQL на один запрос
      страницы приходится 201 запрос: один за заказами и по одному за позициями каждого заказа.
      Что вы будете делать и в каком порядке?
    hint: Откуда берутся «по одному запросу на каждый заказ» и как получить те же данные меньшим числом запросов?
    answer: |
      1. **Подтвердить причину.** Включить логирование SQL и убедиться, что лишние запросы идут из цикла по заказам
         при обращении к навигационному свойству (ленивая загрузка или явная загрузка в цикле).
      2. **Исправить запрос.** Предпочтительно проекция в DTO через `Select` с нужными полями. Если нужны сущности -
         `Include`. При нескольких коллекциях добавить `AsSplitQuery()`, чтобы избежать декартова произведения.
      3. **Не отслеживать лишнее.** Для страницы только на чтение добавить `AsNoTracking()`.
      4. **Закрепить.** Тест, который считает число SQL-запросов страницы, и оценка плана запроса на объёме
         данных, близком к боевому.
    explanation: |
      Это классическая проблема N+1: один запрос за списком и по запросу на каждый элемент. Каждый запрос
      дёшев, но задержка сети до базы умножается на число заказов. Лечится загрузкой связанных данных заранее.

      Проекция предпочтительнее `Include`: она читает только нужные столбцы и не создаёт отслеживаемых сущностей.
      У `Include` для нескольких коллекций есть своя цена - растущий декартов результат, поэтому нужен
      `AsSplitQuery()`, но он делает несколько запросов, что надо измерять.
    linkTitles:
      - "Эффективные запросы в EF Core"
      - "Единый и разделённый запросы"
  en:
    title: A list page of 200 orders is slow and the SQL log shows 201 queries
    prompt: |
      After a release the order list page takes several seconds to open. The SQL log shows 201 queries
      for one page request: one for the orders and one per order for its items.
      What do you do, and in what order?
    hint: Where do the "one query per order" come from, and how do you get the same data with fewer queries?
    answer: |
      1. **Confirm the cause.** Turn on SQL logging and check that the extra queries come from a loop over the
         orders that touches a navigation property (lazy loading, or explicit loading inside the loop).
      2. **Fix the query.** Prefer a projection into a DTO with `Select` and only the needed fields. If you need
         entities, use `Include`. With several collections add `AsSplitQuery()` to avoid a cartesian product.
      3. **Stop tracking what you do not edit.** For a read-only page add `AsNoTracking()`.
      4. **Lock it in.** A test that counts the SQL queries of the page, and a look at the query plan on data
         close to production size.
    explanation: |
      This is the classic N+1 problem: one query for the list and one query per element. Each query is cheap,
      but the network latency to the database is multiplied by the number of orders. The cure is to load the
      related data up front.

      A projection beats `Include`: it reads only the needed columns and creates no tracked entities.
      `Include` over several collections has its own cost, a growing cartesian result, which is why
      `AsSplitQuery()` exists, but it issues several queries, so measure it.
    linkTitles:
      - "Efficient querying in EF Core"
      - "Single vs. split queries"
meta:
  authors: [example-author]
  verified: 2026-11-11
  dotnetVersion: "10"
```

## Code, expectations and the snippet sandbox

CI compiles and runs every runnable snippet and compares the real result with
what the question declares. Snippets come from pull requests and are untrusted,
so they never run in the web app. They run only in disposable Docker containers.

### Harness

| `harness` | Meaning |
|---|---|
| `statements` | The snippet is top-level statements, as in a `Program.cs` with no `Main`. |
| `program` | The snippet is a complete program that declares its own entry point and types. |
| `none` | Illustrative code that is not executed. Not allowed for `output`. Requires `meta.noRunReason`. |

Both `statements` and `program` are compiled as `Program.cs` in a `net10.0`
console project with implicit usings enabled, nullable annotations disabled and
invariant globalization. There is no network, so no NuGet packages: only the
shared framework is available.

Only snippets with `language: csharp` and a harness other than `none` are
executed, and only for `output` and `find-bug` questions.

### Sandbox limits

| Stage | Limits |
|---|---|
| Build container | No network, 2 GB memory, build deadline 3 minutes. On Linux hosts it runs as the host user, not root; on Docker Desktop no `--user` is set. |
| Run container | No network, read-only root file system and output directory, 256 MB memory, at most 128 processes, all capabilities dropped, `no-new-privileges`, logging disabled, `TZ=UTC`, 10 second deadline enforced from the host with `docker kill`, output capped at 64 KiB. |

The image is pinned by digest (`SandboxOptions.Image`), so a moving tag cannot
change what content is verified against.

### How results are compared

- `output`: stdout after converting CRLF to LF and trimming. `expect.value` must
  match exactly. Keep output short and free of timing, thread ids and
  addresses.
- `exception`: only an uncaught exception counts. The value is the full type
  name from the "Unhandled exception." line, for example
  `System.InvalidOperationException`. A snippet that catches the exception and
  prints it is an `output` snippet.
- `compile-error`: the build fails with a compiler error. The value, if given,
  is the code such as `CS0103`.
- `nondeterministic`: the snippet is run 5 times and must produce at least two
  different results.
- A non-zero exit code without an unhandled exception (an out-of-memory kill,
  `Environment.Exit`) is reported as a sandbox failure, never as a content
  defect.

For `output` questions with options, exactly the correct option must match the
real result and every incorrect option must not. For `find-bug` questions with
`fixedSource`, the corrected code is run and compared with `fixedExpect`.

## Markdown in text fields

`prompt`, `hint`, `answer`, `explanation`, option texts and option explanations
are Markdown, rendered once on the server when the content is imported.

- Supported: paragraphs, emphasis, lists, inline code, pipe tables, and the
  extra emphasis and list styles of the Markdig pipeline in
  `MarkdownPipelineFactory`.
- Raw HTML is disabled. A tag in the text is shown as text, not rendered.
- Links and images must be absolute `https:` URLs or site-relative paths that
  start with a single `/`. Any other target (`http:`, `javascript:`, `data:`,
  `mailto:`, `//host/path`) is rejected by the validator
  (`markdown-link-scheme`), and the renderer would replace it with its text.
- External links from content (Markdown body and the `links` list) get `rel="nofollow noopener noreferrer"`.
- Pipe tables are wrapped in a `div.table-scroll` so wide tables scroll sideways while keeping table semantics.
- Put sources in `links`, not in the body text, so they appear in the references
  list and the page keeps a consistent structure.
- Use backticks for identifiers, types and short code. Put longer code in
  `code.source`, not in the Markdown.

## Quality bar

A question is ready for review when all of the following are true.

1. **It has a source.** At least one entry in `links` (the validator
   enforces the count; review checks the content). Prefer, in this order: Microsoft Learn, the C#
   language specification, the `dotnet/runtime` and other official
   repositories, RFCs, then recognised authors. Open every link and check that
   the page supports the claim. Do not invent links or APIs.
2. **It is true for a stated version.** Set `meta.dotnetVersion` when the
   behaviour depends on the runtime or language version, and say so in the
   explanation.
3. **It is verified.** `meta.verified` is the date you checked the content
   against the sources. Update it when you re-check a question.
4. **It runs.** For `output` and `find-bug`, `run-snippets` is green on your
   machine. The expectations are the result of a real run, not a guess.
5. **It exists in both languages.** `ru` and `en`, both complete and equivalent
   in meaning. Code and identifiers stay in English in both; comments inside
   code are English.
6. **It teaches the reason.** `explanation` says why, not only what. Every
   incorrect option says why it is wrong.
7. **It has one defensible answer.** If experts would disagree, rewrite the
   question or make it `open`.
8. **The hint helps without answering.** A hint is a nudge, not the answer.
9. **It is original.** Your own wording, and a topic and level that match the
   directory and `level`.

## Ids, slugs, renames, redirects and retiring

- `id` and `slug` never change after the question is merged. Progress and
  links refer to them.
- The importer enforces this. If a question with a known `id` appears with a
  different slug and no redirect, the import is rejected with `slug-changed`.
  If a new question takes a slug that still belongs to another question
  (including a retired or removed one), the import is rejected with
  `slug-reused`.
- **To rename a slug:** move the file to its new path, change `slug`, keep the
  `id`, and add an entry to `{stack}/redirects.yaml` (see below). The old URL
  then answers with a 301 redirect.
- **To move a question to another topic:** move the file, change `topic`, and
  keep both `id` and `slug`. Question URLs contain no topic, so the URL does not
  change and no redirect is needed (a redirect with the same slug is rejected
  with `redirect-self`).
- **To retire a question:** delete its file. The importer marks the question
  retired instead of deleting it. Its page stays readable with `noindex`, and it
  disappears from the catalog and the sitemap. To send readers somewhere else,
  add a redirect from its path to a live question.

## Stacks, topics and redirects files

`content/stacks.yaml` lists stacks. Both locales need a non-empty title and
description (`taxonomy-locale-incomplete`).

```yaml fragment
- key: dotnet
  locales:
    ru:
      title: .NET
      description: Вопросы для подготовки к собеседованиям по C# и платформе .NET.
    en:
      title: .NET
      description: Interview preparation questions on C# and the .NET platform.
```

`content/{stack}/topics.yaml` lists the topics of the stack in the same shape.
A topic key is lowercase kebab-case, and the directory with that name holds its
questions. Adding a topic means adding both locales.

`content/{stack}/redirects.yaml` is a list of `topic/slug` paths. URLs carry
no topic, so only the slug is compared and the topic part is informational. In
one stack, `from` must not be the slug of a live question, `to` must be one, a
slug can be a source only once, and a chain must not loop.

```yaml fragment
- from: async/result-blocks-ui
  to: async/result-deadlock-ui-thread
```

An empty file is written `[]`.

## Verify locally

All commands run the content CLI. The first argument selects the command. The
optional root argument defaults to `content`.

```bash
dotnet run --project tools/Trainer.ContentCli -- validate
dotnet run --project tools/Trainer.ContentCli -- run-snippets
dotnet run --project tools/Trainer.ContentCli -- check-links [--content <dir>]
dotnet run --project tools/Trainer.ContentCli -- new-question <stack> <topic> <slug> <type> [root]
dotnet run --project tools/Trainer.ContentCli -- generate-schemas [root]
dotnet run --project tools/Trainer.ContentCli -- dump [root]
```

- `validate` reads every file, runs all semantic checks and prints one line per
  problem as `path: code: message [pointer]`. On success it prints the question
  count and the content hash. It needs no database and no Docker.
- `run-snippets` needs Docker. The first run pulls the pinned sandbox image.
- `check-links` requests every absolute `https` URL from `links` and from the Markdown
  text (both locales) and prints one line per URL that is not `ok`, with the question
  files that cite it. `dead` (404, 410, DNS or TLS failure, redirect loop) gives exit
  code `1`; `unknown` (401, 403, 429, timeout, server error) is listed but gives `0`.
  A redirect from `https` to plain `http` also counts as dead.
  It needs network access, so run it on demand; CI runs it weekly. Its root is the
  `--content` option, not a positional argument.
- `generate-schemas` rewrites `content/schema/*.schema.json` from the C# model.
  Run it only when the format changes; a test fails if the committed schemas are
  out of date.
- `dump` prints the parsed content as one JSON object (`hash`, `questions` with
  path, SHA-256 of the file bytes and the parsed document, `topics` and
  `redirects` per stack, `readIssues`) for
  tooling that must not parse YAML itself; read issues are data and exit `0`.

| Exit code | Meaning |
|---|---|
| `0` | Success. |
| `1` | Content problems were found, or `check-links` found a dead link. |
| `2` | Wrong usage: unknown command, wrong arguments, missing content root, or `new-question` would overwrite a file. |
| `3` | The sandbox failed (Docker missing or broken). The content was not judged. |
| `130` | Cancelled with Ctrl+C. |

Pull requests run three CI jobs: `build-test` (format check, build with warnings
as errors, unit and integration tests), `content` (`validate`, then the Docker
tests, then `run-snippets`) and `e2e` (browser checks). The `content` job has no
secrets and read-only permissions. Exit code `3` there is an infrastructure
failure: re-run the job. A separate weekly workflow (`link-check.yml`) runs
`check-links` and fails when a source link has died.

## Editor setup

Every question file starts with a comment that points the YAML language server
at the generated schema:

```text
# yaml-language-server: $schema=../../schema/question.schema.json
```

The path is relative to the file, so it is correct for `content/{stack}/{topic}/{slug}.yaml`.
With the Red Hat YAML extension in VS Code (or any editor that supports the YAML
language server) you get completion, hover descriptions and warnings for unknown
keys and wrong values. For the other files use
`# yaml-language-server: $schema=schema/stacks.schema.json` in `content/stacks.yaml`,
`../schema/topics.schema.json` in `content/{stack}/topics.yaml` and
`../schema/redirects.schema.json` in `content/{stack}/redirects.yaml`.

The schema covers structure and the closed vocabularies only. The semantic rules
(uniqueness, option counts, path matching, snippet results) need `validate`.

## Issue codes

Every code is stable and appears in CLI output, CI annotations and import logs.
A test checks that this section lists exactly the codes that exist in the source.

### Reading files

| Code | Raised when | How to fix |
|---|---|---|
| `yaml-parse` | The file is not valid YAML. | Fix the syntax at the reported line and column. |
| `yaml-extension` | A content file ends in `.yml`, which the reader does not load. | Rename it to `.yaml`. |
| `yaml-multiple-documents` | The file contains more than one YAML document. | Remove the extra `---` documents. |
| `yaml-alias-forbidden` | An anchor, alias or merge key is used. | Write the value out in full. |
| `content-structure` | The structure is wrong: unknown or missing key, null or wrong type, a tag, invalid UTF-8, an empty document. | Match the field reference. Quote numeric-looking text. |
| `content-file-missing` | `schema-version`, `stacks.yaml` or a stack `topics.yaml` is missing. | Restore the file. |
| `schema-version-invalid` | `schema-version` is not a single non-negative integer. | Write the integer, for example `1`. |

### Validation

| Code | Raised when | How to fix |
|---|---|---|
| `schema-version-unsupported` | `schema-version` is outside the range this app version supports (1 to 1). | Use a supported version. |
| `id-invalid` | `id` is not a valid ULID. | Generate it with `new-question`. |
| `id-duplicate` | Another file uses the same `id`. | Give the new question its own ULID. |
| `slug-invalid` | `slug` is not lowercase kebab-case of at most 80 characters. | Rewrite the slug. |
| `slug-duplicate` | The slug is already used in the stack. | Choose another slug. |
| `path-mismatch` | The file is not at `{stack}/{topic}/{slug}.yaml`. | Move the file or fix the fields. |
| `stack-unknown` | The stack is not in `stacks.yaml`, or topics or redirects exist for an unknown stack. | Add the stack or fix the key. |
| `topic-unknown` | The topic is not in the stack `topics.yaml`. | Add the topic or fix the key. |
| `taxonomy-duplicate` | A stack or topic key is declared twice. | Remove the duplicate. |
| `taxonomy-locale-incomplete` | A stack or topic lacks a non-empty title and description in `ru` or `en`. | Add both locales. |
| `locale-unsupported` | A locale key is not `ru` or `en`. | Use `ru` and `en`. |
| `level-invalid` | `level` is not one of the five levels. | Use `junior`, `middle`, `senior`, `lead` or `architect`. |
| `type-invalid` | `type` is not one of the six types. | Use an exact lowercase type key. |
| `grading-invalid` | `grading` is not `self` or `auto`. | Use `self` or `auto`. |
| `grading-type-mismatch` | `grading` does not follow the type. | `self` for `open` and `situation`, `auto` otherwise. |
| `options-required` | `single` or `multi` has fewer than two options. | Add options. |
| `options-forbidden` | `open`, `situation` or `find-bug` has options. | Remove `options`. |
| `options-correct-count` | The number of correct options is wrong for the type. | Exactly one for `single` and for `output` with options; at least one for `multi`. |
| `option-id-duplicate` | An option id is empty or used twice. | Make ids unique and non-empty. |
| `output-expect-required` | An `output` question has no `expect`, or an option has no `expect`. | Add the `expect`. |
| `output-answer-mode` | An `output` question with `expect.kind: output` has neither `options` nor `acceptedOutputs`. | Add one of them. |
| `accepted-outputs-mismatch` | `expect.value` is not among `acceptedOutputs` after trimming and LF normalisation. | Add the value to `acceptedOutputs`. |
| `expect-kind-invalid` | An `expect.kind` is not one of the four kinds. | Use `output`, `exception`, `compile-error` or `nondeterministic`. |
| `expect-value-required` | Kind `output` or `exception` has no `value`. | Add the value. |
| `code-required` | An `output` or `find-bug` question has no `code`. | Add `code`. |
| `harness-invalid` | `code.harness` is not `statements`, `program` or `none`. | Use one of them. |
| `harness-none-forbidden` | An `output` question uses harness `none`. | Make the snippet runnable. |
| `harness-none-reason` | Harness `none` without `meta.noRunReason`. | Explain why the code cannot run. |
| `buglines-required` | A `find-bug` question has no `bugLineSets`, or one of the sets is empty. | Add the accepted line sets. |
| `buglines-out-of-range` | A `bugLineSets` number is outside `1..lines of code.source`. | Fix the line numbers. |
| `fixed-expect-required` | `fixedSource` without `fixedExpect`. | Add `fixedExpect`. |
| `locale-incomplete` | No locale block, a missing option text or explanation, or `linkTitles` does not match the number of links. | Complete the locale. |
| `links-required` | A question has no links. | Add at least one source link. |
| `link-kind-invalid` | A link `kind` is not `docs`, `spec`, `article`, `video` or `book`. | Use an allowed kind. |
| `link-lang-invalid` | A link `lang` is not `ru` or `en`. | Use `ru` or `en`. |
| `link-scheme` | A link `url` is not an absolute `https:` URL. | Use an `https:` URL. |
| `markdown-link-scheme` | A Markdown link or image target is not allowed. | Use `https:` or a site-relative path. |
| `tag-invalid` | A tag is not lowercase kebab-case. | Rewrite the tag. |
| `redirect-target-unknown` | A redirect `to` is not a live question. | Point it at an existing question. |
| `redirect-cycle` | Following redirects (compared by slug) leads back to the start. | Break the loop. |
| `redirect-self` | A redirect `from` and `to` have the same slug, for example after a topic move. | Remove the entry; moving a topic needs no redirect. |
| `redirect-duplicate` | The same slug is a redirect `from` more than once in a stack. | Keep one entry per source slug. |
| `redirect-from-live` | A redirect `from` is a live question. | Remove the entry or retire the question first. |
| `verified-in-future` | `meta.verified` is later than today (UTC). | Use the date you checked the content. |

### Snippets (`run-snippets`)

| Code | Raised when | How to fix |
|---|---|---|
| `snippet-mismatch` | The real result differs from `expect`. | Correct `expect` or the code. |
| `snippet-option-mismatch` | An option's `expect` disagrees with its `correct` flag. | Exactly the correct option must match the real result. |
| `snippet-accepted-mismatch` | The real result is not in `acceptedOutputs`. | Fix `acceptedOutputs`. |
| `snippet-fixed-mismatch` | The corrected code does not produce `fixedExpect`. | Fix `fixedSource` or `fixedExpect`. |
| `snippet-not-nondeterministic` | A `nondeterministic` snippet gave the same result in all runs. | Make it truly nondeterministic or declare a fixed result. |
| `snippet-timeout` | The snippet did not finish within 10 seconds. | Shorten the work. |
| `snippet-sandbox-error` | Docker or the sandbox failed, or the run ended abnormally. Not a content defect: the CLI exits with code 3. | Check Docker and re-run. |

### Import

The importer first reports every validation code above, with the file path.
It adds these:

| Code | Raised when | How to fix |
|---|---|---|
| `slug-changed` | A question kept its `id` but changed its slug without a redirect. | Add a redirect or restore the slug. |
| `slug-reused` | A slug still belongs to a different question, retired or not. | Choose another slug. |
| `db-unique-violation` | The database rejected the content with a unique constraint violation. A safety net that should not normally fire. | Report it with the import log. |

## Pull request checklist

- [ ] `validate` prints "Content is valid".
- [ ] For `output` and `find-bug`: `run-snippets` is green.
- [ ] Both `ru` and `en` are complete.
- [ ] At least one source link, each opened and checked.
- [ ] `meta.verified` is today's date of checking; `meta.dotnetVersion` is set if relevant.
- [ ] `id` is generated, `slug` follows the rules, no existing `id` or `slug` was changed (or a redirect was added).
- [ ] The text is yours and licensed under CC BY-SA 4.0.
