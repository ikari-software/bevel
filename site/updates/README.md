# Update feed (static)

Canonical base URL for Velopack (`UPD-01` / `bevel-ym0`):

```
https://bevel.ikari.software/updates
```

Point `updateFeedUrl` at that directory (not at a single JSON file). Velopack
requests `releases.{channel}.json` next to the `.nupkg` assets — see
https://docs.velopack.io/distributing/overview.

Until the first notarized release is uploaded, the channel files ship with an
empty `Assets` list so the URL is live and safe to poll. The app default stays
an empty `updateFeedUrl` (no network) until you opt in.
