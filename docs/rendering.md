# Rendering

Code: `Markdown/MarkdownRenderer.cs` (blocks), `Markdown/InlineMarkdown.cs` (inline), `Markdown/Frontmatter.cs`.

## The contract

For every paragraph, **parsing the rendered Markdown must show exactly the original text** (whitespace collapsed).
That means:

- no character is eaten (for example a `!` before a link becoming an image, `$5 ... $10` becoming math, `b.` becoming
  a list marker, `==x==` becoming a highlight);
- no delimiter leaks as a literal character (`**`, `*`, backticks);
- text that merely *looks* like block syntax never becomes a list, heading, quote, table, rule or fence.

When a piece of *formatting* cannot be written validly, the formatting is dropped and the text is kept. Text beats
formatting every time. The contract is enforced by `InlineRoundTripTests` and `MarkdownRendererRobustnessTests`,
which parse output with Markdig (CommonMark and an extended pipeline), and by a manual second-parser check with markdown-it
(`tools/markdown-it-oracle.py`); see [testing.md](testing.md).

## Document shape

```text
---                       YAML frontmatter (omitted with --no-frontmatter)
title: ...
---

# Title                   escaped; a trailing " #" is escaped so it is not read as a heading closer
*Subtitle*                only if present (italic, escaped)
*By Author · Month d, yyyy · N min read*      only when frontmatter is off

<body blocks, each separated by exactly one blank line>
```

Blocks are joined with `"\n\n"`; there is **no global blank-line collapsing** (an earlier design collapsed them and
would have altered code blocks).

### Frontmatter keys (in this order, each only when known)

`title`, `author`, `author_url` (`https://medium.com/@username`), `publication` (collection name), `subtitle`,
`published` and `updated` (UTC `yyyy-MM-ddTHH:mm:ssZ`, `updated` only if different, both skipped when out of range),
`reading_time` (whole minutes, rounded up; written only when 0 < minutes < 10,000), `url` (canonical, else Medium URL), `tags` (list),
`preview_image`. All strings are double-quoted YAML scalars through `Frontmatter.Quote`, which also escapes
characters YAML forbids or folds (C0/C1 controls, NEL, U+2028/2029, BOM, U+FFFE/FFFF, lone surrogates). There is no
table of contents (Freedium has one; the headings are visible in the document).

`preview_image` is the remote URL; when images are downloaded `RewriteLinks` points it at the local copy.

## Block rules (`MarkdownRenderer.RenderSingle` and friends)

| Medium type | Markdown |
|---|---|
| `H1`, `H2` | `## text` (line breaks inside heading text are flattened to spaces *before* rendering, one for one, so offsets stay valid) |
| `H3` | `### text` |
| `H4` | `#### text` |
| `P`, `SECTION_CAPTION` | inline-rendered paragraph |
| consecutive `ULI` | `- item` lines |
| consecutive `OLI` | `1. item` lines, renumbered from 1 per run |
| consecutive `PRE` | one fenced block; fence is longer than any backtick run inside (min 3); info string is the first word of `codeBlockMetadata.lang`, empty if none; markups inside code are ignored; text is verbatim |
| `BQ` | `> ` before every line |
| `PQ` | `> *text*`: one emphasis around the whole text (it may span a hard break, which matters when a link or code crosses the break); italic markups are dropped so they cannot flip it into bold; blanks at the ends stay outside the asterisks |
| `IMG` | `![alt](url)` plus a caption paragraph in italics; wrapped in a link if the image has a safe `href`; skipped with a warning if the id fails validation |
| `MIXTAPE_EMBED` | block quote `> [**Title**](href)` + hard break + `> Description`; plain text if the href is missing or unsafe |
| `IFRAME` | `[Embedded content: <title>](url)`; url is `iframeSrc` (embedly wrappers unwrapped to their `url=` parameter) if safe, else `https://medium.com/media/<id>` if the id is alphanumeric, else skipped |
| anything else | plain paragraph + a warning |

A paragraph whose text has nothing visible (only blanks, zero-width spaces or joiners, control characters) is dropped.

Heading levels are mapped one to one (`H3` becomes `###`) and never re-based, so an article whose sections are `H3`
under `# Title` skips level 2. Items inside a list are single paragraphs; line breaks inside an item become a hard
break plus indentation. Empty blocks are skipped.

