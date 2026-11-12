# Ripium maintainer documentation

Ripium is a .NET 10 console app that exports a Medium article to a Markdown file. It is a port of the *fetch + parse*
half of [Freedium](https://freedium.cfd) with a different output: Markdown instead of HTML. These documents are
written for someone (human or LLM) who has never seen the code and has to fix, extend or debug it safely.

## Read in this order

| Document | Read it when |
|---|---|
| [architecture.md](architecture.md) | You need the map: pipeline, files, types, error model. Start here. |
| [medium-api.md](medium-api.md) | Fetching breaks, Medium changes, or you need to know what the JSON means. |
| [rendering.md](rendering.md) | Output is wrong: block rules, the inline-markup engine, escaping, known limitations. |
| [cli.md](cli.md) | You are changing options, output paths, image handling or exit codes. |
| [testing.md](testing.md) | Before changing anything. What is tested, how, and how to add a regression test. |
| [maintenance.md](maintenance.md) | Runbooks for common changes, pitfalls that already cost time, backlog. |
| [decisions.md](decisions.md) | You want to know *why* something is the way it is before "fixing" it. |
| [freedium-reference.md](freedium-reference.md) | You are comparing with the Python reference in `freedium-src/`. |

## Five-minute orientation

```text
input URL/id ──MediumUrl──▶ post id ──MediumClient──▶ JsonObject (resolved data.post)
                                                          │ Post.FromJson
                                                          ▼
                       Post (DTOs) ──MarkdownRenderer (+InlineMarkdown, Frontmatter)──▶ RenderResult
                                                          │
                          ImageDownloader (optional) ◀────┤ markdown text + image URLs
                                                          ▼
                                        OutputPaths ──▶ file (or stdout)
```

```sh
dotnet build                      # solution: Ripium.slnx (src/Ripium + tests/Ripium.Tests)
dotnet test                       # 655 offline tests, about 6 s; no network needed
dotnet run --project src/Ripium -- 27832c8f6644 -o /tmp/out/         # live run (needs network)
RIPIUM_FUZZ_CASES=300000 dotnet test --filter "RandomMarkups|RandomDocuments"   # deep fuzz after touching InlineMarkdown
```

## The five rules that matter most

1. **The visible text of every paragraph must survive.** The test suite parses the output with a real Markdown parser
   and compares text. Formatting may be dropped when it cannot be written validly; text may never be lost, and
   delimiters may never leak as literal characters. See [rendering.md](rendering.md#the-contract).
2. **Medium offsets are UTF-16 code units, which is how .NET indexes strings.** Use them directly. Never convert to
   code points, and never split a surrogate pair. See [medium-api.md](medium-api.md#markup-offsets).
3. **Untrusted article text does not become syntax.** Everything is escaped, control characters are replaced, links are
   scheme-checked, image ids are validated, file names are sanitised. See [rendering.md](rendering.md#escaping) and [cli.md](cli.md#files-it-writes).
4. **No unhandled exception may leave `Main`.** Expected failures exit 1 with one `error:` line. See
   [architecture.md](architecture.md#error-model).
5. **Never overwrite a user's file silently.** `--force` is required. See [cli.md](cli.md#output-path-rules).

## Project conventions (from `AGENTS.md`, which is authoritative)

- Every code change ships with automated tests, including failure paths and adversarial input.
- Non-trivial work gets an independent read-only review before it is called done.
- Document deliberate shortcuts in a comment as `shortcut: <the limit>, <when to upgrade>`.
- State what was verified and what was not; never claim a check that did not run.

## Status at the time of writing

- 655 tests pass (`dotnet test`). The inline engine additionally survived a deep fuzz (200,000 cases per seed, inline and
  document level) against CommonMark and an extended Markdig pipeline, and a markdown-it differential run (8 failures in
  200,000 adversarial paragraphs, all with several overlapping emphasis ranges; see rendering.md for the shape and rates).
- Fetching works with a plain `HttpClient` and no cookies, including for posts marked `isLocked`.
- Not verified live: `link.medium.com` short links (covered by a stubbed resolver only), non-Medium custom domains,
  long-term Cloudflare behaviour. See [medium-api.md](medium-api.md#risks-and-unknowns).
