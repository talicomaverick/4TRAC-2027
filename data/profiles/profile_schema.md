# 4TRAC Bullet Profile Schema (v1.0) — LEGACY

> **Status:** the running app takes its calibre and bullet data from the calibre database
> (CSV), not from these JSON files. The `BulletProfile` JSON format below is still supported
> by the Core library and its tests, but `data/profiles/` is not read by the UI. Keep or
> remove the sample files as needed.
>
> **Check before reuse:** the sample `.308 168 gr ELD-M` file states `bcLbIn2` 0.462 with
> `referenceStandard` "G7". A G7 BC of 0.462 is not plausible for a 168 gr .308 bullet (typical
> G7 is about 0.2-0.27; 0.46 is a G1-scale value). Verify the BC and its standard against the
> manufacturer's data before using it.

Bullet profiles are JSON documents. They are used by the bullet
profile library (D8) for one-click recall of caliber, mass, ballistic coefficient, and spin-drift
parameters.

## Schema

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `schema` | string | Yes | Schema identifier: `"4trac-bullet-profile/1.0"` |
| `name` | string | Yes | Human-readable profile name (e.g. ".308 Winchester 168 gr ELD-M") |
| `caliberIn` | number | Yes | Bullet caliber in inches |
| `massGr` | number | Yes | Bullet mass in grains |
| `lengthIn` | number | Yes | Bullet length in inches |
| `diameterIn` | number | Yes | Bullet diameter in inches (may equal caliber) |
| `twistIn` | number | Yes | Barrel twist rate in inches per turn |
| `rightHandTwist` | boolean | Yes | `true` for right-hand twist, `false` for left-hand |
| `bcLbIn2` | number | Yes | Ballistic coefficient (G-style, imperial: lb/in², sectional-density / form-factor convention) |
| `bcSigmaFraction` | number | No | BC uncertainty as a fraction (default 0.03 = 3%) |
| `referenceStandard` | string | No | Drag reference: `"G1"` or `"G7"` (default `"G1"`) |
| `notes` | string | No | Free-text provenance and usage notes |

## Example

```json
{
  "schema": "4trac-bullet-profile/1.0",
  "name": ".308 Winchester 168 gr ELD-M",
  "caliberIn": 0.308,
  "massGr": 168,
  "lengthIn": 1.307,
  "diameterIn": 0.308,
  "twistIn": 10.0,
  "rightHandTwist": true,
  "bcLbIn2": 0.462,
  "bcSigmaFraction": 0.03,
  "referenceStandard": "G7",
  "notes": "G7 BC from Hornady ELD-M published data."
}
```

## Rules

- Unknown fields are rejected, not silently ignored (build prompt §3).
- All lengths/masses are in imperial units at the profile boundary; the Core converts to SI internally.
- BC values are G-style (G1 or G7 reference) as declared by `referenceStandard`.
- `BulletProfile.ToJson()` writes the same camelCase keys that `FromJson()` reads, so a profile round-trips.
