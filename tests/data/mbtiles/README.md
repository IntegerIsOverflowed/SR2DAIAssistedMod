# MBTiles test data

Small slices of the Chelyabinsk map (OpenMapTiles schema, made with tilemaker from OpenStreetMap data,
© OpenStreetMap contributors, ODbL). Used by `tests/cs/ctlrun/MbTilesChecks.cs`.

| File | What it tests |
|---|---|
| `chel-z0-9.mbtiles` | 16 vector tiles, zoom 0–9. UTF-16 text in the SQLite file, 65536-byte pages (the tilemaker default). |
| `chel-z11-smallpages.mbtiles` | 20 vector tiles, zoom 11. UTF-8 text, 1024-byte pages: multi-level B-tree and overflow pages. |

The expected digests in the checks come from Python's `sqlite3`: SHA-256 over every tile in (z, x, y) order,
each written as `z/x/y/length` + newline + the bytes after gunzip.

The full file (`chelyabinsk.mbtiles`, 17.9 MB, 2597 tiles) is not stored here. Run the checks with
`MBTILES_FILE=/path/to/chelyabinsk.mbtiles` to include it.
