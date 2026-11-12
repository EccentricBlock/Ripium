# Testing

```sh
dotnet test                                               # everything, offline, about 6 s
dotnet test --filter "FullyQualifiedName~InlineRoundTrip" # one class
RIPIUM_FUZZ_CASES=300000 dotnet test --filter RandomMarkups   # deep fuzz (8 inline + 3 document seeds x N cases, 2 parsers each; about a minute for 200000)
```

No test touches the network. Failure is simulated with fake `HttpMessageHandler`s or, for the real executable, a dead
proxy (`https_proxy=http://127.0.0.1:9`).

## Inventory (655 tests)

| File | Tests | What it protects |
|---|---|---|
| `InlineMarkdownTests` | 103 | exact output of the inline engine for hand-picked cases: formatting, nesting, escaping, line-start constructs, URL safety, offsets, helpers |
| `InlineRoundTripTests` | 38 | **the contract**: output parsed with Markdig must show the original text. Named regressions, a seeded inline fuzz (8 seeds, 6 alphabets incl. Unicode blanks/marks/emoji), a document-level fuzz (random lists, quotes, headings, captions), every real paragraph of five articles, an opt-in export for the external oracle |
| `MarkdownRendererTests` | 45 | block rules, title/subtitle/tag de-duplication, frontmatter, byline, fixtures (exact starts, link cards, embeds, rows, code, emoji) |
| `MarkdownRendererRobustnessTests` | 42 | hostile ids/links/titles, heading closers, emphasised lines, block-lookalike paragraphs parsed with Markdig, list/quote/code structure |
| `FrontmatterYamlTests` + `FrontmatterQuoteTests` | 40 | frontmatter parsed with YamlDotNet; every tricky scalar round-trips; exact escapes |
| `FourthReviewTests` | 43 | regressions for the fourth review: duplicate JSON keys at every level (also through the client), byte-bounded reference expansion, response size cap, markup cap, one-line terminal messages, frontmatter control characters, symbol-aware title matching, cookie validation, assets folder names |
| `ThirdReviewTests` | 61 | regressions for the third review: CommonMark blank set (NEL, U+2028/2029, VT), long entity names, long file names, hostile response shapes and a node bound for reference graphs, whole-body image timeout, frontmatter url validation, control characters everywhere, no-letter title matches, CLI argument checks, README and CLI usage kept verbatim |
| `ReviewFindingsTests` | 88 | regressions for the second independent review: hostile link targets in every position (parsed with two pipelines), Unicode blanks, multi-line emphasis, flanking under both CommonMark editions, hostile responses (20,000-entity ref chain, charset), race-free writes, asset collisions, size/type guards, title matching, performance guard |
| `MediumClientTests` | 18 | request shape, retry classification by status, timeouts, caller cancellation, block pages, with a scripted fake transport |
| `GraphQlTests` | 16 | `__ref` resolution (nested, chained, missing, cyclic), response classification, lone-surrogate tolerance |
| `MediumUrlTests` | 44 | hash vectors ported from Freedium, URL shapes, short links via stub, fallback, redirect loops |
| `ModelsRobustnessTests` | 11 | nulls, bad types, absurd numbers, casing |
| `CommandLineTests` | 30 | parsing rules |
| `OutputPathsTests` | 18 | path resolution and overwrite refusal on a real temp directory |
| `FileNameTests` | 17 | slugs: scripts, accents, byte budget, surrogates, forbidden characters |
| `ImageDownloaderTests` | 19 | stub transport: failure isolation, partial files, timeouts, cancellation, bounded parallelism, link rewriting |
| `CliProcessTests` | 14 | the real `ripium.dll`: exit codes, no crash on network failure, refusal before network, no file for option-like `-o` value |
| `TextSimilarityTests` | 8 | ratio equals Python `difflib` on known pairs |

(Counts are xunit results including each theory row, as of writing: 655 in total. A reliable way to recount is `dotnet test --logger "trx;LogFileName=results.trx"` and counting `UnitTestResult` per class; `--list-tests` groups theory rows inconsistently.)

## Fixtures

`tests/Ripium.Tests/Fixtures/post_<id>.json`: the **resolved `data.post` object** (what `MediumClient.GetPostAsync`
returns), compacted. They are copied to the test output directory by the csproj.

| Id | Why it is there |
|---|---|
| `27832c8f6644` | locked post, plain prose, hero image, subtitle de-duplication |
| `252990763468` | link cards, a YouTube embed (embedly URL), ordered lists, `PRE`, quotes, bold+italic everywhere |
| `4e4017b89905` | `H2` sections, image rows and full-width layouts, linked images, tweets, `PQ`, `SECTION_CAPTION` |
| `9dc802a10618` | code-heavy: inline code with backslashes and `$` (found the math-eating bug), lists |
| `d4222bd1a0b3` | emoji throughout (pins UTF-16 offset handling) |

