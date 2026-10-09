#!/usr/bin/env python3
"""Summarise .trx test results as markdown (for $GITHUB_STEP_SUMMARY) and annotate failures.

Usage: test-summary.py <title> <dir-or-trx> [<dir-or-trx> ...]
Prints the summary; appends it to $GITHUB_STEP_SUMMARY when set; emits a `::error` annotation per
failed test. Always exits 0 — `dotnet test` itself decides whether the job fails.
"""
import glob
import os
import sys
import xml.etree.ElementTree as ET

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def trx_files(paths):
    for path in paths:
        if os.path.isdir(path):
            yield from sorted(glob.glob(os.path.join(path, "**", "*.trx"), recursive=True))
        elif path.endswith(".trx") and os.path.exists(path):
            yield path


def parse(path):
    root = ET.parse(path).getroot()
    counters = root.find("t:ResultSummary/t:Counters", NS)
    stats = {k: int(counters.get(k, 0)) for k in ("total", "executed", "passed", "failed", "notExecuted")} if counters is not None else {}
    failures = []
    # xUnit reports skipped tests as outcome "NotExecuted" without counting them in <Counters>.
    stats["notExecuted"] = sum(1 for r in root.iterfind("t:Results/t:UnitTestResult", NS) if r.get("outcome") == "NotExecuted")
    for result in root.iterfind("t:Results/t:UnitTestResult", NS):
        if result.get("outcome") != "Failed":
            continue
        message = result.findtext("t:Output/t:ErrorInfo/t:Message", default="", namespaces=NS).strip()
        failures.append((result.get("testName", "?"), message))
    unit_test = root.find("t:TestDefinitions/t:UnitTest", NS)
    assembly = os.path.basename(unit_test.get("storage", "")) if unit_test is not None else ""
    return stats, failures, assembly


def main(args):
    if len(args) < 2:
        sys.exit(__doc__)
    title, paths = args[0], args[1:]
    rows, all_failures = [], []
    for path in trx_files(paths):
        stats, failures, assembly = parse(path)
        name = assembly or os.path.basename(path)
        rows.append((name, stats))
        all_failures += failures

    lines = [f"## {title}", ""]
    if not rows:
        lines.append("No test results found.")
    else:
        lines += ["| Test assembly | Total | Passed | Failed | Skipped |", "|---|---:|---:|---:|---:|"]
        for name, s in rows:
            status = "❌" if s.get("failed") else "✅"
            lines.append(f"| {status} {name} | {s.get('total', 0)} | {s.get('passed', 0)} | {s.get('failed', 0)} | {s.get('notExecuted', 0)} |")
        if all_failures:
            lines += ["", f"### {len(all_failures)} failed test(s)", ""]
            for test, message in all_failures[:50]:
                first = message.splitlines()[0] if message else ""
                lines.append(f"- `{test}` — {first[:300]}")
    summary = "\n".join(lines) + "\n"
    print(summary)
    if out := os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(out, "a", encoding="utf-8") as fh:
            fh.write(summary)
    for test, message in all_failures:
        flat = " ".join(message.split())[:500]
        print(f"::error title=Test failed: {test}::{flat}")


if __name__ == "__main__":
    main(sys.argv[1:])
