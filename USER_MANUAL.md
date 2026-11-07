# 4TRAC 2027 — User Manual

**Retrograde Ballistic Solver — Forensic Shooting Reconstruction**

v1.1 — flat-earth point-mass model, 10 km validated envelope

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
- The solver itself runs offline. Internet access **is** needed for the OpenStreetMap map
  tiles and for the first download of SRTM terrain tiles (AWS open data, then cached
  locally). A local ESRI ASCII DEM can be used instead of SRTM.

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
| Origin plane height relative to impact (m) | Height of the shooter above (+) or below (-) the impact plane. Ignored when a DEM is used |
| Launch search | **Climbing** (default): the shooter is at or below the impact plane, or the shot was lofted. **Descending**: the shooter is above the impact (roof, high-rise, hillside) and fired downward. **Both**: runs the two searches and lists each as a separate candidate zone. Descending needs an origin height greater than 0 |
| Exterior angle (deg below horizontal, +/- sigma) | Measured angle of travel below horizontal at impact |
| Travel azimuth at impact (deg true, +/- sigma) | Direction of travel at impact, clockwise from true north |
| Magnetic declination (deg, east +) | 0 if the azimuth is already true. If it came from a compass, enter the local declination; true azimuth = entered azimuth + declination, and the results carry a note saying so |

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
| Wind uncertainty, per axis (m/s) | Per-axis wind uncertainty (Monte-Carlo draw sigma) |

**Wind aloft (optional second layer).** Under the wind fields, a second row takes a wind speed,
FROM direction and a height above the impact plane (m). Leave the height at 0 for the single
constant wind. With a height entered, the wind you entered applies at and below the impact
plane, the wind aloft applies at that height and higher, and the wind is blended linearly in
between. Both layers share one uncertainty (one wind draw plus a fixed change). A note in the
results states the layers. The layer is ignored in crosswind-only mode.

A **Wind input** selector chooses between two modes:

- **Speed + direction**: the fields above.
- **Crosswind only (unknown wind)**: for scenes with no wind data at ground level or aloft.
  One box takes a crosswind speed. Each hypothesis then draws that crosswind from the LEFT
  or from the RIGHT of the shot path at random, so the zone widens and may show two lobes,
  one each side of the line. Head and tail wind are not modelled in this mode. The result
  carries a "WIND UNKNOWN" note. The mode is not saved in the session file.

Wind is resolved once into the local east-north frame before the run (constant over the
whole trajectory).

### Run

| Field | Meaning |
|-------|---------|
| Monte-Carlo hypotheses | Number of hypotheses (500–100,000; calibrated minimum is 500). Fewer than 500 is refused |
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
- **Export case report (PDF)** — the same report saved as a PDF. It uses Microsoft Edge (or
  Google Chrome) already installed on the PC, run in the background; if neither is found, use
  the HTML report and print it to PDF from any browser.
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
- Wind is constant, or two-layer (ground and aloft) with a linear blend; no fuller wind profile.
- Coriolis enabled on the production path; spin drift (Litz form) available.
- Magnus effect and wind gradients are **not modelled**.
- Descending-launch origins are reported only through the **Descending** / **Both** launch
  search and only for a flat origin plane at the height you enter (see §11).
- Single shot only; no ricochet or multiple-hole reconstruction.
- Interior penetration angles are proxies; exterior angle preferred.
- Magnetic declination: manual input only (east positive; true = entered + declination). No auto-IGRF.
- DEM vertical error propagates into origin range through Δz / tan(θ); reported explicitly.

## 9. Validation status (as of 2026-11-07)

- **V1 forward solver:** cross-validated against 19 published tables (JBM, Exbal, Warner
  Doppler) within the thresholds in PLAN §7. A model-discrepancy band for G1/G7/CD
  representations is recorded in `data/golden/model_discrepancy_band.md`.
- **Region coverage, climbing branch, spin off (flat model):** 6 scenes (origin range 500,
  1000, 2000 m; crosswind 0 and 3 m/s), 40 trials each, 500 hypotheses per trial. Whole-matrix
  coverage: 95 % region 96.8 % (gate 92-98 %), 50 % region 49.1 % (gate 45-55 %). A separate
  truth-drawn calibration (150 trials x 500 hypotheses, flat origin prior, 3-4.5 km annulus)
  gave 98.0 % and 49.3 %.
- **Region coverage, climbing branch, spin ON:** 6 scenes (500, 1000, 2000 m), 106 trials:
  95 % region 96.2 %, 50 % region 49.1 % (both pass).
- **Region coverage, descending branch (handgun, 9 mm 115 gr, spin on, flat plane 30 m and
  100 m above the impact, 100 m and 250 m range, exact priors):** 320 trials, 95 % region
  94.7 %, 50 % region 49.7 %. Median origin error 1.4-5.7 m. The 250 m and 100 m cells
  marginally miss the 5 m median-error budget.
- **Origin accuracy at shallow angles: not demonstrated.** The origin-error criterion could
  not be evaluated for rifle shots because none reached the required 15 degree exterior angle.
  At shallow impact angles, angle uncertainty cannot pin down range; this is an information
  limit, not a solver bug. Published scene-precision requirements (sigma(theta) <= 0.25 deg,
  MV half-width <= 3 m/s, wind sigma <= 0.5 m/s at the 500 m / 30 deg reference scene) come
  from an earlier-model sensitivity study.
- **Steep and near-vertical impacts: out of calibration.** A near-vertical rifle suite
  (impact angle ~88 deg) gave 95 % coverage of 82 % and median errors of 116-507 m. Do not
  rely on regions for impacts steeper than about 50 degrees (the app warns).
- **Not yet validated:** real-DEM terrain; buildings; spin on the descending branch beyond a
  round-trip test; handgun lofted shots; the full plan matrix; field trials with known
  shooter positions.
- An earlier, superseded validation run on a different physics core met the origin-error
  criterion but failed the 50 % coverage gate (59.8 %); it does not describe the current
  product.

## 10. Intended use

4TRAC is designed toward courtroom-grade scrutiny (testable model, documented limitations,
reproducible runs). It has not yet been field-validated and its error rates are known only
for the conditions listed in §9. Treat outputs as investigative decision support and read
the full case report.

## 11. Elevated shooters, steep impacts, and the scene

**Descending branch.** A shooter on a roof, high-rise or hillside above the impact fires
downward, so the bullet is descending at the moment it leaves the origin plane. The
default (Climbing) search cannot represent that shooter. Choose **Descending**, enter the
origin height above the impact, and 4TRAC searches for origins where the plane is crossed
descending in forward time. **Both** runs both searches and lists each as a candidate zone;
the investigator, not the program, decides which is plausible from the scene.

**Assumptions the descending branch makes.** The origin lies on one flat plane at the height
you entered. A real building or slope has many heights; if the scene allows several, run
each. Building and terrain heights are not read from map data yet.

**Steep impacts.** Near the end of a handgun round's range the bullet falls steeply and the
range along the bearing is poorly constrained, while the bearing stays reliable. A 68-80
degree impact can move the origin range by 400-800 m. The app warns at 50 degrees and above,
and again when spin drift is on (spin drift is extrapolated beyond its calibration at long
flight times).

**Unknown wind.** See the crosswind-only mode in §4.