To add one (a throwaway program referencing `src/Ripium`):

```csharp
using var client = new Ripium.Medium.MediumClient();
var post = await client.GetPostAsync("<id>");
File.WriteAllText("post_<id>.json", post.ToJsonString());   // compact is fine
```

Captured articles are public Medium content used as test data. Do not add fixtures containing member-only text
beyond what the test needs.

## The round-trip harness (`InlineRoundTripTests`)

- `Check(text, markups, noLiteralDelimiters)` renders, parses with two pipelines, collapses whitespace and compares the
  visible text to the original. With `noLiteralDelimiters` the alphabet contains none of the characters star, underscore, backtick, tilde, square
  brackets and backslash, so *any* of them in the visible text is a leaked delimiter.
- Pipelines: plain CommonMark, and a deliberately harsh "extended" one (tables, definition lists, custom containers
  minus inline `::`, footnotes, math, alpha lists, `==mark==`, strikethrough, auto-links, generic attributes...).
  Markdig's `++`, `^` and `""citation""` are excluded on purpose (documented limitation).
- Fuzz: seeds 1-8, 2,500 cases each by default. Alphabets: "plain" and "unicode plain" (no delimiter characters, so any
  leaked delimiter shows), and "hostile" and "unicode hostile" full of Markdown punctuation; the Unicode ones add
  no-break/ideographic/zero-width spaces, combining marks, Devanagari, emoji, symbols and a lone CR. Markups are random: any type, start/end anywhere in `[-2, length+2]` (inverted, out of range),
  links with parentheses in the URL.
- `RandomDocuments_ShowEveryParagraphsTextWhenParsed` builds random articles (P, PQ, BQ, ULI, OLI, H2-H4, IMG with caption) from
  the same generator and checks that every paragraph's text is visible in the parsed document. Single paragraphs cannot show
  block interactions, indentation after hard breaks or emphasis across line breaks.
- On failure the message prints text, markups, rendered output and the visible text, with non-ASCII shown as `<U+XXXX>`. Turn it into a named `[Fact]` in the
  "reviewer repros" section before fixing, so the regression stays.
- After **any** change to `InlineMarkdown.cs` run the deep fuzz with at least `RIPIUM_FUZZ_CASES=300000`
  (about 90 s; 200,000 takes about a minute; it covers the inline and document tests).
  Rare failures only show up at that size (the checked-in default found nothing for a long time while 300k found several).

## The second parser (markdown-it)

Markdig follows CommonMark 0.31. Parsers built on 0.30 (markdown-it, cmark-gfm 0.29) treat emoji, combining marks and some
symbols differently, and a review found hundreds of failures there that Markdig could not see. Run the oracle after
touching flanking, escaping or `Emit`:

```sh
pip install markdown-it-py
RIPIUM_ORACLE_EXPORT=/tmp/cases.jsonl RIPIUM_FUZZ_CASES=50000 dotnet test --filter ExportCasesForExternalOracle
python3 -I tools/markdown-it-oracle.py /tmp/cases.jsonl
```

Expect a handful of failures per 100,000 cases, all in paragraphs with several overlapping strong/italic ranges (the documented
class; see rendering.md for the shape and measured rates). Last measured: 8 in 200,000. A failure with a single markup, or
involving links, code or block syntax, is a bug. The script expects control characters to be replaced by U+FFFD, as Ripium does.

## Conventions

- Use eight-digit capital-U escapes for special characters in C# test source (for example `\U0000FEFF`, `\U0001F600`).
  The editing tools used to write this repo decode lowercase backslash-u escapes into literal characters, which for
  U+2028 or U+0085 *breaks the string literal* (they are line terminators in C#).
- Lone surrogates cannot be carried by `[InlineData]` (xunit replaces them); build them in code with `(char)0xD83D`.
- Temp directories: `Directory.CreateTempSubdirectory(...)`, cleaned in `Dispose`.
- Prefer asserting exact output for engine behaviour and parser-visible text for validity; do both for a fix.
- Test names describe the behaviour, not the method.

## Adding a regression test for a bug

1. Reproduce through the narrowest public entry point (`InlineMarkdown.Render`, `MarkdownRenderer.Render`, ...).
2. Write the failing test first; confirm it fails for the right reason.
3. Fix; run `dotnet test`; for inline changes run the deep fuzz.
4. If the bug came from real data, consider adding the article as a fixture.

## Not covered (and why)

- Live network behaviour (Cloudflare, rate limits, `link.medium.com`): non-deterministic. Verify by hand with the
  commands in the README after touching `MediumClient`.
- Rendering in specific applications (Obsidian, GitHub): approximated by Markdig pipelines.
- Windows path behaviour: developed and tested on Linux only.
- Very large articles: the largest fixture is 311 paragraphs; no dedicated scale test.
