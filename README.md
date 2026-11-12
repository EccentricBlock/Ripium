# Ripium

A .NET console app that exports a Medium article to a Markdown file. It follows the approach of
[Freedium](https://freedium.cfd) that asks Medium's app
GraphQL endpoint for the article, then converts the paragraphs and inline markup to Markdown instead of HTML.

## Usage

```text
ripium - export a Medium article to a Markdown file

Usage:
  ripium <url-or-post-id> [options]

Options:
  -o, --output <path>     Output .md file, or a directory (existing, or ending in /).
                          "-" writes to stdout. A path without an extension gets ".md" added.
                          Default: ./<title-slug>.md
  -f, --force             Overwrite the output file if it already exists (refused by default).
      --cookies <string>  Medium cookies ("uid=...; sid=...") for member-only stories.
                          Default: the MEDIUM_AUTH_COOKIES environment variable. Prefer the variable:
                          command-line arguments are visible to other users in the process list.
      --image-width <n>   Width in pixels to request for images, 50 to 4000 (default 1400).
      --remote-images     Link to images on Medium instead of downloading them.
      --no-frontmatter    Omit the YAML frontmatter (a byline is printed instead).
  -h, --help              Show this help.

Images are saved to <output-name>_assets/ next to the .md file. With "-o -" they stay remote links.

Exit codes: 0 success, 1 the article could not be fetched or written, 2 bad usage,
            70 internal error (a bug), 130 interrupted.
```

```sh
dotnet run --project src/Ripium -- https://medium.com/@someone/some-story-27832c8f6644
dotnet run --project src/Ripium -- 27832c8f6644 -o notes/story.md
dotnet run --project src/Ripium -- 27832c8f6644 -o notes/            # notes/<title-slug>.md
dotnet run --project src/Ripium -- 27832c8f6644 -o - > story.md      # stdout; images stay remote links
```

Accepted inputs: Medium URLs (including custom domains such as `towardsdatascience.com`), `medium.com/p/<id>`,
`link.medium.com` short links, Facebook/Google/12ft redirect wrappers, a bare post id, or a slug ending in the id.

- **Images** are downloaded to `<name>_assets/` next to the file and linked relatively. A failed image keeps its
  remote link and prints a warning; it never aborts the export.
- **Existing files are never overwritten** without `--force`. For an explicit `-o file` the check happens before any
  network access.
- **Exit codes:** `0` success, `1` the article could not be fetched or written, `2` bad usage, `70` internal error,
  `130` interrupted. Diagnostics go to stderr; only Markdown goes to stdout.

## Output

- YAML frontmatter: title, author, publication, subtitle, published/updated, reading time, url, tags, preview image.
- `# Title`, the subtitle in italics, then the body. Medium's H2/H3/H4 map to `##`/`###`/`####`.
- Bold, italic, inline code and links, including overlapping markups, which are nested or reopened as Markdown needs.
- Lists, block quotes, pull quotes, fenced code blocks (language kept), images with captions, link cards (as block
  quotes) and embeds (as links; tweets, gists and videos cannot be inlined).
- Article text is escaped so that it does not turn into Markdown syntax (checked by parsing the output with two Markdown
  parsers; the few known exceptions are listed in [docs/rendering.md](docs/rendering.md)), control characters are replaced,
  and links are restricted to http, https, mailto, tel and relative targets. With `--no-frontmatter`, tags, publication and
  url are not written.

The guiding rule is that the visible text of every paragraph survives; when formatting cannot be written validly it is
dropped rather than printed as stray asterisks. Details and known limitations: [docs/rendering.md](docs/rendering.md).

## Build and test

```sh
dotnet build
dotnet test        # 655 tests, offline, about 6 seconds
```

The tests run against article JSON captured from Medium (`tests/Ripium.Tests/Fixtures/`) and check the output with
real Markdown and YAML parsers (Markdig, YamlDotNet; test-only dependencies). The app has no runtime NuGet
dependencies.

## Documentation

`docs/` is written so that someone who has never seen the code can maintain it: start with
[docs/README.md](docs/README.md). It covers the [architecture](docs/architecture.md), the
[Medium API](docs/medium-api.md), the [rendering rules](docs/rendering.md), the [CLI](docs/cli.md),
[testing](docs/testing.md), a [maintenance runbook](docs/maintenance.md), the [decisions](docs/decisions.md) behind the
design and a [map of the Freedium reference](docs/freedium-reference.md). Project rules for contributors are in
`AGENTS.md`.

## Notes

- Medium's inline markup offsets are UTF-16 code units, which is how .NET indexes strings, so they are used as they
  are (emoji do not shift formatting).
- Fetching uses a plain `HttpClient` with the same headers Freedium sends. If Medium or Cloudflare starts blocking
  that, the client reports the HTTP status; cookies, a proxy or a TLS-impersonating client would then be needed.
- Not verified live: `link.medium.com` short links (tested with a stub only) and non-Medium custom domains.
