# Medium API notes

Everything here was observed against the live service while building Ripium (November 2026) or read from the Freedium
source. Medium can change any of it without notice. When something breaks, start with
[Troubleshooting](#troubleshooting).

## The request

```http
POST https://medium.com/_/graphql
Content-Type: application/json

{"operationName":"FullPostQuery",
 "variables":{"postId":"<id>","postMeteringOptions":{}},
 "query":"query FullPostQuery($postId: ID!, $postMeteringOptions: PostMeteringOptions) { ... }"}
```

Headers sent by `MediumClient` (copied from Freedium's `api.py`; they imitate Medium's own Android app, and that is
what makes the *full* body come back even for locked posts):

| Header | Value | Note |
|---|---|---|
| `X-APOLLO-OPERATION-ID` | random 64 hex chars | fresh per request |
| `X-APOLLO-OPERATION-NAME` | `FullPostQuery` | |
| `Accept` | `multipart/mixed; deferSpec=20220824, application/json, application/json` | duplicated `application/json` is Freedium's, keep it |
| `Accept-Language` | `en-US` | |
| `X-Obvious-CID` | `android` | the important one |
| `X-Xsrf-Token` | `1` | |
| `X-Client-Date` | Unix milliseconds | |
| `User-Agent` | an iPhone Safari string ending `... (compatible; YandexMobileBot/3.0;` | **unterminated on purpose**; do not "fix" it |
| `Cache-Control` | `public, max-age=-1` | |
| `Cookie` | only if `--cookies` or `MEDIUM_AUTH_COOKIES` is set | `uid` and `sid` for member content |

.NET splits `User-Agent` and `Accept` into several header values when read back (`GetValues` returns more than one),
so tests join them.

### The query document

`src/Ripium/Medium/GraphQlQuery.cs` holds one constant, about 5,200 characters on a single line. It was extracted
verbatim from `freedium-src/freedium-library/src/freedium_library/services/medium/api.py` line 106. It requests
`post(id)` with fragments for the body (`ParagraphData`, `MarkupData`, `ImageMetadataData`, `IframeData`,
`MediaResourceData`, `MixtapeMetadataData`, `SectionData`), creator, collection, tags, highlights, preview content and
`meterPost`. Ripium ignores several fields it receives (highlights, claps, bio, sections, `meterPost`).

To regenerate it from the reference, if `freedium-src/` is present:

```python
import re
line = open("freedium-src/freedium-library/src/freedium_library/services/medium/api.py", encoding="utf-8").read().splitlines()[105]
query = re.match(r'\s*"query":\s*"(.*)",\s*$', line).group(1)
assert '"' not in query and "\\" not in query      # it contains neither, so it can sit in a C# string unescaped
# write: namespace Ripium.Medium; public static class GraphQlQuery { public const string FullPostQuery = "<query>"; }
```

If `freedium-src/` is gone, edit the constant by hand, or capture a current query from the Medium web app's network
traffic (operation `FullPostQuery`).

## Locked posts

There is no paywall-bypass code. A post with `isLocked: true` (for example `27832c8f6644`) returned all 110
paragraphs with no cookies. The Android-app headers plus `postMeteringOptions: {}` are what make Medium return the
body. If Medium starts truncating locked posts, the symptoms would be a short `paragraphs` array and a missing
ending; supplying `uid`/`sid` cookies of a member account is the fallback.

## The response

```jsonc
{ "data": {
    "post": {                         // null when the post does not exist or is not visible
      "id": "...", "title": "...", "mediumUrl": "...", "canonicalUrl": "...",
      "readingTime": 4.48,            // minutes, fractional
      "isLocked": true,
      "firstPublishedAt": 1676317725922, "updatedAt": 1706944392000,   // Unix milliseconds
      "previewContent": { "subtitle": "..." }, "previewImage": { "id": "1*g6_...jpeg" },
      "creator": { "id", "name", "username", "imageId", "bio", ... },
      "collection": { "name", "slug", ... } | null,
      "tags": [ { "displayTitle": "Wasting Time", "normalizedTagSlug": "wasting-time" } ],
      "content": { "bodyModel": { "paragraphs": [ ... ], "sections": [ ... ] } }
    },
    "<Type:id>": { ... }              // normalised entities that "post" may point at with {"__ref": "Type:id"}
  },
  "errors": [ { "message": "..." } ]  // may appear instead of data
}
```

How `MediumClient.ExtractPost` classifies a body:

| Body | Result |
|---|---|
| not JSON, empty, or not an object | `MediumException` ("other than JSON (blocked or rate-limited?)"), retryable |
| JSON without `data` (typically `errors`) | `MediumException("Medium returned an error: ...")`, retryable |
| `data.post == null` | `PostUnavailableException`, final |
| `data.post` is an object, or a ref that resolves to one | `GraphQlRefResolver` expands refs, returns it |
| `data.post` resolves to something that is not an object | `MediumException("... unexpected shape: the post is not an object")` |

HTTP statuses: 200 is parsed as above. 408, 429 and 5xx are retried. Other statuses (403, 404, ...) are
final. A GraphQL `errors` body without `data` is treated as a failed request and retried once, even when the error says
NOT_FOUND (an unavailable post has been observed to come back as `data.post == null`, not as an error). A 403 message mentions that Medium or Cloudflare may be blocking the client.

### `__ref` resolution

Some values (notably `iframe.mediaResource`) arrive as `{"__ref": "MediaResource:abc"}` and the entity lives at
`data["MediaResource:abc"]`. `GraphQlRefResolver` returns a deep copy with those replaced. Safeguards:

- A reference to an entity already being expanded higher on the same branch is left in place (cycles).
- At most 50,000 expansions per call, at most 24 references followed inside one another, at most 1,000,000 JSON nodes copied
  and at most 64 MB of serialised referenced entities expanded; beyond any of these, refs stay unresolved. (Counting nodes
  alone is not enough: a string is one node, and 400 references to a 1 MB string allocated 1.5 GB.) A chain of 20,000 entities used to overflow the stack (uncatchable, exit 134); 300 used to fail inside the
  JSON serializer, whose depth limit is now 512 (`Post.FromJson`).
- A missing target leaves the `{"__ref": ...}` object where it was.

An earlier version only had a depth limit, and a self-referencing entity with two references hung for minutes.

### Duplicate keys and response size

`JsonNode` reports a duplicate property name lazily, at the first indexer access, so `ExtractPost` translates `ArgumentException`,
`InvalidOperationException` and `InvalidCastException` for the whole walk of the tree, not only for `Parse`. The body is read
with a 64 MB limit (`ResponseTooLargeException`, not retried).

### Decoding

The response is read as bytes and decoded as UTF-8 by `MediumClient`: a `Content-Type` with an unknown charset makes
`HttpContent.ReadAsStringAsync` throw `InvalidOperationException`, which is neither retried nor handled.

### Lone surrogates

Medium's text comes from JavaScript, where a string can hold half an emoji (for example a title cut at a fixed
length). JSON represents that as a lone surrogate escape, which .NET cannot turn into a valid string and which
crashed deserialisation. `JsonSanitizer` replaces such escapes with U+FFFD in the raw response text before parsing. It
scans escape pairs so that an escaped backslash followed by the letters of a surrogate escape is not mistaken for one.

