# Decisions and deviations

Each entry: what was decided, why, and what would justify revisiting it. Most come from testing against real
articles and from independent reviews, not from taste.

## Fetching

**D1. Plain `HttpClient`, Freedium's headers, no TLS impersonation.** A spike showed Medium answers it (200, full body
for locked posts). Freedium needs curl_cffi because it serves at scale; a single-article tool does not. *Revisit if*
403s or HTML-with-200 appear: add cookies, then proxy, then `curl-impersonate`.

**D2. Retry only transient failures.** Network errors, timeouts, 408, 429, 5xx and unparseable bodies are retried
(2 attempts, 2 s then doubling). A missing post and other 4xx are answers. `PostUnavailableException` is a type, not a
message check. `errors` without `data` is a failed request (retried), not "post missing".
*Why:* an earlier version retried 404s (two seconds wasted) and, worse, would have reported a Medium-side failure as
"post does not exist".

**D3. No domain allow-list for URLs.** The id in the slug is enough; the last-token fallback mirrors Freedium. The query
string is excluded from id extraction because tracking values look like ids.

**D4. Lone-surrogate JSON escapes become U+FFFD before parsing.** One half-emoji in a title otherwise lost the whole
article (`InvalidOperationException`).

## Model and robustness

**D5. All DTO members are nullable; null array elements are removed in `Post.FromJson`; shape errors become
`MediumException`.** A `null` start offset used to throw `JsonException` and abort the article. Tolerant by default,
because partial data beats no output; a mistyped field still fails loudly with its JSON path.

**D6. UTF-16 offsets are used directly.** .NET strings are UTF-16, as are JavaScript's. Freedium's converter was wrong for
astral characters. Offsets are subtracted as `long`, clamped, and snapped out of surrogate pairs.

## Rendering

**D7. Text beats formatting.** The inline engine drops formatting that cannot be written validly (flank check, doomed
reopen, unsafe link) instead of emitting something that prints delimiters or loses text. This is a *testable* contract
(round trip through a real parser), which is why the engine could be rewritten with confidence.

**D8. Emphasis is `**` and `*`, never `_`.** `_` does not work at word boundaries. Cost: `***x***` for both.

**D9. Crossing markups are closed and reopened; longest-lived style opens first; code is always innermost.**
Medium allows arbitrary overlap; Markdown does not. Alternatives rejected: clipping the later span (loses formatting
even when it could be written), HTML tags (not Markdown).

**D10. Escaping policy.** Escape everything CommonMark and GFM act on, plus what mainstream editors act on
(`$` math, `==highlight==` and `%%comments%%` in Obsidian). Do **not** escape Markdig-only syntaxes (`++`, `^`,
`""`, inline `::`) because that would deface common prose ("C++", "std::vector") for one parser. Line-start constructs
are judged against the whole line and decided at the completing character, because markup boundaries cut segments
mid-construct. *Revisit if* a mainstream viewer starts acting on one of the excluded syntaxes.

**D11. Link safety by allow-list** (`http`, `https`, `mailto`, `tel`, relative). Article text is untrusted and Markdown
viewers may act on `javascript:` or app schemes. Control characters are stripped before the check because browsers
ignore them inside schemes.

**D12. Image ids are validated; unusable ones are skipped with a warning.** An id becomes part of a URL and a file name.

**D13. Only the single character `…` marks a truncated title or subtitle.** Treating "..." as truncation let a
legitimate title be overwritten by a similar early heading (reviewer finding). Freedium also only checks `…`.

**D14. The preview image paragraph stays in the body as the hero image** (Freedium removes it because its page header
shows it). The frontmatter also records `preview_image`.

**D15. `SECTION_CAPTION` is rendered as a paragraph** (Freedium drops it, losing text such as bylines).

**D16. Mixtape title/description are found by markup type, not position.** One real card has no description.

**D17. Code spans turn newlines into spaces.** A newline inside a span lets block syntax claim the next line, and escapes
are impossible inside code. CommonMark renders such a newline as a space anyway.

