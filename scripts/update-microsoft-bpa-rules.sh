#!/usr/bin/env bash
# Refreshes rules/microsoft-bpa from the upstream microsoft/Analysis-Services repo and records the commit.
set -euo pipefail
cd "$(dirname "$0")/.."
sha=$(git ls-remote https://github.com/microsoft/Analysis-Services refs/heads/master | cut -f1)
base="https://raw.githubusercontent.com/microsoft/Analysis-Services/$sha"
curl -sSf -o rules/microsoft-bpa/BPARules.json "$base/BestPracticeRules/BPARules.json"
curl -sSf -o rules/microsoft-bpa/LICENSE "$base/LICENSE"
count=$(python3 -c "import json; print(len(json.load(open('rules/microsoft-bpa/BPARules.json', encoding='utf-8-sig'))))")
sed -i.bak -E "s/^\| Commit \|.*/| Commit | \`$sha\` |/; s/^\| Rules \|.*/| Rules | $count |/" rules/microsoft-bpa/SOURCE.md && rm rules/microsoft-bpa/SOURCE.md.bak
echo "Updated to $sha ($count rules). Run 'dotnet test' and review the compatibility report before committing."