## Paragraph types

Observed in the five fixtures and in a dozen live articles:

| `type` | Notes |
|---|---|
| `H3` | usually the article title (repeated at the top of the body) |
| `H2`, `H4` | section headings; `H4` is often the subtitle or a tag at the top; `H1` is handled but not seen |
| `P` | body text; `text` may contain `\n` (a line break inside the paragraph) |
| `IMG` | `metadata.id`, `metadata.alt`, `text` is the caption, `href` set when the image is a link |
| `PRE` | code; consecutive `PRE` paragraphs are one block; `codeBlockMetadata.lang` is usually null |
| `BQ`, `PQ` | block quote, pull quote |
| `ULI`, `OLI` | list items; flat, no nesting |
| `MIXTAPE_EMBED` | link card: see below |
| `IFRAME` | embed (tweet, gist, YouTube): `iframe.mediaResource {id, iframeSrc, title, thumbnailUrl}` |
| `SECTION_CAPTION` | caption-like text (for example a byline); Freedium drops it, Ripium renders it as a paragraph |

Layouts seen on images: `INSET_CENTER`, `OUTSET_CENTER`, `OUTSET_ROW`, `OUTSET_ROW_CONTINUE`, `FULL_WIDTH`. Ripium
ignores layout; a row of images renders as consecutive images.

Unknown types are rendered as plain paragraphs and add a warning.

### Mixtape (link card) layout

