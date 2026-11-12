# Freedium reference map

`freedium-src/` is a checkout of the Freedium project (Python). It is **not part of the build**, is untracked in git, and
should be treated as read-only reference. It may be absent in a fresh clone; everything Ripium needs from it is
reproduced in these docs. When it is present it is the best place to compare behaviour.

## Two implementations inside it

- `freedium-src/medium-parser/` - the original, used by the production site. Renders **HTML** (Jinja). Its
  `render_as_markdown` is a stub.
- `freedium-src/freedium-library/` - the newer rewrite with a **Markdown** renderer. This is what Ripium ports. Its
  README is stale (claims Markdown is a TODO).

## Where each Ripium piece comes from

| Ripium | Freedium source (under `freedium-library/src/freedium_library/services/medium/` unless noted) |
|---|---|
| `MediumClient` request, headers | `api.py` `query_post_graphql` (lines ~77-175) |
| `GraphQlQuery` | `api.py` line 106 (the long `"query"` string); same text in `medium-parser/medium_parser/api.py:61` |
| `GraphQlRefResolver` | `api.py` `resolve_graphql_references` (lines ~20-58) |
| `MediumUrl` | `validators.py` (`resolve_medium_url`, `_MediumServiceHashesValidator`); tests in `tests/validators_hashes_test.py`, `tests/validators_url_test.py` |
| DTOs | `models.py` (the GraphQL models, lines ~263-512) |
| `MarkdownRenderer` | `renderer.py` (`MediumMarkdownRenderer`: `_process_paragraph`, `_render_*`, title/subtitle skipping around lines 627-670, frontmatter `render_with_frontmatter`) |
| `InlineMarkdown` | `renderer.py` `MarkupProcessor` (lines ~161-361) and `rl_string_helper/` for the original overlap splitter |
| `TextSimilarity` | Python `difflib.SequenceMatcher.ratio()` (used with a threshold of 80 in the renderer) |
| Sample post ids | `tests/api_integration_test.py`, `tests/smokie_tests.py` |

## Freedium features Ripium intentionally does not have

Redis/Postgres/Mongo/SQLite caches, the FastAPI/SvelteKit web apps, proxy pools and Cloudflare WARP, curl_cffi TLS
impersonation, base64-embedded images, iframe HTML fetching (`medium.com/media/<id>`), highlights (`<mark>`), drop
caps, the homepage gallery, Sentry/Telegram, admin cache bypass, sitemap generation.

## Things learned about the reference (do not re-discover)

- Its `get_original_position` UTF-16 converter subtracts `char_length + 1` per astral character (3 instead of 1);
  emoji articles shift markups. Ripium sidesteps it.
- Its renderer has no Markdown escaping at all; text containing `*`, `_`, `[`, `#` renders wrongly.
- Its tests are partly stale (`renderer_test.py` constructs the renderer without a required argument and expects
  `![alt](url)` where the code now emits `<picture>`; `api_integration_test.py` expects a REST model the code no longer has).
- Its `User-Agent` string is unterminated and its `Accept` header repeats `application/json`; both are deliberate.
- The old REST endpoint (`/p/<id>?format=json`) is dead; only GraphQL works.
- There are no JSON fixtures in the repo; `freedium-library/save_graphql_response.py` fetches one.
