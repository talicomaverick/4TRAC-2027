# 4TRAC Bullet Profile Schema (v1.0)

Bullet profiles are JSON documents stored in `data/profiles/`. They are used by the bullet
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
| `bcLbIn2` | number | Yes | Ballistic coefficient in lb/in² |
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
- Profiles are read-only at runtime; new profiles are added by placing a JSON file in `data/profiles/`.
