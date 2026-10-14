# Synthetic seat-map fixtures

Generate another new file; existing output files are never overwritten:

```bash
node scripts/generate-seat-chart.mjs 100 samples/demo-100-new.json
node scripts/generate-seat-chart.mjs 2000 samples/demo-2000-new.json
```

Import `samples/seating-chart-2000.json` using preview/confirm only into a dedicated
isolated fixture showtime. These are explicit demo coordinates, not legacy mappings,
owners or prices. Never replace protected or live maps.

Repeatable real-browser/API measurements and their local-environment limits are
recorded in the FINAL-FIX report and repository README, not claimed as production SLA.
