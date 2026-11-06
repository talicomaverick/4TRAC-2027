# 4TRAC 2027 — User Manual

**Retrograde Ballistic Solver — Forensic Shooting Reconstruction**

v1.0 — flat-earth point-mass model, 10 km validated envelope

---

## 1. What 4TRAC Does

Given a bullet impact point (where a stray bullet struck a surface), the impact geometry
(angle and direction of travel), the recovered bullet, a muzzle-velocity range, and the
weather at the scene, 4TRAC runs the trajectory **backwards** and produces a **probability
region** on a map — the area that contains the firing position with 50 % and 95 %
probability.

4TRAC never presents a single point as "the answer." The primary output is a pair of
highest-density-region (HDR) polygons (50 %, 95 %). A most-likely origin point is shown
only as an annotation inside them.

## 2. System Requirements

- Windows 10 or 11, x64
- No .NET runtime required (the published executable is self-contained)
- Internet access is **not** required for normal operation. SRTM terrain tiles are
  downloaded from AWS open data on demand and cached locally.

## 3. Running the Application

Launch `4TRAC.App.exe`. The window has two halves:

- **Left panel** — input forms: Scene, Bullet, Muzzle velocity, Atmosphere, Wind, Run.
- **Right panel** — map (top) and results/deviations text (bottom).

Enter the case data and press **Solve**. The solver runs a Monte-Carlo analysis and
displays:
- The 50 % and 95 % origin regions as polygons on the map.
- The most likely origin (lat/lon, range, bearing) as an annotation only.
- Implied muzzle velocity and origin range percentiles.
- A deviations-and-assumptions panel listing every assumption, exclusion, and deviation.

## 4. Input Panels

### Scene (impact point)

| Field | Meaning |
|-------|---------|
| Impact latitude / longitude (deg) | GPS of the exterior (roof) hole, WGS84 decimal degrees |
| DEM file | Optional ESRI ASCII grid (WGS84 degrees). Blank = flat origin plane |
| Use SRTM terrain | Automatically download and cache SRTM 1° tiles covering the envelope |
| Impact elevation (m) | Elevation of the impact point; anchors the atmosphere |
| Origin plane height relative to impact (m) | Height of the shooter above/below the impact plane |
| Exterior angle (deg below horizontal, +/- sigma) | Measured angle of travel below horizontal at impact |
| Travel azimuth at impact (deg true, +/- sigma) | Direction of travel at impact, clockwise from true north |

### Bullet

| Field | Meaning |
|-------|---------|
| Bullet profile | Recall a saved bullet profile (.308 168 gr ELD-M, .223 55 gr FMJ, .30-06 150 gr Core-Lokt) |
| Ballistic coefficient (lb/in²) | G1 or G7 ballistic coefficient |
| Sigma (%) | BC uncertainty as a percentage |
| Reference drag function | G1 or G7 |
| Bullet diameter (in) / mass (gr) | Used for spin drift (Miller/Litz) |
| Barrel twist (in per turn) | Rifling twist rate |
| Bullet length (in) | Used for Miller stability factor |
| Spin drift on | Enable Litz-form spin drift (declared, not an EOM force) |
| Right-hand twist | Twist direction |

### Muzzle velocity range (fps)

Uniform prior on muzzle velocity, min and max in feet per second. This is a **prior**,
not a free parameter: the solver root-finds the muzzle velocity consistent with each
drawn impact speed.

### Atmosphere

| Field | Meaning |
|-------|---------|
| Temperature (deg F) | Station temperature |
| Station pressure (inHg) | Station pressure |
| Relative humidity (%) | Relative humidity |

Moist-air density is computed from temperature, pressure, and humidity.

### Wind (constant, FROM direction)

| Field | Meaning |
|-------|---------|
| Speed (mph) | Wind speed at the scene |
| FROM direction (deg true) | Direction the wind blows **from**, clockwise from true north |
| Wind uncertainty, per axis (m/s) | Per-axis wind uncertainty (monte-carlo draw sigma) |

Wind is resolved once into the ECEF frame before the run.

### Run

| Field | Meaning |
|-------|---------|
| Monte-Carlo hypotheses | Number of hypotheses (500–100,000; calibrated minimum is 500) |
| RNG seed | Deterministic seed; same seed = bit-identical output |
| Coriolis on | Enable Coriolis deflection |

## 5. Outputs

### Map

- Impact marker (red) at the measured impact point.
- Most likely origin marker (blue) — annotation only, inside the regions.
- 50 % region (darker fill) and 95 % region (lighter fill) polygons.
- North is up in the map projection.

### Results panel

- Most likely origin: lat/lon, range from impact, bearing, E/N offset.
- Region areas (m²) for 50 % and 95 % bands.
- Implied muzzle velocity P5/P50/P95 (fps).
- Origin range P5/P50/P95 (m).
- KDE cell size and bandwidth (m).
- Deviations and assumptions — every itemized assumption and exclusion.

### Exports (buttons)

- **Export GeoJSON** — regions and points as GeoJSON to the desktop.
- **Export CSV** — origin cloud (east, north, lat, lon, muzzle fps, range) to the desktop.
- **Export PNG** — map image to the desktop.
- **Export case report (HTML)** — self-contained HTML audit trail: every input, the
  declared model and envelope, exclusions and deviations, calibration record, precision
  requirements, and limitations.

## 6. Determinism

4TRAC uses an in-house xoshiro256\*\* PRNG. The seed is printed in every report. The
same seed plus the same inputs produces bit-identical output on any machine and any core
count. Parallel hypothesis solving does not affect results because draws are made
sequentially from the seed and solved in parallel.

## 7. Labels and SKIPs

4TRAC follows the principle **"label, never smooth over"**:

- `LOW-MACH`: muzzle velocity below 400 fps (121.92 m/s). These hypotheses validate the
  solver, not the bullet model.
- Origins beyond the 10 km validated envelope are **rejected, never clamped**.
- If more than 5 % of hypotheses leave DEM coverage, no region is reported.
- `SKIP` means a criterion could not be evaluated; it is not a PASS or FAIL.

## 8. Declared Limitations (v1)

- Flat-earth point-mass model; validated envelope is 10 km horizontal range.
- Gravity: constant 9.80665 m/s².
- Drag: scalar G1/G7 ballistic coefficient only (BC(v) and CD(Mach) not wired in v1).
- Constant wind; no layered wind profile.
- Coriolis enabled on the production path; spin drift (Litz form) available.
- Magnus effect and wind gradients are **not modelled**.
- Descending-launch origins are not reported (only ascending/lofted solutions).
- Single shot only; no ricochet or multiple-hole reconstruction.
- Interior penetration angles are proxies; exterior angle preferred.
- Magnetic declination: manual input only (no auto-IGRF).
- DEM vertical error propagates into origin range through Δz / tan(θ); reported explicitly.

## 9. Validation

- V1 golden tables: forward trajectories cross-validated against JBM, Exbal, and Warner
  Doppler data within stated tolerances.
- V2 Monte Carlo matrix: 95 % region coverage 96.8 % (gate 92–98 %), 50 % coverage 49.1 %
  (gate 45–55 %), origin error within budget on eligible cells.
- Coverage is calibrated by drawing truth from the same priors the inverse uses.

## 10. Support

This is a forensic tool built to survive Daubert-type scrutiny: testable model, published
validation, known error rates, documented limitations. Every result carries an itemized
account of assumptions and exclusions. Read the case report before relying on any output.