**D18. Smart quotes are kept as typed.** Freedium flattens them to ASCII for its HTML; Markdown does not need that.

**D19. No table of contents in the frontmatter, no byline when frontmatter is on, subtitle in italics.** The document
already shows its headings.

**D20. Heading levels map one to one (`H3` becomes `###`).** Re-basing is on the backlog; it changes the outline users see.

## CLI and files

**D21. Existing output files are never overwritten without `--force`, checked before any network access for explicit
paths.** The default file name is derived from the title; silently clobbering a user's note is data loss. (The original
design overwrote silently; an independent review and a live test showed the risk.)

**D22. A path without an extension gets `.md`; a trailing `/` or an existing directory means "put it inside".**
Ambiguity resolved in favour of never creating an extensionless Markdown file.

**D23. `-o` values that look like options are rejected; value options may not repeat.** `-o --remote-images` used to
write a file called `--remote-images`.

**D24. Exit codes: 0, 1 expected failure, 2 usage, 70 bug, 130 interrupt.** Distinguishing 70 from 1 makes "ripium
crashed" visible to scripts. A timeout is reported as a timeout, not as "Cancelled".

**D25. Images are downloaded by default into `<name>_assets/`; one failure never aborts; partial files never remain.**
The `.md` should work offline; a flaky image should not cost the article.

**D26. Slugs keep letters and digits of every script.** An ASCII-only slug turned Russian or Chinese titles into the
post id. Hangul must not decompose into jamo, so only Latin accents are folded. Names are capped at 150 UTF-8 bytes.

**D27. Cookies via flag or `MEDIUM_AUTH_COOKIES`; the environment variable is recommended** because command lines are
visible in process listings. No cookie persistence.

## Testing and tooling

**D28. Markdig and YamlDotNet are test-only dependencies.** They are the oracles for "valid Markdown" and "valid YAML";
hand-written string comparisons were shown (by independent review) to miss real bugs. The app itself has zero runtime
NuGet dependencies.

**D29. The fuzz harness is part of the test suite and scalable by environment variable.** Fixed seeds keep CI stable; a
large run is mandatory after engine changes because rare cases appear only at 100k+ cases.

**D30. Process-level CLI tests with a dead proxy.** The unhandled-exception bugs lived in `Program`, which unit tests could
not reach. No network is used.

## Added after the second independent review

**D31. Link destinations are made literal before the scheme is checked.** The parser decodes backslash escapes and
entities inside a destination *after* Ripium's check, so `javascript\:x`, `javascript&colon;x`, `jav&#97;script:x` and
`java&Tab;script:x` passed the check and became `javascript:` links (reproduced with Markdig). Backslashes and entity-shaped
ampersands are now percent-encoded; ordinary URLs (`&a=1&b=2`, `%20`) are untouched.

**D32. A blank is any Unicode space except a line break.** CommonMark's flanking rules use Unicode whitespace, and no-break
space is common in Medium text. Trimming only space and tab leaked `**` next to an NBSP. One predicate
(`InlineMarkdown.IsBlank`) is used by the engine, the pull-quote/caption/subtitle wrapper and the list-marker tests (Markdig
treats `+` followed by an ideographic space as a list item).

**D33. Flanking decisions must hold under both CommonMark 0.30 and 0.31.** The editions disagree on whether emoji and
symbols are punctuation. A fragile edge needs a neighbour that is whitespace or punctuation under the *narrower* definition;
combining marks, zero-width characters and non-ASCII symbols count as blockers. Verified against Markdig (0.31) in the suite
and against markdown-it (0.30) with `tools/markdown-it-oracle.py`: 514 failures in 80,000 hostile cases before, 8 in 200,000 after.

**D34. Emphasised blocks (pull quotes, captions, subtitle, byline) get one pair of asterisks around the whole text.** Wrapping
each line separately was based on a wrong belief that emphasis cannot span a hard break; it can, and per-line wrapping tore
links and code that crossed a break.

