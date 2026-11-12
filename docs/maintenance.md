# Maintenance runbook

## Before you change anything

1. `dotnet build && dotnet test` must be green. Read `AGENTS.md` (project rules) and [testing.md](testing.md).
2. If the change touches `InlineMarkdown.cs`, you will need the deep fuzz at the end (see below). Budget for it.
3. If the change is about *what Medium returns*, reproduce with a live run first and save a fixture.

## Definition of done for a change

- Tests added for normal behaviour, edge cases, invalid data, failure paths and adversarial input.
- `dotnet test` green; for inline-engine changes `RIPIUM_FUZZ_CASES=300000 dotnet test --filter "RandomMarkups|RandomDocuments"`
  green, and the markdown-it oracle ([testing.md](testing.md#the-second-parser-markdown-it)) shows only the documented class.
- A live run still works (commands in the README), including `-o -` and a directory output.
- Docs updated: [rendering.md](rendering.md) for output rules, [cli.md](cli.md) and the README for options,
  [medium-api.md](medium-api.md) for protocol facts, [decisions.md](decisions.md) if you changed a decision.
- For non-trivial work, an independent read-only review of the *artefacts* (not your conclusions). Fix material
  findings and say what remains.

## Runbooks

### Fetching suddenly fails

1. Reproduce: `dotnet run --project src/Ripium -- 27832c8f6644 -o -`.
2. Read the error: see the table in [medium-api.md](medium-api.md#troubleshooting).
3. HTTP 403 or HTML with status 200: Medium/Cloudflare is blocking the client. Try `--cookies`. If that is not
   enough, the next steps are a proxy or `curl-impersonate`; both are *new features*, not tweaks (they change how
   `MediumClient` sends requests).
4. `unexpected shape`: a field changed type. The message names the JSON path; adjust `Models.cs` and add a robustness test.
5. Headers/query changed upstream: compare with `freedium-src/.../api.py` (it is kept current by its maintainers) and
   see "Regenerating the query" below.

### Regenerating the GraphQL query

See [medium-api.md](medium-api.md#the-query-document). After replacing the constant, run the five fixture tests (they
do not call the network, so also do a live run) and look for new fields you may want in the DTOs.

### A new paragraph type appears

Symptom: a `warning: Unsupported paragraph type 'X'` line; the text is rendered as a plain paragraph so nothing is lost.

1. Capture a fixture containing it.
2. Add a `case` in `MarkdownRenderer.RenderSingle` (and grouping in `RenderBody` if consecutive items form one block).
3. Add DTO fields in `Models.cs` if it needs new data (all members must be nullable).
4. Tests: exact rendering, hostile input (links, ids), empty input; update the table in [rendering.md](rendering.md).

### A new markup type appears

Add it in `InlineMarkdown.BuildSpans` (a new `Kind`, a `Style`, candidate rules), decide its priority against links and
code, and extend `Emit` ordering only if it has a different nesting requirement. Then the deep fuzz, with the random
generator extended (`InlineRoundTripTests.RandomCase`) to produce the new type.

### Output looks wrong in some viewer

1. Reduce to one paragraph: text plus markups (print them from the fixture JSON).
2. Render with `InlineMarkdown.Render` in a scratch program; compare against what the viewer shows.
3. Decide whether it is (a) a bug in our output (fix and add to the round-trip suite), (b) a viewer-specific syntax
   (decide per [decisions.md](decisions.md#escaping-policy): escape only if the viewer is mainstream and the escape is
   harmless elsewhere), or (c) a known limitation ([rendering.md](rendering.md#known-limitations-accepted-with-reasons-in-decisionsmd)).

### Changing escaping rules

Escaping is the most fragile part. Rules of thumb that cost hours to learn:

- Judge line-start constructs against the **line in the whole text**, not the segment being escaped; segments are cut at
  markup boundaries, which can fall inside `IV)` or between `X` and `)`.
- Decide at the character that *completes* the construct (the `.` or `)`), not at its start.
- Anything inside a code span is verbatim; escapes there would print backslashes. Newlines inside code become spaces.
- Never add an escape without a test in `InlineMarkdownTests` *and* a case the fuzz alphabet can reach.
- Escaping is cheap for the viewer but visible in the raw `.md`; avoid escaping common prose (`C++`, `std::vector`).

### Adding a CLI option, changing exit codes, changing image handling

[cli.md](cli.md#adding-or-changing-an-option).

### Publishing

```sh
dotnet publish src/Ripium -c Release -o out            # framework-dependent: out/ripium.dll, run with: dotnet out/ripium.dll
dotnet publish src/Ripium -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o out
```

Nothing in the project configures packaging beyond `AssemblyName=ripium`. There is no CI configuration in the repo.

## Pitfalls that already cost time

| Pitfall | What to do |
|---|---|
| The editing tools decode lowercase backslash-u escapes (e.g. a U+2028 escape) into literal characters when writing files. In C# source U+2028 and U+0085 are line terminators, so a string literal breaks. | Write special characters as eight-digit capital-U escapes: `\U00002028`. Check with `grep -nP "[\x{2028}\x{2029}\x{FEFF}]" -r src tests`. |
| `Ripium.Markdown` (our namespace) shadows `Markdig.Markdown` in the tests. | Write `Markdig.Markdown.ToPlainText(...)`. |
| A new `*` run next to another (closers followed by reopeners) looks fine but parses wrong. | Run the fuzz; do not trust eyeballing. See `Emit`'s `starsMeetStars` rule. |
| "Fixing" the unterminated `User-Agent` or the duplicate `Accept` value. | Don't. They are Freedium's and are what works. |
| Treating `HttpRequestMessage` headers as single-valued in tests. | `User-Agent` and `Accept` come back as several values; join them. |
| `Post.Paragraphs` and tags may contain nulls before `FromJson` cleans them. | Construct `Post` through `FromJson` in tests of that behaviour. |
| Stale `Ripium.dll` next to `ripium.dll` in `bin/` after the assembly rename. | Harmless; `dotnet clean` removes it. Process tests use `ripium.dll`. |
| Chaining offsets: using `int` subtraction on untrusted offsets overflows. | `InlineMarkdown` subtracts as `long` and clamps. |
| Counting tests via `--list-tests` (theory rows are grouped inconsistently). | Use a TRX file. |
| A "followed by a space" test written with `' '` and `'\t'` only. | Use `IsBlankOrBreak`; Markdig accepts Unicode spaces after a list marker, and CommonMark's flanking rules use Unicode whitespace. |
| Checking a URL scheme on the raw string. | The parser decodes backslashes and entities later. Use `FormatDestination` first, then `IsSafeUrl`. |
| Regex anchors `$` for validating ids. | Use `\z`; `$` also matches before a trailing newline. |
| Replacing a hard break with a space *after* rendering. | Flatten the text before rendering so blanks stay outside delimiters. |
| `char.IsWhiteSpace` as "is a space" for Markdown. | It includes NEL, U+2028/2029 and VT, which parsers do not treat as whitespace. Use `IsBlank` / `IsCommonMarkWhitespace`. |
| Relying on `HttpClient.Timeout` for a streamed download. | It covers only the headers. Use a linked `CancellationTokenSource` with `CancelAfter`, as `ImageDownloader` does. |
| Appending to a user-chosen file name to build a temporary name. | Names near the 255-byte limit overflow. Use a short fixed name in the same directory. |
| Catching only `JsonException` around JSON handling. | `System.Text.Json` also throws `InvalidOperationException` (wrong node type, depth), `ArgumentException` (duplicate keys) and `InvalidCastException`. |
| A new input validation stricter than the data. | It silently dropped real `@2x` retina images once. After tightening any rule, run it over live articles and the fixtures. |
| Validating only with Markdig. | It follows CommonMark 0.31; also run `tools/markdown-it-oracle.py` (0.30 rules). |
| Long Markdown lines in docs containing backticks or pipes inside tables. | Escape pipes in table cells as `\|`. |

## Backlog / ideas, in rough priority

1. **Run the markdown-it oracle in CI** (and perhaps Pandoc) next to Markdig; today it is a manual step.
2. **Embed expansion**: fetch `https://medium.com/media/<id>` and emit the underlying gist/tweet/video URL instead of
   the Medium wrapper link.
3. **Re-base heading levels** so the shallowest body heading becomes `##` (today H3 sections skip level 2).
4. **Cookie-file option** (`--cookies-file`) so secrets stay off the command line.
5. **TLS impersonation fallback** if Cloudflare blocks plain `HttpClient`.
6. **Batch mode** (several URLs or a file of URLs), if wanted.
7. Normalise or optionally ASCII-fold smart quotes (`--ascii-quotes`) to match Freedium.
8. `shortcut:` items currently in the code: `Emit` is quadratic in markups per paragraph (see the comment on it). If you add
   one, tag it `shortcut: <limit>, <when to upgrade>`.
9. Treat NOT_FOUND-style GraphQL `errors` as final instead of retrying (only if Medium is seen to return them).
10. A sweep-line `Emit` if paragraphs with thousands of markups ever appear.

## Where the bodies are buried (non-obvious design points)

- `Emit`'s blank-interval skip means two same-style runs separated only by blanks are *one* run; `MergeSameStyle`
  mirrors that so the flank check sees what the emitter will write. If you change one, change the other.
- `DelimitersCanFlank` only needs to know about code and *inner* links because the emitter already puts a link outside
  emphasis when a letter precedes both (`afterLetter` ordering rule in `Transition`).
- `ImageUrls` includes the frontmatter preview image so it is downloaded and `RewriteLinks` can localise it.
- `MarkdownRenderer.IsTruncated` is the single place that defines "Medium truncated this" (only U+2026).
- `PostUnavailableException` vs `MediumException("Medium returned an error")`: the former is a *final answer*, the latter a
  failed request. Do not collapse them; retry behaviour depends on it.