`text` is `"Title\nDescription…domain.com"` with three markups: `A` over everything, `STRONG` over the title, `EM` over
the description. When there is no description, `STRONG` and `EM` cover the same range. `mixtapeMetadata.href` holds
the target. Ripium finds the title by the `STRONG` range and the description by an `EM` that starts at or after the
title's end (Freedium used fixed positions `markups[1]` and `markups[2]`, which fails for the no-description case).
The trailing domain text is dropped.

## Markup types

`STRONG`, `EM`, `CODE`, and `A` with `anchorType` `LINK` (uses `href`) or `USER` (uses `userId`, link is
`https://medium.com/u/<userId>`). Other types are ignored. `start`/`end` are offsets into `text`, end exclusive; they may
be null, inverted, out of range or overlapping, and all of that is tolerated.

### Markup offsets

Offsets count **UTF-16 code units**, exactly like a JavaScript string index and a .NET `string` index. An emoji is two
units. Therefore `text[start..end]` is correct as is. Freedium (Python) has to convert because Python indexes by code
point, and its converter had an off-by-three bug for astral characters. The fixtures `d4222bd1a0b3` (emoji) and the
`Fixture_Emoji_BoldSpansEndExactlyAfterTheirEmoji` test pin this behaviour.

An offset can point into the middle of a surrogate pair (observed in fuzzing, plausible in data). `InlineMarkdown`
widens such a range to include the whole pair.

## Post ids and URL shapes

A post id is 8 to 12 hex characters (some are all digits, for example `395390432598`). Accepted inputs, in
`MediumUrl` order:

1. bare id, for example `27832c8f6644`
2. `/p/<id>` on any host
3. `l.facebook.com/l.php?u=<url>`, `webcache.googleusercontent.com/search?q=cache:<url>`, `google.com/url?url=|q=`,
   `12ft.io?q=`, `/m/global-identity-2?redirectUrl=` - unwrapped recursively (depth limit 8)
4. `link.medium.com/<code>` - `GET https://rsci.app.link/<code>` without following redirects, read `Location`
5. any other URL: last path segment, then the part after its last `-`
6. fallback: the last id-shaped token anywhere in the input, after cutting the query string and fragment

There is no domain allow-list: custom domains work because the id is in the slug. The query string is never used for
step 5 because tracking values can look like ids (`?subid1=20241023-0155-4648-992f-1316e4a9d771`).

## Images

`https://miro.medium.com/v2/resize:fit:<width>/<imageId>` where the id looks like `1*g6_rwCXG8HuOBKVI-NM3Qw.jpeg`
`1*abc@2x.png` (retina variant) or `0*ZcNX1tTo4wnM3kqO` (no extension). Ripium accepts only ids matching `^[A-Za-z0-9*._~@-]{1,200}\z` (`@` appears in retina variants such as `1*abc@2x.png`) and without
`..`, so an id can never change the meaning of the URL (`\z`, not `$`: `$` also matches before a trailing newline). The download sends `Accept: image/*` and a Chrome user agent, and accepts only `image/*` or `application/octet-stream`
responses up to 50 MB (an HTML error page with status 200 is not saved as a picture).

## Risks and unknowns

- **Cloudflare / TLS fingerprinting.** Freedium impersonates Chrome's TLS fingerprint (curl_cffi). Ripium does not and
  works with plain `HttpClient` today. If Medium starts blocking it, expect 403 responses or HTML bodies with status
  200. Options, in order of effort: add `--cookies`, route through a proxy, shell out to `curl-impersonate`.
- **`link.medium.com`** has only been exercised with a stub resolver.
- **Rate limits** are unknown; the client makes one request per article plus image downloads.
- **Schema drift.** New paragraph or markup types degrade gracefully (warning / ignored). Renamed or retyped fields
  surface as `MediumException("... unexpected shape ...")`. A new query field that is not requested is simply absent.

## Troubleshooting

| Symptom | Likely cause | First thing to try |
|---|---|---|
| `Medium returned HTTP 403 (... Cloudflare may be blocking this client)` | TLS/UA fingerprint blocked | run with `--cookies`; see Risks |
| `Medium returned something other than JSON` | challenge page returned with status 200 | same |
| `Post is unavailable or does not exist` | wrong id, deleted, or restricted | open the URL in a browser; try with cookies |
| Short article, missing ending | body truncated for a locked post | try with member cookies |
| `unexpected shape` | Medium changed a field type | read the message path, adjust `Models.cs` |
| Hang, then failure after ~30 s | request timeout (30 s per attempt, 2 attempts) | check network/proxy |