### Title, subtitle and tag de-duplication (`DropRedundantLeadingParagraphs`)

Medium repeats the title, subtitle and tags at the top of the body. Within the first 4 paragraphs only:

- an `H1`-`H4` whose text matches the title is dropped;
- an `H4` equal to a tag's display title is dropped;
- an `H4` or `P` matching the subtitle is dropped.

"Matches" (`MarkdownRenderer.Matches`): the two texts are equal after lower-casing and removing everything but letters and
digits ("Stop Wasting Your Life" and "stop wasting your life!" match); or both are at least 15 letters/digits long, contain
exactly the same digits, and `TextSimilarity.Ratio(...) > 0.80` (the gestalt measure of Python's `difflib`, checked
against Python output in tests). **Short or digit-differing texts never match fuzzily**: Freedium's plain 80% rule dropped
a real "Chapter 2" heading under the title "Chapter 1" (89% alike) and would drop "Part B" under "Part A".
A title or subtitle ending in the single character `…` (U+2026) was truncated by Medium: the heading whose text
*starts with* the stub completes the title; a truncated subtitle is discarded and the paragraph it came from is kept.
When a heading matches only fuzzily (for example "Observable" in the metadata title and "Observables" in the heading) it is dropped and
the *metadata* title is kept, so the heading's slightly different wording is lost; only a truncated (`…`) title is completed from
the heading. **Three ASCII dots are not truncation** ("Wait for it..." is a real title). The preview image paragraph is *not* dropped;
it stays in the body as the hero image.

### Images

