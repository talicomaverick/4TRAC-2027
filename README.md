# 4TRAC 2027 — Retrograde Ballistic Solver

4TRAC works out **where a bullet may have been fired from**. You give it the impact point, the angle and direction the bullet was travelling when it hit, the bullet and a muzzle-velocity range, and the weather. It runs the trajectory *backwards* many thousands of times with the unknowns drawn from their stated uncertainty, and draws two regions on a map: the area that contains the firing position with **50 %** probability and the larger one with **95 %**.

It never gives a single "answer". The most likely point is only an annotation inside the regions.

> **Status: research prototype / investigative decision support.** It is not field-validated and it is not proof of where a shot came from. Read [LIMITATIONS.md](LIMITATIONS.md) before relying on any output.

## What it does

- Flat-earth point-mass model with G1/G7 drag, Coriolis, optional Litz spin drift, and a 10 km validated envelope. Origins outside the envelope are rejected, never clamped.
- Monte-Carlo cloud: every hypothesis is an exact draw from the declared priors (muzzle velocity, angle, azimuth, ballistic coefficient, wind). Muzzle velocity is solved by bracketed root-find, so weights are all 1.
- 50 % / 95 % highest-density regions from a Gaussian KDE, drawn on an OpenStreetMap map.
- **Two launch searches:** *Climbing* (shooter at or below the impact plane, or a lofted shot) and *Descending* (shooter on a roof or hillside above the impact firing down). *Both* lists the two as separate candidate zones for the investigator to judge.
- **Wind options:** speed and direction, a crosswind-only mode for scenes with no wind data (applied from the left and the right at random), and an optional second layer of wind aloft.
- Optional terrain: SRTM tiles (downloaded on demand) or a local ESRI ASCII DEM.
- Magnetic declination input, pre-solve warnings, and warnings on the result for steep impacts, spin drift out of range and unknown wind.
- Exports: HTML and PDF case report (every input, assumption, exclusion and calibration record), GeoJSON, CSV, PNG.
- Deterministic: same seed and inputs give bit-identical results on any machine.

## Validation (summary)

| Test | Result |
|---|---|
| Forward solver vs 19 published tables | Within stated thresholds (`data/golden`) |
| Rifle, spin off, 3–4.5 km, 500 hypotheses | 50 % band covers 49.3 %, 95 % band 98.0 % |
| Rifle, spin on, 500–2000 m, 106 trials | 49.1 % / 96.2 % |
| Handgun, descending branch, flat plane, 320 trials | 49.7 % / 94.7 %; median origin error 1.4–5.7 m |
| Near-vertical rifle impacts | **Under-covers** (95 % band covers 82 %); out of calibration |

Coverage was measured with truth drawn from the same priors the solver uses. Origin accuracy at shallow impact angles is **not** demonstrated (an information limit, not just a bug), and there are no field trials yet. The full list of what is and is not validated is in [USER_MANUAL.md](USER_MANUAL.md) §9 and [LIMITATIONS.md](LIMITATIONS.md).

## Requirements

- Windows 10/11, x64.
- To build: the .NET SDK that targets .NET 8 (`net8.0` / `net8.0-windows`). The author builds with SDK 10.
- Internet is needed only for map tiles and the first download of SRTM terrain tiles. The solver itself runs offline.
- PDF export uses Microsoft Edge or Google Chrome already installed on the PC.

## Build, test, run

```bat
:: from the repository root
tools\build-all.cmd          :: runs the unit tests, then publishes the single-file app (log: build-run.log)
tools\run-tests.cmd          :: tests only
tools\publish-app.cmd        :: publish only
tools\run-validation.cmd all 40 500   :: Monte-Carlo validation matrix (suite, trials per cell, hypotheses); slow
```

Or open `4TRAC.sln` in Visual Studio and run `4TRAC.App`. The published app is `dist\...\4TRAC.App.exe` (self-contained, no .NET runtime needed).

## Repository layout

| Path | Contents |
|---|---|
| `src/4TRAC.Core` | Physics, inverse solver, statistics, HDR regions, case report |
| `src/4TRAC.App` | WPF application (MVVM) |
| `src/4TRAC.Validation` | Validation harness and Monte-Carlo matrix runner |
| `tests/4TRAC.Tests` | xUnit tests |
| `data/calibre_database.csv` | Nominal calibre and bullet catalogue used by the app |
| `data/golden` | Reference ballistic tables used to validate the forward solver |
| `tools` | Build, test, publish and validation scripts |
| `PLAN.md`, `4TRAC 2027 Spec.txt`, `docs/` | Design plan, specification, original build brief |

## Documentation

- [USER_MANUAL.md](USER_MANUAL.md) — inputs, outputs, launch searches, wind modes, validation status
- [LIMITATIONS.md](LIMITATIONS.md) — every declared model, solver, input and statistical limit
- [CONTRIBUTING.md](CONTRIBUTING.md) — how to contribute and what a change must show
- [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) — libraries, data and attribution

## Responsible use

4TRAC is intended for forensic and investigative reconstruction of stray-bullet and similar incidents. Its output is a probability region with quantified uncertainty and a list of assumptions. It must not be presented as identifying a person or a firing position without independent evidence.

## License

GNU General Public License v3.0 — see [LICENSE](LICENSE).
