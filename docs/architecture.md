# Architecture

## Layout

```text
Ripium.slnx                         solution (new .slnx XML format; `dotnet build` at the root works)
src/Ripium/                         console app, assembly name "ripium" (ripium.dll), net10.0, nullable enabled
  Program.cs                        Main: parse args, orchestrate, map exceptions to exit codes
  Cli/CommandLine.cs                argument parsing + the usage text (the usage text is the CLI contract)
  Cli/Messages.cs                   one-line, control-free, bounded text for anything printed to the terminal
  Medium/MediumUrl.cs               any input (URL, wrapper URL, bare id) -> 8-12 hex char post id
  Medium/MediumClient.cs            GraphQL POST, headers, retry policy, response parsing; exception types
  Medium/GraphQlQuery.cs            the verbatim FullPostQuery document (one 5 KB constant)
  Medium/GraphQlRefResolver.cs      replaces {"__ref":"Type:id"} pointers with the entities they name
  Medium/JsonSanitizer.cs           turns lone-surrogate JSON escapes into U+FFFD before parsing
  Medium/Models.cs                  DTOs (Post, Paragraph, Markup, ...), Post.FromJson
  Markdown/MarkdownRenderer.cs      Post -> Markdown document: block rules, title/subtitle dedupe, images, embeds
  Markdown/InlineMarkdown.cs        the inline engine: markups + escaping + URL safety (the hard part)
  Markdown/Frontmatter.cs           YAML frontmatter and the always-safe quoted scalar writer
  Markdown/TextSimilarity.cs        difflib-compatible similarity ratio (title/subtitle dedupe)
  Markdown/Timestamps.cs            range-checked Unix-millisecond conversion
  Output/OutputPaths.cs             where the .md goes; overwrite refusal; atomic no-clobber write
  Output/ImageDownloader.cs         parallel downloads to <name>_assets/ (size/type guard, no overwrite), link rewriting
  Output/FileNames.cs               Unicode-aware slug for the default file name
tests/Ripium.Tests/                 xUnit; Markdig and YamlDotNet are test-only; Fixtures/*.json are captured articles
tools/markdown-it-oracle.py         optional second-parser check of the inline output (see testing.md)
docs/                               this folder
freedium-src/                       the Python reference app (untracked, read-only, not part of the build)
```

There are no runtime NuGet dependencies. Everything is `System.*`.

## Data flow in detail

1. **`CommandLine.Parse`** turns `argv` into `CommandLineOptions`. Bad input throws `ArgumentException` (exit 2).
2. **Fast refusal.** If `-o` names an explicit file that exists and `--force` is absent, `Program.RunAsync` stops
   before any network call (`OutputPaths.EnsureWritable`).
3. **`MediumUrl.ResolvePostIdAsync`** unwraps redirect wrappers (Facebook, Google, 12ft, `global-identity-2`,
   `link.medium.com` through `rsci.app.link`), then takes the id from the path, then falls back to "last id-shaped
   token in the input, query string excluded". Throws `InvalidMediumUrlException`.
4. **`MediumClient.GetPostAsync`** POSTs `FullPostQuery` (see [medium-api.md](medium-api.md)), retries transient
   failures, and returns `data.post` with `__ref` pointers resolved. Throws `PostUnavailableException`,
   `MediumHttpException` or `MediumException`.
5. **`Post.FromJson`** deserialises into DTOs (tolerant: unknown members ignored, nulls allowed, null array elements
   removed, JSON depth limit 512) and wraps shape errors (`JsonException`, `InvalidOperationException`) in `MediumException`.
6. **`MarkdownRenderer.Render`** produces a `RenderResult`:
   `Markdown` (frontmatter + `# Title` + body), `Title`, `ImageUrls` (remote URLs the document links to, in order,
   body images plus the preview image), `Warnings`. It is pure: no I/O.
7. **Output.** For stdout the markdown is written as is (images stay remote). For a file, `OutputPaths.Resolve`
   picks the path, `ImageDownloader.DownloadAsync` fetches `ImageUrls` into `<name>_assets/`,
   `ImageDownloader.RewriteLinks` points the document at the local copies, then `OutputPaths.WriteAsync` writes the file
   (UTF-8, no BOM, LF) through a temporary file that is moved into place, so a file that appeared since the check is
   never replaced without `--force`.