**D35. Heading line breaks are flattened before rendering.** Replacing the rendered hard break afterwards produced `** x**`.

**D36. Title/subtitle matching: equal after normalisation, or fuzzy only for 15+ letters with identical digits.**
Freedium's plain 80% rule drops real headings ("Chapter 2" under "Chapter 1", "Part B" under "Part A"). This deviates from the
reference on purpose; a repeat of the title that differs only in case, punctuation or a word in a long title is still dropped.

**D37. The output file is written through a temporary file and moved into place; image names never collide.** The
check-then-write sequence had a race (a file created during the download was overwritten); asset names are index-based, so a
re-export silently replaced old images. `File.Move` without overwrite fails if the target exists, and a taken image name gets
a `-2` suffix unless `--force`.

**D38. Hostile responses are bounded** (see also D46 and D52, which closed gaps in this first attempt). Reference chains stop after
24 nested references (a 20,000-entity chain overflowed the stack), JSON depth is 512, a post that is not an object is a
`MediumException`, and the response is decoded as UTF-8 bytes (an unknown charset in `Content-Type` threw `InvalidOperationException`).

**D39. Paragraphs with nothing visible are dropped; a lone CR is a line break.** A zero-width-space paragraph used to be written
as an invisible line; a lone CR used to be deleted, joining words.

**D40. Images must be `image/*` (or octet-stream) and at most 50 MB.** An HTML error page with status 200 was saved as `.jpeg`
and nothing limited the size.

**D41. DTO members nothing reads are not declared.** (`IsLocked`, ids, slugs, thumbnails, layout, code `mode`, ...) The full
response is documented in [medium-api.md](medium-api.md); unknown JSON members are ignored.

**D42. Accepted limits found by the review, documented rather than fixed:** `Emit` is quadratic in the markups of one paragraph
(a `shortcut:` comment marks it; a test guards against it getting worse); several overlapping emphasis ranges can leak
under older parsers (about 0.004% of random adversarial paragraphs, 0.35% of exhaustive triples; see rendering.md); GraphQL `errors` without `data` are retried even if they say
NOT_FOUND; Markdig-only `{#id}` and `^^x^^` are not escaped.

## Added after the third independent review

**D43. A blank is CommonMark whitespace, not .NET white space.** NEL, U+2028, U+2029 and vertical tab are white space to
`char.IsWhiteSpace` but not to a parser; moving delimiters across them leaked asterisks (1,500-2,900 failures per 20,000
fuzz cases once those characters were included). `IsBlank` is now the Unicode space-separator category plus tab and form
feed (line breaks are excluded from blanks but still count as whitespace in `BlocksFlanking`).

**D44. Entity-shaped text is recognised up to 32 characters.** HTML5 names reach 31 (`&CounterClockwiseContourIntegral;`).

**D45. The output file is written through a short, fixed-length temporary name.** Appending to the target's name made
names of about 219-255 bytes unwritable, after the whole fetch.

**D46. Response handling is meant to throw nothing but `MediumException`** (non-object `errors` entries, wrong node types, node-heavy
reference graphs). The ref resolver bounds copied *nodes* (1,000,000) as well as references: 45,000 references to a 20 KB entity
used to expand to 900 MB from a 700 KB response. **This entry first claimed duplicate JSON keys were covered; they were not**, see D52.

**D47. An image has a time limit for the whole body.** `HttpClient.Timeout` covers only headers when the body is streamed.

**D48. The frontmatter `url` is validated in the form that is written**, as a plain absolute http(s) URL, and the first
valid of `canonicalUrl`/`mediumUrl` is used. The earlier check ran on the *encoded* form, which hid `<javascript:...>`.
Other frontmatter strings are data and are only quoted.

**D49. Control characters are replaced by U+FFFD** in document text and in everything written to stderr, one for one so
markup offsets stay valid. A NUL is shown as U+FFFD by parsers anyway, and an ESC in a file is interpreted by a terminal
that merely displays it.

