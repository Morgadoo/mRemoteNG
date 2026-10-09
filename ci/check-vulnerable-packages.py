#!/usr/bin/env python3
"""Fail the build when a project depends (directly or transitively) on a package with a known
High or Critical vulnerability. Lower severities are reported as warnings.

Usage: check-vulnerable-packages.py <project.csproj> [<project.csproj> ...]
Runs `dotnet list <project> package --vulnerable --include-transitive --format json` for each
project. Writes a markdown table to $GITHUB_STEP_SUMMARY when it is set.
"""
import json
import os
import subprocess
import sys

BLOCKING = {"high", "critical"}


def vulnerabilities(project):
    result = subprocess.run(
        ["dotnet", "list", project, "package", "--vulnerable", "--include-transitive", "--format", "json"],
        capture_output=True, text=True, check=False)
    if result.returncode != 0:
        sys.exit(f"dotnet list failed for {project}:\n{result.stdout}\n{result.stderr}")
    report = json.loads(result.stdout)
    for proj in report.get("projects", []):
        for framework in proj.get("frameworks", []):
            for kind in ("topLevelPackages", "transitivePackages"):
                for package in framework.get(kind, []):
                    for vuln in package.get("vulnerabilities", []):
                        yield {
                            "project": os.path.basename(proj.get("path", project)),
                            "package": package.get("id"),
                            "version": package.get("resolvedVersion"),
                            "transitive": kind == "transitivePackages",
                            "severity": vuln.get("severity", "unknown"),
                            "advisory": vuln.get("advisoryurl", ""),
                        }


def main(projects):
    if not projects:
        sys.exit(__doc__)
    found = [v for p in projects for v in vulnerabilities(p)]
    blocking = [v for v in found if v["severity"].lower() in BLOCKING]

    lines = ["## Dependency vulnerabilities", ""]
    if not found:
        lines.append(f"No known vulnerable packages in {len(projects)} project(s).")
    else:
        lines += ["| Severity | Package | Version | Via | Project | Advisory |",
                  "|---|---|---|---|---|---|"]
        for v in sorted(found, key=lambda v: (v["severity"].lower() not in BLOCKING, v["package"])):
            via = "transitive" if v["transitive"] else "direct"
            lines.append(f"| {v['severity']} | {v['package']} | {v['version']} | {via} | {v['project']} | {v['advisory']} |")
    summary = "\n".join(lines) + "\n"
    print(summary)
    if path := os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(path, "a", encoding="utf-8") as fh:
            fh.write(summary)

    for v in found:
        level = "error" if v in blocking else "warning"
        print(f"::{level}::{v['severity']} vulnerability in {v['package']} {v['version']} ({v['project']}): {v['advisory']}")
    if blocking:
        sys.exit(f"{len(blocking)} High/Critical vulnerable package(s). Upgrade or pin a fixed version in Directory.Packages.props.")


if __name__ == "__main__":
    main(sys.argv[1:])
