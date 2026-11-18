#!/bin/bash
# Prints progress per wave and the next card for an agent. Status comes from bd, not from this folder.
set -euo pipefail
dir="$(cd "$(dirname "$0")" && pwd)"
tmp="$(mktemp)"; trap 'rm -f "$tmp" "$tmp.st"' EXIT
bd export -o "$tmp" >/dev/null 2>&1
jq -r '[.id, .status] | @tsv' "$tmp" > "$tmp.st"
touch "$dir/parked.txt"
awk -F'|' -v st="$tmp.st" -v pk="$dir/parked.txt" '
BEGIN {
  while ((getline l < st) > 0) { split(l, a, "\t"); s[a[1]] = a[2] }
  while ((getline l < pk) > 0) { split(l, a, "|"); if (a[1] != "") parked[a[1]] = 1 }
}
{
  w = $1; if (!(w in tot)) order[++n] = w
  tot[w]++; all++
  status = (($2 in s) ? s[$2] : "missing")
  if (status == "closed") { done[w]++; alldone++ }
  else if ($3 == "HUMAN" || w == "H") owner++
  else if ($2 in parked) park++
  else if (next_id == "") { next_id = $2; next_line = $2 "  [" w ", size " $4 ", " status "]  " $6 "\n      card: " $5 }
}
END {
  for (i = 1; i <= n; i++) printf "%-3s %3d / %-3d closed\n", order[i], done[order[i]] + 0, tot[order[i]]
  printf "ALL %3d / %-3d closed   (%d owner-only, %d parked in parked.txt)\n", alldone + 0, all, owner + 0, park + 0
  print "Next: " (next_id == "" ? "none" : next_line)
}' "$dir/waves.txt"
