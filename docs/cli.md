# Command-line interface

Code: `Cli/CommandLine.cs` (parsing, usage text), `Program.cs` (orchestration), `Output/*`. The usage text in
`CommandLine.Usage` is the single source of truth for the option list; keep it, `README.md` and this file in step
(there is a test that every option appears in the usage text).

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

## Parsing rules

- Exactly one positional argument (the URL or id). A second one is an error.
- Value options (`-o`, `--cookies`, `--image-width`) take the next argument. A value that looks like another option
  (starts with `-`, longer than one character) is rejected: `-o --remote-images` is an error, not a file named
  `--remote-images`. The one exception is `-o -` (stdout).
- Giving a value option twice is an error. An empty or whitespace-only `-o` value is an error too (it used to be silently
  ignored or written ` .md`). `--cookies` and `MEDIUM_AUTH_COOKIES` must be printable ASCII without control characters or line breaks (the value goes into an HTTP
  header); a bad value exits 2.
- `--image-width` must parse as an integer from 50 to 4000.
- Unknown options are errors. `-h`/`--help` prints the usage and exits 0 (a URL is not needed), provided the rest of the
  command line parses: `-h --bogus` is a usage error, because arguments are validated before the help flag is acted on.

## Exit codes and streams

| Code | Meaning |
|---|---|
| 0 | success |
| 1 | expected failure: bad URL, network/Medium error, post unavailable, file exists, I/O error |
| 2 | usage error (usage text follows the message on stderr) |
| 70 | unexpected exception, i.e. a bug (message says so) |
| 130 | interrupted with Ctrl-C |

- **stdout** carries only the Markdown, and only with `-o -`. (`--help` prints its text to stdout.)
- **stderr** carries progress (`Fetching post ...`, `Downloading N image(s) ...`, `Wrote <path>`), `warning:` lines
  (unsupported paragraph types, skipped images, failed downloads) and `error:` lines.
- With `-o -`, images are never downloaded (there is nowhere to put them) and stay remote links.

## Output path rules (`OutputPaths`)

| `-o` value | File written |
|---|---|
| (absent) | `./<slug>.md` in the current directory |
| existing directory, or ending in `/` | `<dir>/<slug>.md`; the directory is created if needed |
| `name.ext` (has an extension) | exactly that path |
| `name` or `name.` (no extension) | `name.md` |
| `-` | stdout |

`<slug>` is the title slug (`FileNames.Slugify`): letters and digits of any script, lower-cased, other characters
collapsed to single `-`, Latin accents removed, at most 150 UTF-8 bytes, never splits a surrogate pair; falls back to the
post id when nothing remains. Parent directories are created.

**Existing files are never overwritten without `--force`.** For an explicit file path the check happens *before any
network access*; for a default or directory-derived name it happens right after rendering and before images are
downloaded. An existing path that is a directory where the file should go is also an error. The final write goes to a
temporary file next to the target and is *moved* into place, and the move fails if the target exists, so a file created
while the article was downloading (a race the early check cannot see) is still protected.

## What `--no-frontmatter` leaves out

The frontmatter carries author, publication, dates, reading time, canonical url, tags and the preview image. Without it only
the title, the subtitle and a byline (author, date, reading time) are written; **tags, publication and url are not**.

## Files it writes

- `<name>.md`: UTF-8, no BOM, LF line endings. It is written to a short temporary file beside it
  (`.ripium-<random>.tmp`) and moved into place, so a failure never leaves a half-written file. Names up to 255 bytes work for the
  `.md`; the assets folder name is shortened when needed (`OutputPaths.AssetsFolderName`) because `_assets` adds seven bytes.
- Control characters in article text (NUL, ESC, C1 ...) are replaced by U+FFFD in the document body and in the frontmatter
  values. Everything printed to stderr that came from a server, an article or the command line goes through `Messages.OneLine`:
  control characters *and line breaks* become spaces or U+FFFD and the text is cut at 500 characters, so a hostile string can
  neither drive the terminal nor forge a line such as `Wrote /etc/passwd`.
- `--force` replaces the file by moving a temporary file over it: the new file gets default permissions, hard links keep the
  old content, and a symlink is replaced rather than followed.
- `<name>_assets/NN-<sanitised-id>.<ext>` for images, created on the first successful download. `NN` is the image's
  1-based position in the document's image list. `<sanitised-id>` keeps only `A-Za-z0-9._-`; so a file name can never
  contain a path separator or `..` segments. The extension comes from the id, or from the response content type when
  the id has none (`.img` if unknown).
- During a download a `.part` file is written and renamed when complete; failures delete it.
- **An existing file in the assets folder is never replaced** (without `--force`): the new image gets the next free name
  (`01-1-abc-2.jpg`) and the document links to that. With `--force` the old file is replaced.

## Images

- Default is to download. `--remote-images` and `-o -` keep remote links.
- Up to 4 downloads run in parallel, each with a 60 s limit for the *whole* image, headers and body. (`HttpClient.Timeout`
  alone only covers the headers when a body is streamed, so a server sending one byte per second would have held a
  download forever.) A response must be `image/*` or
  `application/octet-stream` and at most 50 MB (declared or actual); an HTML error page or an oversized stream counts as
  a failed download.
- **One bad image never aborts the export.** A failure for any reason (404, timeout, dropped connection, file system
  error) prints a `warning:` and that image keeps its remote link. Only Ctrl-C stops the batch.
- After downloading, `ImageDownloader.RewriteLinks` replaces `](remoteUrl)` link targets and the frontmatter's
  `preview_image: "remoteUrl"` with the relative path `<name>_assets/<file>` (each path segment percent-encoded).
  URLs that merely appear in prose are untouched.

## Environment

| Variable | Use |
|---|---|
| `MEDIUM_AUTH_COOKIES` | default for `--cookies`, validated the same way. Prefer it over the flag: command-line arguments are visible to other users in the process list. |
| `https_proxy` / `HTTPS_PROXY` etc. | honoured by .NET's `HttpClient`; the process-level tests use a dead proxy to simulate network failure |
| `RIPIUM_FUZZ_CASES` | test-only: size of the fuzz run |

## Adding or changing an option

1. `CommandLineOptions` record, `CommandLine.Parse`, and `CommandLine.Usage`.
2. Use it in `Program.RunAsync`.
3. `CommandLineTests` (parsing, bad values, interaction with other options) and, if behaviour is observable from
   outside, a `CliProcessTests` case.
4. Update this file and `README.md`.