URL: `https://miro.medium.com/v2/resize:fit:<ImageWidth>/<id>`, `ImageWidth` 1400 unless `--image-width`. Every URL the
document links to goes into `RenderResult.ImageUrls` (deduplicated, in order, preview image included when frontmatter
is on). Ids are validated first (see [medium-api.md](medium-api.md#images)). Alt text goes through `EscapeText`.

## The inline engine (`InlineMarkdown`)

Entry point: `Render(text, markups, ignoreEm)`. Input is one paragraph's text and its markups; output is one or more
lines of Markdown. The steps, in order:

1. **Trim** the paragraph text and shift offsets by the removed leading amount (so a trailing newline can never become
   a dangling hard break). Offsets are `long`-subtracted and clamped, so `int.MinValue`/`MaxValue` are safe. Text with
   nothing visible returns an empty string; control characters other than tab and line breaks become U+FFFD one for one
   (`ReplaceControlCharacters`), so offsets stay valid. A *blank* throughout the engine is what CommonMark calls
   whitespace other than a line break (`InlineMarkdown.IsBlank`): space, tab, form feed and the Unicode space separators
   (no-break, em, ideographic space ...). NEL, U+2028, U+2029 and vertical tab are **not** blanks: .NET calls them white space
   but parsers do not, and treating them as blanks leaked asterisks.
2. **Candidates.** For each markup with non-null offsets: clamp, snap out of surrogate pairs, then
   - `STRONG`/`EM`: trim blanks, then `NormalizeEmphasisEdges` (below); require a letter or digit inside;
   - `CODE`: trim blanks; punctuation-only code (`->`) is kept;
   - `A`: href (or `https://medium.com/u/<userId>` for `USER`) goes through `FormatDestination` and `IsSafeUrl`;
     unsafe links are dropped; the range is trimmed and *clipped* so it never overlaps a link with a different
     destination (Markdown cannot nest links; a piece of a later link survives only where no earlier link is).
3. **Priority.** A `CODE` with the same range as a link is dropped; `STRONG`/`EM` with the same range as `CODE` is dropped.
4. **Merge** same-style ranges that overlap, touch, or are separated only by blanks into runs (the emitter treats them
   as one run anyway, so flanking is judged on the run).
5. **Flank check** (`DelimitersCanFlank`): an emphasis whose opening edge has code inside it right after a letter, or
   whose closing edge ends where code or an *inner* link ends right before a letter, is dropped. CommonMark cannot
   express `word**`code`**` or `**[x](u)**word`.
6. **Emit** (`Emit`): cut the text at every boundary into intervals; for each interval know the active styles. Walk
   the intervals keeping a stack of open styles:
   - a blank-only interval is not formatted at all; its blanks wait and are written outside delimiters;
   - styles that stop being active are closed; any inner style that continues is closed and reopened (crossing
     markups). If the closers and reopened openers would form an unparseable `*****` run with no blank between, the
     reopened style's formatting is dropped for the rest of that run;
   - **ordering**: code is always innermost (nothing inside a code span is interpreted, so emphasis inside would print
     asterisks); otherwise the style that lasts longest opens first (fewer crossings); one exception: right after a
     letter a link opens outside emphasis (`a[**x**](u)`), because `**[` cannot open after a letter;
   - delimiters never touch blanks on their inner side: leading/trailing blanks of an interval are moved outside;
   - code text has line breaks (LF, CRLF, lone CR) replaced by spaces (a continuation line like `# x` would otherwise be
     claimed by block syntax before the span is parsed, and escapes do not work inside code);
   - an escaped backtick directly before a code delimiter is written as `&#96;`.
7. Plain intervals go through `EscapePlain`.

### `NormalizeEmphasisEdges` and the flanking rules

CommonMark: a `*` run can open only if it is left-flanking, and a delimiter followed by punctuation is left-flanking only
when preceded by whitespace or punctuation (closers mirror this). Two editions of the spec disagree about *what is
punctuation*: 0.31 (Markdig 1.x) counts Unicode symbols such as emoji, currency and math signs; 0.30 and the parsers built
on it (markdown-it, cmark-gfm 0.29) count only ASCII punctuation and Unicode `P` categories. Ripium's rules are chosen to be
valid under both:

- an **edge character is fragile** if either edition calls it punctuation (`Rune.IsPunctuation || Rune.IsSymbol`);
- a fragile edge needs a neighbour (outside the span) that is whitespace or punctuation under the **narrower 0.30**
  definition. Anything else *blocks* (`BlocksFlanking`): letters and digits, but also combining marks, zero-width
  joiners and spaces, and non-ASCII symbols and emoji.

So in `Wow!` followed directly by an emoji, the bold cannot end at the `!`: the span shrinks to `Wow` and becomes
`**Wow**!😀`. The shrink repeats with blank trimming until stable, and the span is dropped if no letter or digit remains.
The same predicate drives `DelimitersCanFlank` and the "letter before" ordering rule in `Emit`.

### Delimiter choices

Strong is `**`, emphasis is `*` (not `_`, which does not work inside words). `***text***` results from both.
Code uses a fence one longer than any backtick run in the merged code ranges, padded with one space on each side when
any code range contains a backtick.

## Escaping

`EscapePlain(full, start, end)` escapes a segment but judges *line context* against the whole text, so an indented line
after a hard break is still recognised. Table of what is escaped and why:

| Character / pattern | Rule | Why |
|---|---|---|
| `\` `` ` `` `*` `[` `]` `<` `~` | always | Markdown syntax; `~` also GFM strikethrough |
| `$` | always | `$...$` is math in Obsidian, GitHub and most editors and swallows the text between |
| `_` | unless letters on both sides *within the same segment* | `snake_case` stays readable, but beside a delimiter `_` can open emphasis |
| `&` | only when it looks like an entity: `&name;` with a name of up to 32 letters/digits (HTML5 has 31-character names such as `&CounterClockwiseContourIntegral;`) or `&#169;` | would otherwise be decoded |
| LF, CRLF, lone CR | each is one line break, written as a hard break (backslash + LF); a run collapses to one | keeps line structure; blank lines would split the paragraph; a lone CR must not be deleted (it joined words) |
| `#`, `>` | at line start | heading, quote |
| `-`, `+` | at line start when followed by *any* blank, `-`, `=` or the end of the text | bullet, rule, setext underline, `+---+` grid table |
| `=` | at line start if the rest of the line is only `=`; also when doubled anywhere | setext underline; `==highlight==` |
| `%` | when doubled | Obsidian `%%comment%%` |
| `:` , `\|` | at line start | definition list / `:::` container; table rows |
| `.` or `)` | when it ends a list marker at line start: digits (at most 9), one ASCII letter, or roman numerals up to 4 letters, followed by any blank or the end | `1.`, `b.`, `IV)` lists (Markdig and Pandoc accept letters and romans). Decided at the punctuation so a markup boundary inside the marker cannot hide it |
| `!` | at the end of a segment that is followed by more text | `![` makes an image; the next segment may be a link |

`EscapeText` (titles, alt text, link labels, subtitle) collapses newlines and applies the same rules to a single line.
`EscapeAtxClosingSequence` escapes a trailing ` #` run in heading text.

### URLs

`FormatDestination` makes a destination *literal*, because the Markdown parser decodes things inside a destination **after**
Ripium has checked the scheme: it
- unescapes Medium's backslash-escaped brackets/parentheses and trims a trailing backslash;
- percent-encodes space `(` `)` `<` `>`;
- percent-encodes every remaining backslash (`%5C`) and any `&` that starts an entity-shaped sequence like `&colon;`,
  `&#97;`, `&Tab;` (`%26`). Real URLs are untouched: `&utm_medium=post` has no semicolon, and `%20` stays as it is;
- removes control characters (browsers ignore them inside a scheme, so `java<TAB>script:` would pass a naive check).

Without the second step `javascript\:alert(1)` and `javascript&colon;alert(1)` pass a scheme test written on the raw
string and become `javascript:` links in the parsed output (reproduced with Markdig). `IsSafeUrl` then allows `http`,
`https`, `mailto`, `tel` and scheme-less (relative) URLs; everything else (`javascript:`, `data:`, `file:`, `vbscript:`,
custom app schemes) is refused and the link renders as its text. The same two helpers guard inline links, linked images,
mixtapes and embeds. The frontmatter `url` (author-controlled `canonicalUrl`) and `author_url` are stricter, because a consumer may render it as
a link: it must be an absolute `http`/`https` URL without whitespace, control characters or the characters `<`, `>`, `"`, backtick,
`[`, `]`, backslash, checked in the form that is written; the first of `canonicalUrl` and `mediumUrl` that passes is used.
Other frontmatter strings (title, author, tags) are data, quoted as YAML scalars; they are not validated.

## Known limitations (accepted, with reasons in [decisions.md](decisions.md))

- **Markdig-only syntaxes** are not escaped: `++inserted++`, `^super^`, `^^footer^^`, `""citation""`, inline `::container::`,
  `{#id}` generic attributes, and `[x]` as a link label inside a list item (Markdig's task-list extension reads it as a
  checkbox). Escaping `+`/`^`/`:`/`{` would make every "C++", "std::vector" or code-ish sentence ugly for
  the benefit of one parser.
- **Several overlapping emphasis ranges in one paragraph** can leak delimiters under parsers using the older CommonMark rules.
  The shape is: strong and italic ranges that nest, abut or cross so that a `**` run lands between two punctuation characters
  (it is then both opening and closing, and the "rule of three" in the parsing algorithm pairs it wrongly). It is *not* limited
  to crossing ranges. Measured with markdown-it: single-markup paragraphs 0 failures in 250,000; random adversarial paragraphs
  with up to 5 markups about 4 in 100,000; exhaustive triples of strong/italic at every word edge 0.35% (760 of 215,820);
  adversarial whole documents about 1 in 1,500. Markdig shows none. Fifteen real articles (1,283 paragraphs) show none.
- **Crossing styles** that need a reopen with no blank between them lose the reopened tail's formatting.
- **Emphasis next to code, or ending where an inner link ends, directly before a letter** is dropped (flank check).
- **Plain URLs containing `*`, `_` or `~`** get backslashes; a GFM auto-link then includes them. Medium normally links URLs
  explicitly, and linked text is fine.
- **Editor-specific syntaxes** other than `==`, `%%`, `$` (for example `#tags` in Obsidian) are not specially handled;
  wikilinks `[[ ]]` and footnotes are covered by escaping `[`.
- **Performance**: `Emit` is quadratic in the number of markups of one paragraph (`shortcut:` comment in the code), bounded by
  using at most 2,000 markups per paragraph (the rest keep their text but lose their formatting). 2,000 markups on a 50,000-character
  paragraph take about 0.1-0.2 s; the busiest real paragraph has about 250.
- Highlights, drop caps, image layouts, section layouts, claps and the author bio are fetched or present but not rendered.
- Embeds are links only; no oEmbed/gist expansion is attempted.
- Smart quotes are kept as typed (Freedium converts them to ASCII).
