#!/usr/bin/env python3
"""One-shot cleanup of redundant completed Quality Gate push runs.

Requires GITHUB_TOKEN/GH_TOKEN with Actions write; never touches package builds,
PR checks, running jobs, or the cleanup workflow's own history.
"""
import collections
import json
import os
import sys
import time
import urllib.error
import urllib.request

TOKEN = os.environ.get("GH_TOKEN", "")
REPO = os.environ.get("GH_REPOSITORY", "")
CURRENT = int(os.environ.get("CURRENT_RUN_ID", "0"))
if not TOKEN or REPO != "doanythingK/FaceShield_" or CURRENT <= 0:
    sys.exit("Cleanup requires the expected repository, current run ID and Actions token")
BASE = f"https://api.github.com/repos/{REPO}"
HEADERS = {
    "Accept": "application/vnd.github+json",
    "Authorization": f"Bearer {TOKEN}",
    "X-GitHub-Api-Version": "2022-11-28",
    "User-Agent": "FaceShield-quality-gate-pruner",
}


def request(method, path):
    req = urllib.request.Request(BASE + path, method=method, headers=HEADERS)
    with urllib.request.urlopen(req, timeout=35) as resp:
        body = resp.read()
        return json.loads(body) if body else None


runs = []
for page in range(1, 31):
    result = request("GET", f"/actions/workflows/quality-gate.yml/runs?per_page=100&page={page}")
    batch = result.get("workflow_runs", [])
    runs.extend(batch)
    if len(batch) < 100:
        break
else:
    sys.exit("More than 3,000 Quality Gate runs: refusing incomplete cleanup selection")

# Preserve all PR checks, pending/running runs and non-push triggers.
completed = collections.defaultdict(list)
for run in runs:
    if (run.get("event") != "push" or run.get("status") != "completed"
            or not run.get("head_branch") or run.get("id") == CURRENT):
        continue
    completed[run["head_branch"]].append(run)

keep = set()
for branch, branch_runs in completed.items():
    branch_runs.sort(key=lambda run: (run.get("created_at", ""), run["id"]), reverse=True)
    # Retain the most recent checks for each branch, with more main history.
    count = 10 if branch == "main" else 3
    keep.update(run["id"] for run in branch_runs[:count])
    # Also retain the most recent non-success result as troubleshooting evidence.
    for run in branch_runs:
        if run.get("conclusion") != "success":
            keep.add(run["id"])
            break

candidates = sorted(
    (run for branch_runs in completed.values() for run in branch_runs
     if run["id"] not in keep),
    key=lambda run: (run.get("created_at", ""), run["id"]),
)
print(f"Quality Gate runs found={len(runs)}; completed push candidates={len(candidates)}; "
      f"preserved={len(keep)}; branches={len(completed)}", flush=True)
failed = []
deleted = 0
for run in candidates:
    run_id = run["id"]
    try:
        request("DELETE", f"/actions/runs/{run_id}")
        deleted += 1
        print(f"deleted {run_id} ({run['head_branch']}, {run.get('conclusion')})", flush=True)
    except urllib.error.HTTPError as exc:
        failed.append(run_id)
        print(f"FAILED deleting {run_id}: HTTP {exc.code}", flush=True)
        if exc.code in (401, 403):
            break
    except Exception as exc:
        failed.append(run_id)
        print(f"FAILED deleting {run_id}: {type(exc).__name__}", flush=True)
    time.sleep(0.15)
print(f"FINAL deleted={deleted} failed={len(failed)} kept={len(keep)}", flush=True)
if failed:
    sys.exit(1)
