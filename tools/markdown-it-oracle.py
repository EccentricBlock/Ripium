"""
Second-parser oracle for Ripium's inline Markdown output.

The test suite checks rendered Markdown with Markdig (CommonMark 0.31 punctuation rules). Parsers built on CommonMark 0.30
(markdown-it, cmark-gfm 0.29) disagree on a few edge cases, notably emoji and symbols next to emphasis. This script
parses the same cases with markdown-it and compares the visible text with the original.

  RIPIUM_ORACLE_EXPORT=/tmp/cases.jsonl RIPIUM_FUZZ_CASES=60000 dotnet test --filter ExportCasesForExternalOracle
  python3 -I tools/markdown-it-oracle.py /tmp/cases.jsonl        # needs: pip install markdown-it-py

Prints failures per alphabet and one example each. Expect a handful per 100,000 cases, all in paragraphs with several overlapping
strong/italic ranges (a documented limitation); anything else is a bug. Last measured: 8 failures in 200,000 cases (see docs/testing.md).
"""
import json, re, sys, collections, unicodedata
from markdown_it import MarkdownIt
md = MarkdownIt("commonmark")
def collapse(s): return re.sub(r"\s+", " ", s).strip()
def visible(markup):
    out = []
    for token in md.parse(markup):
        if token.type != "inline": continue
        for child in token.children or []:
            if child.type in ("text", "code_inline"): out.append(child.content)
            elif child.type in ("softbreak", "hardbreak"): out.append(" ")
            elif child.type == "image": out.append(child.content)
    return collapse("".join(out))
def expected(text):
    # Ripium replaces control characters (NUL, ESC, C1, form feed ...) other than tab and line breaks with U+FFFD
    return collapse("".join("\ufffd" if unicodedata.category(c) == "Cc" and c not in "\n\r\t" else c for c in text))
def has_visible(text):
    import unicodedata
    return any(not c.isspace() and unicodedata.category(c) not in ("Cf", "Cc") for c in text)
failures = collections.Counter(); total = collections.Counter(); examples = {}
for line in open(sys.argv[1], encoding="utf-8"):
    case = json.loads(line); a = case["alphabet"]; total[a] += 1
    if not has_visible(case["text"]):
        if case["rendered"] != "": failures[a] += 1; examples.setdefault(a, case)
        continue
    if visible(case["rendered"]) != expected(case["text"]):
        failures[a] += 1; examples.setdefault(a, case)
for a in total: print(f"{a:16s} {failures[a]:6d} / {total[a]}")
for a, c in examples.items():
    print("\n--", a, "\n  text:    ", json.dumps(c["text"]), "\n  markups: ", c["markups"], "\n  rendered:", json.dumps(c["rendered"]), "\n  visible: ", json.dumps(visible(c["rendered"])))
