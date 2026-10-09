#!/usr/bin/env python3
"""Fail the build when tests were skipped that are expected to run.

Reads one or more Visual Studio .trx result files (or directories searched recursively for *.trx),
compares every skipped test with an allow-list and exits with 1 when

  * a test was skipped that the allow-list does not cover (a new skip: a missing server, binary,
    port clash, ... in CI), or
  * a test failed, or
  * no test results were found at all.

The allow-list has one entry per line: ``<pattern> | <reason>``. The pattern is a shell-style
wildcard (fnmatch, case-sensitive) matched against the full test name as written in the .trx
(``Namespace.Class.Method`` plus ``(arguments)`` for theory cases). Blank lines and lines starting
with ``#`` are ignored. Entries that matched no skipped test are reported as stale (a warning only).

A Markdown summary is printed to stdout and appended to $GITHUB_STEP_SUMMARY when that is set.
Standard library only.
"""

from __future__ import annotations

import argparse
import fnmatch
import os
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field
from pathlib import Path

TRX_NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"


@dataclass
class AllowEntry:
    pattern: str
    reason: str
    line: int
    matches: int = 0


@dataclass
class Results:
    passed: list[str] = field(default_factory=list)
    failed: list[tuple[str, str]] = field(default_factory=list)
    skipped: list[tuple[str, str]] = field(default_factory=list)
    files: list[Path] = field(default_factory=list)


def load_allow_list(path: Path) -> list[AllowEntry]:
    entries: list[AllowEntry] = []
    for number, raw in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        pattern, sep, reason = line.partition("|")
        pattern, reason = pattern.strip(), reason.strip()
        if not sep or not pattern or not reason:
            raise SystemExit(f"{path}:{number}: expected '<pattern> | <reason>', got: {raw!r}")
        entries.append(AllowEntry(pattern, reason, number))
    return entries


def find_trx(inputs: list[str]) -> list[Path]:
    files: list[Path] = []
    for item in inputs:
        path = Path(item)
        if path.is_dir():
            files.extend(sorted(path.rglob("*.trx")))
        elif path.is_file():
            files.append(path)
        else:
            print(f"warning: {item} does not exist", file=sys.stderr)
    return files


def first_line(text: str | None) -> str:
    text = (text or "").strip()
    return text.splitlines()[0].strip() if text else ""


def read_results(files: list[Path]) -> Results:
    results = Results(files=files)
    for trx in files:
        root = ET.parse(trx).getroot()
        for result in root.iter(f"{TRX_NS}UnitTestResult"):
            name = result.get("testName", "?")
            outcome = result.get("outcome", "")
            message = first_line(result.findtext(f"{TRX_NS}Output/{TRX_NS}ErrorInfo/{TRX_NS}Message"))
            if outcome == "Passed":
                results.passed.append(name)
            elif outcome == "NotExecuted":
                results.skipped.append((name, message))
            else:
                results.failed.append((name, message or outcome))
    return results


def allowed_by(name: str, entries: list[AllowEntry]) -> AllowEntry | None:
    for entry in entries:
        if fnmatch.fnmatchcase(name, entry.pattern):
            return entry
    return None


def md_escape(text: str) -> str:
    return text.replace("|", "\\|").replace("\n", " ")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("inputs", nargs="+", help=".trx files or directories containing them")
    parser.add_argument("--allow-list", default=str(Path(__file__).with_name("expected-skips-linux.txt")),
                        help="expected skips (default: %(default)s)")
    parser.add_argument("--title", default="Integration tests (real servers)", help="summary heading")
    args = parser.parse_args()

    entries = load_allow_list(Path(args.allow_list))
    files = find_trx(args.inputs)
    results = read_results(files)

    expected: list[tuple[str, str, AllowEntry]] = []
    unexpected: list[tuple[str, str]] = []
    for name, message in sorted(results.skipped):
        entry = allowed_by(name, entries)
        if entry is None:
            unexpected.append((name, message))
        else:
            entry.matches += 1
            expected.append((name, message, entry))
    stale = [e for e in entries if e.matches == 0]

    total = len(results.passed) + len(results.failed) + len(results.skipped)
    ok = total > 0 and not unexpected and not results.failed

    out: list[str] = [f"## {args.title}", ""]
    if total == 0:
        out += [f"**No test results found** in {', '.join(args.inputs)}.", ""]
    out += [
        "| Result | Count |",
        "|---|---:|",
        f"| Passed | {len(results.passed)} |",
        f"| Failed | {len(results.failed)} |",
        f"| Skipped (expected) | {len(expected)} |",
        f"| Skipped (unexpected) | {len(unexpected)} |",
        f"| Total | {total} |",
        "",
        f"Result files: {', '.join(f'`{f}`' for f in files) or 'none'}",
        "",
    ]
    if results.failed:
        out += ["### Failed tests", "", "| Test | Message |", "|---|---|"]
        out += [f"| `{md_escape(n)}` | {md_escape(m)} |" for n, m in sorted(results.failed)]
        out.append("")
    if unexpected:
        out += [
            "### Unexpected skips",
            "",
            "These tests were expected to run on Linux CI. Fix the environment (ci/linux-integration-setup.sh) "
            f"or, if the skip is legitimate, add a pattern with its reason to `{args.allow_list}`.",
            "",
            "| Test | Skip reason |",
            "|---|---|",
        ]
        out += [f"| `{md_escape(n)}` | {md_escape(m)} |" for n, m in unexpected]
        out.append("")
    if expected:
        out += ["### Expected skips", "", "| Test | Skip reason | Allowed because |", "|---|---|---|"]
        out += [f"| `{md_escape(n)}` | {md_escape(m)} | {md_escape(e.reason)} |" for n, m, e in expected]
        out.append("")
    if stale:
        out += ["### Allow-list entries that matched nothing", ""]
        out += [f"- line {e.line}: `{md_escape(e.pattern)}` ({md_escape(e.reason)})" for e in stale]
        out += ["", "These tests now run (or were renamed); consider removing the entries.", ""]
    out.append("**Result: OK**" if ok else "**Result: FAILED**")
    report = "\n".join(out) + "\n"

    sys.stdout.write(report)
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as handle:
            handle.write(report)
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