## Key types

| Type | Role |
|---|---|
| `Post`, `Paragraph`, `Markup`, `ImageMetadata`, `IframeData`, `MixtapeMetadata`, ... | DTOs for the part of Medium's JSON we use. All members nullable/optional. |
| `RenderOptions(ImageWidth = 1400, IncludeFrontmatter = true)` | The only renderer inputs besides the post. |
| `RenderResult(Markdown, Title, ImageUrls, Warnings)` | Renderer output. `Title` may differ from `Post.Title` (truncated titles are completed). |
| `InlineMarkdown` (static) | `Render(text, markups, ignoreEm)`, `EscapeText`, `FormatDestination`, `IsSafeUrl`, `Slice`, `EscapeAtxClosingSequence`. |
| `MediumException` and subclasses | See the error model below. |

## Error model

| Exception | Meaning | Retried? | Exit code |
|---|---|---|---|
| `ArgumentException` (from `CommandLine`) | bad usage | n/a | 2 |
| `InvalidMediumUrlException` | no post id in the input | n/a | 1 |
| `ResponseTooLargeException : MediumException` | body over 64 MB | no | 1 |
| `PostUnavailableException : MediumException` | Medium answered, `data.post` is null (deleted, private, wrong id) | no | 1 |
| `MediumHttpException : MediumException` | non-200 status; carries `StatusCode` | only 408, 429, 5xx | 1 |
| `MediumException` | not JSON (including duplicate keys), `errors` without `data`, unexpected shape (`InvalidOperationException`, `ArgumentException` and `InvalidCastException` from response handling are wrapped into it), retries exhausted | yes (inside the loop) | 1 |
| `HttpRequestException`, `TaskCanceledException` (timeout) | network failure / timeout | yes inside `GetPostAsync` | 1 |
| `IOException`, `UnauthorizedAccessException`, `JsonException` | file system / parsing; includes "file exists, use --force" | no | 1 |
| anything else | a bug in ripium | no | 70 |
| `OperationCanceledException` with the user's token cancelled | Ctrl-C | never | 130 |

Rules that keep this honest:

- A timeout is a `TaskCanceledException` that the *user did not* cause, so `Program` checks
  `cts.IsCancellationRequested` first and only then treats it as an expected failure.
- `MediumClient` rethrows caller cancellation unwrapped and does not retry it.
- Image downloads never throw for a single bad image (see [cli.md](cli.md#images)).
- `PostUnavailableException` is a dedicated type on purpose. An earlier version matched the message text, which broke
  silently when the wording changed.

## Concurrency and state

- `MediumClient` is not thread-safe by contract but holds no mutable state besides the `HttpClient`; Ripium uses one
  per run and disposes it.
- `ImageDownloader` downloads up to 4 images in parallel (`SemaphoreSlim`), results are collected under a lock.
- `MarkdownRenderer` has per-render mutable lists, reset at the start of `RenderPost`, so an instance may be reused
  sequentially. Do not share one instance between threads.
- `InlineMarkdown` and the other statics are stateless.

## Performance notes

Measured on the development machine (Release build, one run each; indicative only):

| Input | Time |
|---|---|
| one paragraph, 500 chars, 20 random markups | about 65 ms, but that is JIT warm-up (first call in the process) |
| one paragraph, 5,000 chars, 200 random markups | 6 ms |
| one paragraph, 20,000 chars, 800 random markups | 30 ms |
| one paragraph, 50,000 chars, 2,000 random markups | about 105 ms |
| whole fixture articles (31 to 132 paragraphs), `MarkdownRenderer.Render` | 0.3 to 2.7 ms each |

`InlineMarkdown.Emit` is roughly `O(intervals x spans)` per paragraph; real paragraphs have a handful of markups, and the
random inputs above are far denser than any article. Network time and image downloads dominate a real run. The ref
resolver copies the tree and is bounded by `MaxExpansions = 50_000` expansions plus cycle detection.