**D50. Texts with no letters or digits never match each other** when removing title repeats (two different emoji, or `???`
and `!!!`, both normalised to nothing and were "equal").

**D51. Usage text in the READMEs is tested against `CommandLine.Usage`.** Two documents had drifted from the program within a day.

## Added after the fourth independent review

**D52. Everything that touches a parsed `JsonNode` sits inside the exception translation.** `JsonNode` builds an object's property
table lazily, so a duplicate key throws `ArgumentException` at the first indexer access, not at `Parse`. The first fix wrapped only
`Parse` and resolution, so `{"data":{"post":{}},"data":{}}` still reached `Program` as exit 70 (verified by running the real
executable behind a TLS-intercepting proxy). The tests now cover duplicates at every level and through `GetPostAsync`, and were
confirmed to fail against the old code.

**D53. Reference expansion has a byte budget (64 MB of serialised entities) next to the node and reference limits.** A node count
cannot see size: a string is one node. 400 references to a 1 MB string allocated 1.5 GB, and 2,000 killed the real app with
`OutOfMemoryException`. The size of each referenced entity is measured once.

**D54. The response body is read with a size limit (64 MB)** and a larger one is a non-retryable `ResponseTooLargeException`.

**D55. At most 2,000 markups per paragraph are used.** `Emit` is quadratic in them; a hostile response with tens of thousands in one
paragraph could occupy minutes. The largest real paragraph has about 250. The text is never affected, only formatting beyond the limit.

**D56. Terminal output is one line.** `Messages.OneLine` replaces control characters *and line breaks*, and bounds the length, for
everything printed that came from a server, an article or the command line (including usage errors that echo an argument). Keeping
line breaks let a paragraph type forge a line such as "Wrote /etc/passwd".

**D57. Frontmatter strings get the same control-character replacement as the body.** They used to be written as YAML escapes that
parse back into real ESC characters.

**D58. `MEDIUM_AUTH_COOKIES` is validated like `--cookies`** (printable ASCII only; exit 2): its value goes into an HTTP header, and
a CRLF in it was sent as a second header.

**D59. The assets folder name is shortened if needed** (`OutputPaths.AssetsFolderName`): the `.md` may be valid at 255 bytes while a
folder seven bytes longer is not.

**D60. Title matching keeps `# + & % $ @ =`.** "C#" and "C++" no longer count as the same heading; two texts that normalise to
nothing match only when identical.

**D61. Known and accepted:** `--force` replaces the file (move over the target), so permissions, hard links and symlinks are not
preserved; `-o ...` writes a file called `.md`; a link labelled exactly `x` inside a list item is read as a checkbox by Markdig's
task-list extension; an overlong URL or one with a bidi override passes the frontmatter `url` check.

## Deviations from the Python reference (summary)

| Reference behaviour | Ripium | Why |
|---|---|---|
| UTF-16 to code point conversion | none | .NET is UTF-16 (D6) |
| Drops bold/italic overlapping a link or code | nests/reopens, drops only what cannot be written | keeps formatting (D7, D9) |
| Mixtape by `markups[1]`/`[2]` | by type | D16 |
| Drops `SECTION_CAPTION` | renders it | D15 |
| Smart quotes to ASCII | kept | D18 |
| Frontmatter has table of contents | no | D19 |
| Removes the preview image paragraph | keeps it | D14 |
| `"..."` and `…` both truncation (old parser) / only `…` (new) | only `…` | D13 |
| Code spans: no handling of newlines | newlines become spaces | D17 |
| Raw HTML (`<picture>`, `<iframe srcdoc>`), `decorations=` attributes | standard Markdown | goal of the project |
| No Markdown escaping | full escaping | D10 |
| Title/subtitle match at 80% similarity | normalised equality, or fuzzy only for 15+ letters with equal digits | D36 |
| Fetch only | atomic no-clobber output, image guards | D37, D40 |
| Redis/Postgres/Mongo caches, proxies, TLS impersonation, base64 images | none | out of scope (D1) |
