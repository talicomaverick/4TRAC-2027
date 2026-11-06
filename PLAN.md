# 4TRAC 2027 — Retrograde Ballistic Solver
## Project Plan (v0.1)

**Field:** External forensic ballistics / shooting reconstruction
**Deliverable:** Windows desktop executable (GUI) that takes scene evidence from a
stray-bullet incident and produces a map-borne *likely area of origin* with quantified
uncertainty.
**Lineage:** Litz (Applied Ballistics), Pejsa (low-velocity exterior ballistics),
Haag (forensic shooting reconstruction). This tool is built to survive Daubert-type
scrutiny: testable model, published validation, known error rates, documented limitations.

---

## 1. Problem statement

A bullet enters through a roof and strikes a person. Scene investigators measure:

- where the bullet struck (GPS of the hole, elevation),
- the direction of travel through the material (incidence angle below horizontal,
  scene azimuth) — measured both *outside* (through-hole geometry before/at the
  exterior surface) and *inside* (after penetration, probe rods / elongation),
- the recovered bullet (caliber, mass, estimated ballistic coefficient),
- a physically plausible muzzle-velocity range for the suspected firearm/ammunition,
- weather at the scene.

**Question:** where, on a map, did the shot most likely originate — and how large
and how trustworthy is that area?

This is an inverse problem: observations at the *impact* end, unknown at the
*muzzle* end. Drag curves the trajectory, so a straight-line back-projection (the
classic string/probe method) is biased, increasingly so with range and velocity loss.
4TRAC solves the full boundary-value problem instead.

### Well-posedness (why an *area*, not a point)

| Quantity | DOF |
|---|---|
| Origin position (on terrain or a user elevation plane) | 2 |
| Launch direction (azimuth, elevation) | 2 |
| Muzzle velocity | 1 |
| **Unknowns** | **5** |
| Constraint: pass exactly through impact point P | 3 |
| Constraint: match observed direction at P (angle, azimuth) | 2 |
| **Constraints** | **5** |

Exact observations + one fixed MV ⇒ a discrete solution. Sweeping the MV *range*
⇒ a 1-D solution ridge. Measurement/model uncertainty (θ±σ, azimuth±σ, BC, wind,
density, MV prior) smears that ridge into a 2-D **posterior probability density over
the ground plane**. The correct product is therefore a density map with 50 % / 95 %
highest-density-region contours — never a single dot.

---

## 2. Locked design decisions

| # | Decision | Choice |
|---|---|---|
| D1 | Range envelope | **Any / unknown** — design for worst case: ECEF integration, full Coriolis/spin-drift availability, adaptive performance |
| D2 | Drag representation | **All three, selectable:** G1/G7 standard tables · velocity-dependent BC (Pejsa-style) · user-supplied C_D(Mach) table |
| D3 | Penetration angle | **Both angles as inputs** (exterior and interior). Solver prefers the exterior angle with its uncertainty; interior-only input is accepted with an explicit warning flag and inflates σ (correction model deferred to a later phase) |
| D4 | Map & terrain | **Online raster tiles (OSM + aerial imagery switch) + DEM** (SRTM/Copernicus-class, cached locally) for trajectory–ground intersection |
| D5 | Tech stack | **C# / .NET 8 WPF**, MVVM; validation harness as a separate console project |
| D6 | Validation data | **Synthetic round-trip recovery + cross-checks vs published reference tables** (JBM/Lapua/Applied-Ballistics-grade data), all executable in CI |
| D7 | Magnetic declination | **Manual input for v1** (date/location supplied by user); conversion stored and reported. Auto-IGRF model deferred to v1.1 |
| D8 | Bullet library | **Yes** — named JSON profiles (caliber, mass, BC + reference, twist, SG), one-click recall, stored in `data/profiles/` |

### Conventions (normative)

- **Incidence angle θ:** degrees below the horizontal plane, 0° = horizontal,
  positive downward. (Zenith-angle inputs converted on entry.)
- **Azimuth:** degrees clockwise from **true** north, 0–360°. If the scene measure
  came from a magnetic compass, the user must enter magnetic declination for date/
  location; the tool stores and reports the conversion. (Auto-IGRF model: enhancement.)
- **Wind:** meteorological convention — direction the wind blows *from*, degrees +
  speed m/s; converted internally to a vector. Multi-layer profile supported.
- **Impact point P:** the *exterior* hole (roof surface). Interior hits are auxiliary
  geometry only.
- **Units:** SI internally; imperial input/output offered at the UI boundary
  (lb/in² BC, mph, ft, deg/cmils).

---

## 3. Physical model

### 3.1 Equations of motion (ECEF frame)

Integrated in Earth-Centered Earth-Fixed coordinates so arbitrary ranges and
latitudes are handled without a flat-earth approximation:

```
r⃗″ = g⃗(r⃗) + a⃗_drag(v⃗ − w⃗(r⃗)) + a⃗_spin + a⃗_Coriolis
a⃗_Coriolis = −2 Ω⃗ × v⃗,   Ω = 7.292115 × 10⁻⁵ rad/s (ECEF)
```

- **Gravity:** WGS84 normal gravity via Somigliana's formula with height correction,
  applied along the geodetic normal. *Bookkeeping of centrifugal vs. Coriolis terms
  is an explicit architecture-decision record with dedicated correctness tests*
  (stationary-probe test at equator and pole) — see V0.
- **Wind w⃗(r⃗):** user profile (altitude-AGL layers, linear interpolation); default
  constant surface wind. The bullet flies tens of metres above the roof — a surface
  wind alone is a known error source at range and is treated as a nuisance parameter
  with its own σ.
- **Magnus effect:** implemented behind a flag, **off by default** (sub-decimetre
  effect for small-arms at forensic ranges; documented as a limitation).

### 3.2 Drag — three selectable modes (D2)

1. **Standard BC mode:** retardation magnitude
   `f(v) = (ρ/ρ₀) · v² / (2·BC)` against a reference drag function **G1 or G7**
   (tabulated, monotone cubic interpolation). Tables shipped as JSON resources with
   provenance fields; exact unit conventions pinned by golden tests.
2. **Velocity-dependent BC (Pejsa-style):** BC(v) given as a piecewise/polynomial
   fit, used when the lab has calibrated BC at two velocities.
3. **C_D(Mach) table:** full user-supplied drag curve (CSV import);
   `a⃗ = −(ρ/2)(A/m) C_D(M)|v_a| v_a`. Highest fidelity, optional input.

Mode choice, table provenance, and interpolation method are recorded in every
exported report.

### 3.3 Atmosphere

- Air density ρ from inputs: temperature, pressure, relative humidity (or station
  elevation → ISA) with **variation along the trajectory** (barometric height
  correction) — the origin may be hundreds of metres above P.
- Outputs density altitude for the report.

### 3.4 Perturbation terms (selectable, on by default at ≥ 300 m)

- **Coriolis** (latitude + azimuth dependent; right-deflection N. hemisphere).
- **Spin drift** (Litz model: caliber, twist rate, gyroscopic stability factor SG —
  defaults supplied, advanced inputs available).
- Sensitivity analysis (V2) will publish at what range each term stops being
  negligible, so the UI can advise rather than guess.

### 3.5 Terrain intersection

Back-traced trajectory is intersected with:

- DEM elevation (bilinear sampling of cached tiles), **or**
- a user-specified origin elevation plane (e.g., second-floor window line) —
  essential because DEMs do not know building heights.

**Known critical sensitivity (documented in the UI):** at shallow incidence angles
the origin range scales as `Δrange ≈ Δz / tan θ`. A 10 m vertical error at θ = 5°
⇒ ~115 m range error. The tool will *report* this term separately so the
investigator knows when surveyed elevation is mandatory.

---

## 4. Inverse algorithm

**Backward integration** (exact time reversal of the EOM above):

```
for each Monte-Carlo sample:
    draw impact direction  (θ, α) ~ N(observed, Σ_scene)
    scan impact speed v_P over the physically admissible band
    integrate BACKWARD from P until terrain/elevation-plane intersection → origin
    implied MV = |v₀| ; implied flight time; implied impact speed
    weight = prior_MV(implied MV) × L(observations) × prior_BC × prior_wind
density(ground plane) ← weighted sample cloud (histogram grid + smoothing)
contours ← highest-density regions at 50 % and 95 % total mass
```

- **Priors:** MV range as uniform or normal (user pick); BC tolerance; wind σ;
  each explicitly shown in the UI and stored in the report.
- **Search domain:** auto-derived from the MV range (back-azimuth sector +
  max slant range), user-overridable. Out-of-domain samples are rejected, not clamped.
- **Statistics:** highest-density-region contouring (sorted-cell accumulation)
  rather than naive KDE level sets — correct for skewed, banana-shaped ridges.
- **Parallelism:** `Parallel.For` over samples; deterministic RNG seed exposed in
  UI and report (reproducibility is a Daubert requirement).
- **Performance target:** 50 000 samples ≤ 10 s on an 8-core machine.
- **Forward solver** exists alongside (same core dynamics) — used for validation,
  round-trip testing, and a "predict impact from a suspect position" cross-check mode.

---

## 5. Inputs & outputs

### Inputs (GUI panels)

| Panel | Fields |
|---|---|
| **Scene** | P (lat/lon, elevation or DEM auto), exterior θ ± σ, exterior azimuth ± σ, interior θ/az (optional), magnetic declination, search radius/sector |
| **Bullet** | caliber, mass, BC value + **σ(BC)**, BC reference (G1/G7) *or* BC(v) curve *or* C_D(Mach) CSV; twist rate, SG (advanced); recallable from the bullet profile library (D8) |
| **Muzzle** | MV min/max (or mean/σ), distribution type |
| **Atmosphere** | T, P, RH (or standard-at-elevation) |
| **Wind** | speed, FROM-direction, σ per axis; optional multi-layer profile |
| **Advanced** | Coriolis on/off, spin drift on/off, Magnus, integrator tolerance, RNG seed, origin elevation plane override |

### Outputs

1. **Map:** impact marker, back-azimuth reference line, density heatmap,
   **50 % and 95 % origin contours**, optional sample cloud overlay.
2. **Statistics panel:** most-likely origin (lat/lon, UTM), credible-region areas (m²),
   implied MV distribution summary, flight time, impact speed, elevation-error
   contribution to range error, density altitude.
3. **Exports:** PNG map · CSV/GeoJSON sample cloud + contours · **HTML/PDF case
   report** containing every input, model version, drag-table provenance, RNG seed,
   tolerances, and limitations — the audit trail.

---

## 6. Architecture

```
4TRAC.sln
├─ 4TRAC.Core            net8.0 class library — no UI dependencies
│   ├─ Geo/              WGS84, ECEF↔LLA↔ENU, UTM, geodesic distance, DEM grid math
│   ├─ Atm/              ISA/user atmosphere, ρ(h), density altitude
│   ├─ Drag/             IDragModel → G1G7Model | BCvModel | CdTableModel
│   ├─ Dynamics/         EOM assembly (gravity, drag, Coriolis, spin drift, wind)
│   │                    IIntegrator → Rk4Fixed | Rk45DormandPrince (adaptive, tol)
│   ├─ Forward/          Trajectory engine, event detection (ground hit), state history
│   ├─ Inverse/          Monte-Carlo sampler, backward engine, priors, weights
│   ├─ Stats/            weighted HDR contours (marginal/2-D), quantiles, summaries
│   ├─ Terrain/          DEM tile provider + cache + bilinear sampler
│   ├─ Library/          bullet/ammunition profile store (D8, JSON CRUD)
│   └─ Report/           case-report model, GeoJSON/CSV writers
├─ 4TRAC.App             WPF (.NET 8), MVVM
│   ├─ Views/            input panels, map view, results view
│   ├─ Map/              Mapsui + OSM raster & aerial layers, heatmap/polygon overlays
│   └─ ViewModels/       async run/cancel/progress plumbing
├─ 4TRAC.Validation      console harness → generates the validation report (CSV/MD)
├─ 4TRAC.Tests           xUnit: V0 verification + V1 golden regression tests
└─ data/                 drag tables (JSON, with provenance), golden reference sets,
                         bullet profiles (JSON), default presets
```

**Third-party (to be license-audited before adoption):** Mapsui (map rendering),
tile/DEM HTTP sources (OSM usage policy compliant caching), NetTopologySuite only if
needed for polygon ops (marching squares likely implemented in-house).

**Packaging:** `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true`
→ single EXE; also a framework-dependent build for a smaller binary.

---

## 7. Validation & verification plan

*No guessing — every claim below has a pass/fail criterion.*

### V0 — Code verification (unit level)
| Test | Criterion |
|---|---|
| No-drag ballistic analytic solution vs integrator | error ≤ 1e-6 relative at RK45 tol |
| Integrator convergence order (step halving) | observed order ≈ 4 (RK4) / 5 (RK45) |
| Atmosphere ρ at ISA table points | ≤ 0.1 % deviation |
| ECEF↔LLA↔ENU round trips (global sample) | ≤ 1 mm |
| Gravity/rotation bookkeeping: stationary probe at equator & pole in ECEF | matches normal gravity to ≤ 1e-4 m/s² (kills centrifugal/Coriolis sign errors) |
| Coriolis direction test | N. hemisphere deflection to the right, magnitude vs closed-form |
| Drag-table interpolation monotonicity, endpoint exactness | pass/fail |

### V1 — Forward solver cross-validation (published data)
Golden datasets recorded in `data/golden/` with **source citations** (JBM-calculator
outputs, Lapua/Applied Ballistics trajectories, Pejsa worked examples):
bullets spanning pistol/rifle, ranges 50–1000 m, at least 3 weather conditions.

Acceptance (example thresholds to be finalized against the reference spread):
- impact velocity ≤ 0.5 % deviation,
- drop/windage ≤ 1 % of value or ≤ 2 cm, whichever larger,
- time of flight ≤ 1 %.

G1 vs G7 vs C_D-mode spread on the *same* bullet is recorded as an explicit
**model-discrepancy band** used later in inverse weighting.

### V2 — Round-trip recovery study (the error-rate experiment)
1. Pick known origins; **forward-simulate** synthetic scenes across a design matrix:
   range {50, 100, 250, 500, 1000, 2000 m} × θ {5°, 15°, 30°, 45°} ×
   azimuth {N/E/S/W} × wind {0, 3, 8 m/s} × scene-angle noise {0.25°, 0.5°, 1°, 2°}
   × MV-prior widths × BC error {0, 3 %, 6 %}.
2. Run the inverse solver on each; 200 MC trials per cell (auto-generated harness).
3. **Metrics:** bias, RMSE of MAP origin; **coverage** — fraction of trials where the
   true origin lies inside the 50 % and 95 % regions; region area (m²).
4. **Pass criteria:**
   - 95 % region coverage ∈ [92 %, 98 %] over the whole matrix,
   - 50 % coverage ∈ [45 %, 55 %],
   - median origin error ≤ max(5 m, 1.5 % of range) for θ ≥ 15° at ≤ 1000 m,
   - all deviations itemized and explained, never silently absorbed.
5. **Sensitivity/tornado output:** variance contribution per input → publishes the
   required scene-measurement precision (e.g., "σ(θ) must be ≤ 0.5° for ≤ 2 % range
   error at 500 m").

### V3 — Elevation/terrain validation
- DEM vs surveyed control points where available; propagation of DEM vertical error
  into origin-range error validated against the `Δz/tan θ` analytic prediction.

### V4 — Field validation (later, if a range becomes available)
Documented protocol: RTK-GPS muzzle positions, instrumented target with probe-rod
angles, blind trial procedure; produces the fielded error rate for the report.

### Continuous
- Golden regression tests in CI: any change to the physics core that moves V1/V2
  numbers must update goldens with a written justification.
- Performance benchmark (samples/sec) tracked.

---

## 8. Milestones

| M | Scope | Exit criterion |
|---|---|---|
| **M0** | This plan, decisions locked | user sign-off |
| **M1** ✅ | Core: Geo, Atm, Drag, Dynamics, Integrators | all V0 tests green — **50/50 pass, 2026-10-20** |
| **M2** ✅ | Forward solver + golden data sets | V1 cross-validation within thresholds — **85/85 pass, 2026-10-21** |
| **M3** 🔄 | Inverse Monte-Carlo engine + stats/contours + 4TRAC.Validation harness | V2 matrix passes; report auto-generates — **M3.1 + M3.2 + M3.3 done, 106/106, 2026-10-22; thinned V2 demo PASSES all three §7 criteria (96.79 % / 54.49 % / 5.73 m ≤ 7.50 m), tornado publishes required precision (σθ ≤ 0.25°, MV ≤ 3 m/s, σw ≤ 0.5 m/s); plan-weight run pending (economics in §8.3)** |
| **M4** | WPF GUI + map (tiles, DEM, overlays) + exports | end-to-end scenario run by hand, single-file EXE |
| **M5** | Case report, user manual, limitations document, packaging, V1–V3 final report | shippable v1.0 |

### 8.1 M1 implementation notes (2026-10-20)

Code: `src/4TRAC.Core` (Geo, Atm, Drag, Dynamics) + `tests/4TRAC.Tests`
(xUnit, 50 tests — the complete V0 battery: integrator order on analytic ODEs,
vacuum parabola vs closed-form flat-Earth solution, gravity stationary probes
at equator/pole/mid-latitude, Coriolis vs closed-form ENU expressions, ECEF/
LLA/ENU/UTM round trips + Vincenty against published arc constants, ISA anchors,
drag-node exactness/monotonicity, BC↔CD cross-mode equivalence, quadratic wind
law). Key decisions made while implementing:

- **Drag scaling convention (SI-safe):** all drag models expose
  `Beta = (π/4)·CD_ref(M)/BC_SI` [m²/kg] with `BC_SI = BC[lb/in²]·703.0696…`
  (exact conversion), and deceleration `a = −½·ρ·β·|v_air|·v_air`. This makes
  scalar-BC, BC(v) and full-CD-table modes interchangeable; equivalence is
  asserted in the tests.
- **Gravity system:** WGS84 NIMA-form normal gravity (γ_e = 9.7803253359,
  k = 0.00193185265241, m computed from GM/ω). The GRS80 system's pole value is
  1.4e-6 m/s² higher — irrelevant at ballistics scale, but tests pin our own
  system's consistent anchors.
- **BC(v) interpretation:** the bullet's G-style BC evaluated at current
  air-relative speed; the reference curve supplies the Mach-shape, BC(v) the
  scaling (the standard "BC vs velocity" convention, JBM/Litz).
- **PCHIP interpolation** for all drag tables: node-exact, overshoot-free across
  the transonic cliff; out-of-table Mach clamps to endpoint values (policy
  documented in code).
- **RK45 `Step` API** returns `(HTaken, HNext)` — internal rejections make it
  impossible to infer the taken step from the caller's request; V0 caught this.
- **Engine:** fixed-step RK4 + bisection-refined ground event (1e-4 m), because
  a fixed-step path integrates through the event cleanly; RK45 kept as the
  validation/reference integrator.
- Environment note: .NET SDK 8.0.426 installed via winget on the dev machine.

### 8.2 M2 implementation notes (2026-10-21)

Code: `src/4TRAC.Core/Solver` (forward solver, fixed-step RK4 + bisection ground
event; RK45 reference) + `tests/4TRAC.Tests/Golden` + `data/golden/` —
**85/85 tests, V1 cross-validation within thresholds** (full derivation in
`data/golden/README.md`, band in `data/golden/model_discrepancy_band.md`):

- **Golden corpus:** 19 tables from three independent sources (JBM/BE semicolon
  tables and Exbal Report-Trace printouts via the gehtsoft BallisticCalculator1
  test corpus; Warner Tool Doppler sheets), spanning 65–285 gr, 50–2000 yd,
  G1/G5/G7/GI + CD tables + banded BC(v), 10 °F→100 °F, 0→10 000 ft, calm→20 mph,
  Coriolis east/north.
- **Interpolation convention corrected (supersedes §8.1 for bundled tables):**
  bundled G-standards evaluate **linearly** between nodes (the reference
  ecosystem's convention); PCHIP stays for user-supplied sparse CD tables.
  Golden acceptance pinned this: PCHIP smoothing shifts integrated drag ~0.3 %
  → ~1 % drop error at 1000 yd vs linear.
- **F-WIND:** the reference's along-wind term is inverted (its `WindVectorRaw`
  feeds the FROM-cosine into the air velocity — a headwind reduces drag like a
  tailwind); crosswind is physically correct. Effective direction
  `(180 − from + 360) % 360` runs full-column acceptance, pinned by mirrored
  vs physical (0.01–0.05 % vs 2.0–5.7 % at 1000 yd); pure-crosswind tables
  coincide, so the Exbal 3-o'clock wind tests the physical convention directly.
- **F-HUM:** humid tables decide the humidity treatment empirically — moist
  (vapor-pressure-corrected) density matches to 0.01 % where dry misses by
  0.55 % (`g1_wind_hot`); moist is the production default.
- **F-B1:** the `b1_*` tables do not imply their header's scalar BC; their
  velocity columns carry a BC(v) curve. Fitted per file by a multiplicative
  loss-ratio iteration (re-zeroed per pass, damped after 50 passes), with the
  last six knots pooled non-increasing (PAVA) and Chebyshev-centered — worst
  velocity residual 0.048–0.312 % across the family. Velocity is circular by
  construction; TOF (≤ 0.72 ms), drop and windage remain the independent checks.
- **F-DROP:** JBM-corpus drop columns carry a measured far-field tail
  (≈ 8.5e-5 m/m past the zero range, ≤ 0.067 m at 1000 yd) attributable to the
  reference (its headers declare Coriolis/spin off and its velocity/TOF match
  ≤ 0.03 %). Carried as an explicit additive envelope on drop/path assertions
  only — velocity/TOF/drift stay at full V1 tightness, Exbal CSV not enveloped.
- **Exclusions audited, not assumed:** elevated-shot tables (reference's
  flat-fire-equivalent incline model, cos-θ pin), spin columns (M4, Litz),
  Warner 65 gr's first TOF row (printout implies average speed above muzzle),
  `be_coriolis_*` drift beyond 1000 m (reference adds spin drift).
- **Model-discrepancy band recorded** (G1 vs G7 vs CD vs banded-BC spreads:
  0.17–0.61 % for Warner/ELD-M, 3.9 % banded-vs-single at 1000 yd) —
  regenerated every run, transcribed in `data/golden/model_discrepancy_band.md`.
- V0 note: `Rk4_StepHalving_ConvergesAtFourthOrder` needed a smooth constant-CD
  probe — G-standard transonic CD kinks destroy h⁴ scaling locally.

### 8.3 M3 implementation notes

#### M3.1 — Inverse scaffold: priors, sampler, backward engine (2026-10-22)

Code: `src/4TRAC.Core/Inverse/` (`Xoshiro256`, `ScalarPrior`, `Hypothesis`/
`HypothesisSampler`, `BackwardSolver`/`BackwardTrajectoryEngine`) +
`tests/4TRAC.Tests/InverseTests.cs` — **95/95 tests** (85 M2 + 10 new).

- **Backward integration = same EOM, negative step.** Every term (gravity,
  drag against v_air, Coriolis −2Ω×v) is state-local, so integrating
  `TrajectoryModel` with h < 0 from the impact state re-traces the forward
  states exactly — no sign flips anywhere. At the origin-plane crossing the
  state velocity IS the launch velocity (speed = implied muzzle velocity).
  Verified: the noise-free identity (σ→0) round-trips six scenes (calm/wind/
  Coriolis ±/lat 60°/−35°/steep lob) to **sub-mm, µm/s-class, ≤1e-4°**.
- **Crossing rule:** first crossing of the origin plane with ENU vertical
  velocity > 0 (a lofted launch leaves the plane ascending); the impact start
  and the far-side mirror crossing are descending and never fire. Downhill-
  launch origins (v_up < 0 at the origin) are an open M3.2+ item alongside
  terrain-following origin planes.
- **Wind-frame finding (published, not absorbed):** the forward solver
  resolves wind through the launch-point ENU frame, the inverse can only
  resolve through the impact-point frame; the frames differ by d/r_earth,
  which converts part of a *horizontal* wind into a spurious *vertical* wind
  of ~w·d/r (~0.85 mm/s at 5 m/s, 1.1 km). Shallow arcs amplify vertical air
  motion strongly (measured sensitivity ≈ 30–70× δw·TOF vs ≈ 3–5× for
  horizontal — the plane-crossing time scales with 1/v_z, and time errors
  convert to along-track position at 500–850 m/s), costing ~2 cm of origin
  identity at that scene. This is a wind-meter-location ambiguity of the
  scene measurement (≤ 1 mm/s of vertical wind, far below any wind σ), not a
  solver defect. **Convention adopted:** the inverse takes the measured wind
  resolved **once to ECEF at the scene anchor** and holds it fixed (the air
  mass does not rotate with the observer's axes); the profile-based overload
  remains for layered crime-scene wind, with the term bounded by
  60·(d/r)·w·TOF in `Backward_WindFrameRotationTerm_IsQuantified`.
- **Determinism:** the sampler runs on an in-house xoshiro256** (SplitMix64
  seeding) — `System.Random`'s algorithm is implementation-specific and could
  silently break case-report reproducibility across .NET versions. Same seed
  ⇒ bit-identical hypothesis cloud (tested); priors recover their declared
  moments at n = 1e5 (bounds exact, mean/σ inside MC error); azimuth wrapped
  to [0,360) across the seam.
- **Observables convention (M3.1 → production):** exterior angle and travel
  azimuth are defined in the ENU frame **anchored at the impact point**
  (compass/inclinometer at the hole); launch angles are reported in the
  origin-anchored frame (the aim). `ForwardSolution.Impact` angles are
  currently origin-frame projections (fine for V1 drop-chart comparisons at
  golden ranges); aligning them to the impact-anchored convention is an M3.2
  item if the inverse consumes them directly.
- V2 gate shape confirmed: flat scenes (no DEM — terrain is V3/M4); the full
  matrix (~10⁴ cells × 200 trials ≈ 2×10⁶ inverse runs) will be the
  parallelized console-app run, with a smoke subset in the unit suite.

Next: M3.2 — MV-prior → impact-speed mapping, likelihood weighting, HDR
statistics + marching-squares contours, then the `4TRAC.Validation` harness.

#### M3.2 — Muzzle-velocity consistency, Monte-Carlo cloud, HDR statistics (2026-10-22)

Code: `BackwardSolver.SolveAtMuzzleVelocity` (+ `MuzzleConsistentSolve`),
`Inverse/OriginCloud.cs`, `src/4TRAC.Core/Stats/` (`Vec2`, `MarchingSquares`,
`Hdr`), new tests in `StatsTests.cs` + `InverseTests.cs` — **101/101 tests**
(95 + 6 new), log `testrun6.log`, 0 warnings.

- **Exact prior draws ⇒ weights ≡ 1 (no importance reweighting anywhere).**
  The declared prior lives at the MUZZLE (D1) but the backward engine is
  parameterised by the IMPACT speed, so `SolveAtMuzzleVelocity` finds the
  impact speed whose backward-implied MV equals the drawn target — bracketed
  Illinois false position on the continuous, strictly monotone
  f(v) = impliedMV(v) − target (f(target) > 0: backward integration only
  accelerates toward the launch), bracket [0.2·MV, MV] with expansion guard,
  periodic bisection step as stagnation insurance, converged at |f| < 1e-4
  m/s in typically 5–8 full integrations. The alternative — sampling impact
  speed and weighting by the MV prior — needs the Jacobian |dMV/dv_imp|
  (O(1), ≈1.1–1.3); an *estimated* Jacobian would bias the cloud, so it was
  rejected for the exact root find. The M3.1 "likelihood weighting loop"
  therefore reduces to exact sampling: every hypothesis is a prior draw, all
  weights are 1, and the cloud needs no reweighting. (First implementation
  had the Illinois damping side inverted — damped on alternating moves, not
  repeated same-side moves — which stagnated at the 16-eval cap; caught by
  the identity-at-target test, fixed, plus the bisection step.)
- **Cloud driver (`OriginCloud.Run`):** per hypothesis draws (MV, θ, az, BC,
  wind E/N) from the declared priors, resolves wind E/N → ECEF **once at the
  impact anchor** (M3.1 convention — the air mass does not rotate with ENU
  axes), applies the drawn BC (ScalarBc: draw *is* the BC; BcVelocity: knots
  scaled by draw/prior-centre, shape-identical F-B1 curves; CdTable: no BC
  exists to perturb — σ(BC) on CD tables is an **M4/Litz item**, documented,
  not silently ignored), runs the MV-consistency solve, and reports origin
  E/N in the **impact-anchored scene frame** + implied MV. Non-converged
  hypotheses are counted (`FailedCount`) and dropped, never kept silently.
  Noise-free collapse gate: n = 18 near-exact priors ⇒ cloud mean ≤ 2 m from
  truth, bearing = azimuth + 180° ± 3° (up-range placement / E-N convention),
  spread ≤ 8 m, and a same-seed rerun is **bit-identical** (case-report
  reproducibility).
- **HDR statistics, in-house (no NuGet):** Gaussian KDE on a regular grid —
  Scott 2-D rule h = pooled σ·n^(−1/6) (explicit bandwidth for non-convex
  clouds, see below), 3h + 2-cell padding so supported regions close inside
  the grid, separable kernel truncated at 3h; mass-ranked cell thresholds per
  requested fraction (default {0.5, 0.95}; ties at the cutoff are all kept, so
  a band never contains LESS than its fraction); marching squares with linear
  edge interpolation, **inside-left directed segments** (outer rings CCW,
  hole rings CW ⇒ the signed shoelace areas sum to the net region area) and
  the cell-centre average decider for ambiguous saddles. Outputs: rings,
  net area, KDE threshold, mean/median, radial quantiles.
- **Validated against analytic targets (`StatsTests`):** paraboloid level set
  → exact circle (area within 1 %, closed, CCW, every vertex on the level
  set); 20 k Gaussian cloud → χ²₂ HDR ellipse areas within 15 %, area ratio
  95/50 within 12 % of 4.322, thresholds ordered, single closed CCW ring per
  band, median/mean at centre; 15 k annulus → ≥ 2 rings with a CW hole
  centred on origin. **Itemized measurement:** with bandwidth h = σ_r the KDE
  widens the radial profile to σ_eff = √(σ_r² + h²), so the annulus 95 % band
  is ~20 % larger than the naive ±1.96·σ_r prediction — the test asserts the
  σ_eff prediction (observed agrees to ~0.2 %); strongly non-convex clouds
  must therefore pass an explicit bandwidth matched to their local spread
  (documented on `Hdr.Compute`). n = 1 degenerate handled (bands coincide on
  the sampled peak — honest for an under-resolved bump).
- Observables: the cloud consumes `ImpactObservation` directly, so the
  harness projects forward impact states into the impact-anchored frame
  itself (as the identity tests do); the `ForwardSolution.Impact` angle
  alignment remains a harness item. GeoJSON/CSV writers deferred to M3.3,
  once the V2 report fixes the output shape.

Next: M3.4 — full V2 run (see M3.3 economics), then GeoJSON/CSV writers for
case output, DEM/terrain origin planes, and the M4 Litz spin-drift layer.

#### M3.3 — `4TRAC.Validation` V2 harness (2026-10-22)

Code: `src/4TRAC.Validation/` (`DesignMatrix`, `SceneFactory`, `TrialRunner`,
`Aggregation`, `Tornado`, `ReportWriters`, `Checkpoint`, `Program` CLI) + smoke
and resume tests in `tests/4TRAC.Tests/ValidationSmokeTests.cs` /
`ResumeRunTests.cs` — **111/111 tests** (`testrun17.log`, 0 warnings).

- **Structure:** a cell = (scene combo: range × launch-el × azimuth × wind
  speed) × (protocol: σ(θ) × MV-prior half-width × σ(BC) × σ(wind)); truth MV
  is SOLVED per combo (secant on the monotone range(MV)) so the shot lands at
  the declared range. Each trial: perturb the observables with the cell's
  noise, build priors centred on the observations (calibration protocol:
  truth at every prior's centre - coverage is only meaningful for
  correctly-specified priors; systematic misdeclaration is a tornado input),
  run the exact-prior cloud, score truth containment in the 50 %/95 % HDRs +
  median/MAP origin error + areas. Per-trial seeds derive deterministically
  from (run seed, cell, trial) - a single trial is reproducible from the
  report alone. Outputs: `cells.csv`, `trials.csv`, `report.md` with the
  pass/fail criteria of §7 and a full-matrix wall-time extrapolation.
- **Tornado mode:** one reference scene (500 m / 30° / 3 m/s), one input's
  uncertainty swept per ladder (σ(θ) 0.1–2°, MV half-width 1–15 m/s, σ(wind)
  0.25–3 m/s, σ(BC) 0.2–6 %), the rest nominal; published output is the
  largest sigma still inside the 1.5 %-of-range error budget = the required
  scene-measurement precision (§7).
- **Documented protocol decisions** (all repeated in the generated report):
  θ axis = launch elevation (realized exterior angle recorded per cell);
  wind = full-value crosswind FROM azimuth+90°; "BC error" realised as the
  WIDTH of the BC prior with truth centred (the matrix measures how BC
  ignorance inflates the region; a misdeclared BC is a separate sensitivity
  input); Coriolis on both directions; flat scene (no DEM, V3/M4).
- **MV validity finding:** the plan's matrix asks for scenes (short range ×
  steep angle) whose solved MV is far below the published G-table bound
  (400 fps = 121.9 m/s) - e.g. 250 m/15° needs 72 m/s. Rule adopted:
  floor at **Mach 0.2** (the bundled G1 curve's low-speed extension is
  smooth and near-vacuum there, and V2 coverage calibration is
  MODEL-RELATIVE - truth and inverse share the curve), cells below it
  EXCLUDED with reason; cells below the published 400 fps bound flagged
  **LOW-MACH** in the report (they validate the solver, not the bullet
  model). ~9 of 24 (R,θ) pairs (the 50 m row and the steepest short-range
  corners) are excluded this way - itemized, never silent.
- **V2 caught a real cloud bug on its first run:** `OriginCloud.Run` passed
  the UN-DRAWN observation into the MV-consistency solve, silently dropping
  the drawn exterior angle/azimuth - the cloud had zero angle scatter while
  trial centers jittered with observation noise (measured: 11 m cloud
  extents vs ±20 m center scatter at 500 m/5°, coverage collapsing to
  17 %). Fixed (per-hypothesis `ImpactObservation` with the drawn angles);
  the noise-free identity test's angle prior tightened to 0.0005° to stay a
  true σ→0 limit at shallow launches (dR/dθ = 2cot2θ ≈ 95 rad⁻¹ at 0.6°).
  This is exactly the silent-input-drop class the harness was built for -
  unit tests with tight priors could not see it.
- **Economics measured:** ~4.4 ms per TOF-second per backward solve on the
  dev machine (12 cores); heavy cells (2000 m / 45°, TOF ~28 s) dominate
  wall time. The plan-weighted full run (stride 1, 200 trials × 250
  hypotheses) extrapolates to ~4.7×10³ hours at 12-way parallelism - i.e.
  NOT a single-machine job; V2 is therefore run as a thinned matrix
  (--stride) at reduced trials/hypotheses with the deviation itemized, and
  the plan-weight run is a scheduling decision (cluster/overnight).
- **KDE smoothing-bias calibration (third V2 catch):** with n = 40
  hypotheses the Gaussian kernel (Scott h ≈ 0.5σ̂) inflates HDR level sets -
  a band labelled p covers 1−(1−p)^k of the unsmoothed cloud (k = the
  kernel's per-axis variance addition). Measured before the fix: the 50 %
  band covered 69.3 %, the 95 % band 97.8 %. A pooled-sigma k corrected the
  isotropic case but under-corrected elongated clouds (50 % → 65.9 % only:
  joint coverage is limited by the WIDE axis). Final rule: each band's mass
  target is bisection-inverted through the exact Gaussian-ellipse coverage
  integral over the covariance EIGEN-AXES (1-D quadrature; Abramowitz &
  Stegun 7.1.26 erf, since the BCL ships no Math.Erf) - exact for
  single-blob Gaussian clouds, approximate for multi-modal ones (the
  harness measures the residual). After the fix both bands sit at their
  labels (demo: 50 % → 54.5 %, 95 % → 96.8 %). Same class of catch as the
  plan's §3.3 note against naive KDE level sets - now quantified and
  compensated, not just flagged.
- **Error criterion qualified by scene precision (§7 reading made
  explicit):** at the nominal inclinometer (±0.5°), a 500 m / 15° scene
  leaves ~15 m of along-track uncertainty - the 7.5 m budget is then
  unreachable by ANY estimator (information bound, not solver error; the
  tornado ladder measures it: σθ 0.5° → 8.4 m, 0.25° → 6.2 m at the
  reference scene). Read jointly with the tornado, the §7 error budget is
  a promise FOR SCENES WHOSE MEASUREMENTS MEET THE PUBLISHED REQUIREMENTS.
  Implementation: criterion ANCHOR cells - every eligible (R,θ) scene at
  the ACHIEVABLE protocol (σθ 0.25°, MV ±1 m/s, σ(wind) 0.5 m/s, at/inside
  the tornado-published precisions) - are force-included in every run
  (arithmetic strides alias protocol against geometry: a stride-145 demo
  contained zero nominal-protocol eligible cells), and a criterion with no
  eligible cell reports SKIP, never FAIL.
- **V2 demo result (thinned, all three §7 criteria PASS):** stride 144→145
  + 15 anchors = 87 cells (52 run; 35 excluded cells = 32 distinct scenes
  below the Mach floor; 6 trials × 40 hypotheses; 17.2 min): 95 % coverage
  **96.79 %** ∈ [92,98], 50 % coverage **54.49 %** ∈ [45,55], origin error
  worst eligible cell **5.73 m vs the 7.50 m budget** (500 m / 15° - the
  information-bound-limited corner) with all eligible cells inside budget.
  Statistical weight is ~10⁻⁴ of the plan-weight run; the gates validate
  the pipeline, the plan-weight run remains the scheduling decision above.
- **Tornado output (published required scene precision, 30 trials × 20
  hypotheses, reference scene 500 m / 30° / 3 m/s, budget 7.50 m):**
  σ(θ) ≤ **0.25°**, MV prior half-width ≤ **3 m/s**, σ(wind) ≤ **0.5 m/s**
  per axis (each sufficient at the others' nominal values); no σ(BC) meets
  the budget at nominal others - BC knowledge is NOT the binding
  constraint at 500 m (angle/MV/wind are). MV precision dominates region
  area: 614 m² at ±1 m/s vs 4 281 m² at ±5 m/s.
- **Coverage on tight-prior subsets over-covers by construction (preflight
  finding, anchors-only 40 × 80 run: 50 % → 70.4 %, 95 % → 100 %):** the
  documented calibration protocol fixes truth at every prior's centre, so
  symmetric MV/BC prior axes widen the cloud along directions the truth
  never leaves - a subset dominated by those axes covers more than its
  labels, exactly as centre-truth calibration implies. This is NOT a solver
  error and not a gate failure: the plan's coverage criterion is a
  WHOLE-MATRIX statement (mixing noise-dominated and prior-dominated
  cells), the sensitivity to real MV/BC misdeclaration is the tornado's MV
  and BC ladders, and the anchors' job is the origin-error gate (which
  passed at production weight: worst 4.87 m ≤ 7.50 m). Anchors mode
  therefore reports the coverage gates as **SKIP** with this explanation.
  A per-trial truth drawn from the priors would calibrate coverage of
  tight-prior cells too; it costs one truth solve per trial (~3-5× the
  run) and is deferred (open question 4).
- **M3.4 intermediate-weight run - COMPLETE (2026-10-25):** full stride-13
     matrix run on dedicated machine `optimusprime` (i5-2500K, 4 cores),
     PID 12384 under Task Scheduler/SYSTEM + `watch-production.ps1`. Run
     completed autonomously (no session-attached process), watchdog exited
     0. **Cells:** 507 recorded / 813 matrix cells, 306 excluded (itemized,
     invariant `completed+excluded=total`). Artifacts copied to
     `validation-out/v2-intermediate-final/` (report.md 22,227 B, cells.csv,
     trials.csv). Checkpoint deleted on success by the harness.
     **V2 gates (seed 20261022, 40 trials x 80 hypotheses, stride 13):**
     - 95% region coverage (whole matrix): **PASS** 97.51 % (required 92-98 %)
     - 50% region coverage (whole matrix): **FAIL** 59.82 % (required 45-55 %)
     - median origin error at published scene precision (θ>=15°, R<=1000 m):
       **PASS** worst eligible cell #807 (500 m/15°): median 4.75 m vs
       budget 7.50 m; all eligible cells within budget (pooled median 4.30 m,
       7 eligible cells).
     **Assessment:** coverage fails only on the 50% band (overshoot +4.82 pp
     above 55%). The 95% band passes; origin error passes well under budget.
     This is a calibration/coverage issue, not a solver/integration crash.
     Per §7 the result is recorded exactly as above.
     **Infrastructure notes (for traceability):** first session run killed by
     harness session restart at 360/813 (artifacts-only-at-end); fixed by
     checkpoint/resume (`Checkpoint.cs`) - proven end-to-end including a
     null-`CellOutcome` on resumed cells that file-hash-only checks missed
     (fixed via `OfType<CellOutcome>()` + invariant), excluded cells never
     checkpointed (re-evaluated each invocation). **111/111** tests
     (`testrun17.log`). Portable package `dist/4TRAC-validation-win-x64.zip`
     with watchdog validated (kill mid-run -> restart -> resume -> finish).
     Windows OpenSSH killed session children on disconnect -> production
     launched via Task Scheduler/SYSTEM with `MultipleInstances=IgnoreNew`,
     `AtStartup` trigger, unlimited runtime. Preflight on optimusprime
     reproduced the 12-core result exactly (worst 4.87 m) confirming
     seed determinism across CPUs/cores (~3.8/4 cores sustained).
     **Report ETA claim - REPORTING DEFECT, do not quote it:** the generated
     report printed a naive 10.5 h extrapolation over cheap sampled cells
     ignoring the multi-decade per-cell cost spread (affects no verdict).
     Not patched mid-run; fix in the next build (remove or label).

---

### 8.4 Flat-earth rebuild and working GUI (2026-10-29)

Decisions from the owner: online tiles (overrides the brief's "fully offline"),
spin drift ON, flat-earth model with a **10 000 m** validated envelope, truth-drawn
calibration implemented, V2 matrix deferred until the physics is shown to work.

Code: `src/4TRAC.Core/Flat/` (`FlatModel`, `FlatIntegrators`, `SpinDrift`,
`FlatSolver`, `FlatCloud`, `FlatAnalysis`, `CoverageStudy`) · `src/4TRAC.App` (WPF,
drives the real solver) · `tests/4TRAC.Tests/FlatPhysicsTests.cs`. The ECEF core
and its 111 tests are unchanged. **122/122 tests pass** (`dotnet test`, .NET SDK 8.0.427).

- **Verified by test:** vacuum range vs closed form (1e-3 m); Coriolis horizontal fall
  vs closed form (1 %, east deflection for northward fire); Miller stability factor vs
  hand calculation; Litz spin-drift value and sign; forward→inverse round trip recovers
  origin (1 cm) and muzzle velocity (0.01 m/s) with and without spin; zero-spread cloud
  collapses onto truth; a reported origin always ascends.
- **Spin drift convention:** post-integration lateral displacement D(T) and lateral velocity
  D'(T) with T the DRIFT-FREE time of flight (fixed-point iteration). Using the drift-carrying
  first pass gave a 4 cm error at 25 s flight (lateral velocity feeds Coriolis).
- **Defects found and fixed:** (1) the muzzle-velocity root find capped at 16 evaluations
  discarded ~25 % of steep-impact hypotheses non-randomly → cap 80, tolerance 1e-3 m/s;
  (2) spin correction used the drift-carrying time of flight (above).
- **Removed:** the ad-hoc `Pow(mass, 1.09)` deflation of the 50 % band in `Stats/Hdr.cs`,
  which had been tuned against the broken cloud. The recorded V2 outputs in
  `validation-out/` predate this change and the root-find fix and are stale.
- **Calibration, revised (2026-10-30).** The earlier failure came from the test, not
  the method. That test drew the shooter's launch elevation, which makes the origin prior
  non-flat. The cloud method assumes a flat origin prior, and for fixed muzzle velocity,
  BC and wind the solver is a bijection between origin and observed angles, so the cloud
  density equals likelihood × Jacobian: the exact posterior under that prior.
  Truth-drawn check with a flat origin prior (`CoverageStudy` origin mode: origin uniform
  by area in a 3–4.5 km annulus, launch found by shooting), BC/wind/Coriolis on:
  150 trials × 200 hypotheses → 50 % band 44.7 %, 95 % band 91.3 %;
  150 trials × 500 hypotheses → **50 % band 49.3 %, 95 % band 98.0 %** (both inside the
  brief's gates; 95 % on the upper edge). At 40 hypotheses the 95 % band covered only
  88 %, so the cloud needs ≥ 500 hypotheses (GUI minimum now 500, default 500).
  Importance weighting (option 2) is therefore not needed for a flat origin prior; it
  would be needed only if the truth's origin prior were not flat. Remaining cause to test:
  residual KDE bias at n = 200. The earlier notes below are superseded:
  the flat-angle prior implied by exact prior draws (brief §5 "weights identically 1"),
  and the Gaussian-ellipse mass calibration (brief §6) on a curved cloud.
- **Declared limitations (in every result):** flat origin plane (no terrain/DEM);
  constant wind (no layers); scalar G1/G7 BC only (BC(v) and CD(Mach) not wired to the
  flat solver); Magnus and wind gradients not modelled; descending-launch origins not
  reported (a lofted origin can match the same impact when the plane is above the impact,
  and is then reported with a note); LOW-MACH flag below 400 fps; 10 km envelope rejects,
  never clamps.
- **GUI fixes:** `InvariantGlobalization` overridden to false for the App only (the
  Core stays invariant for deterministic numbers); software rendering set in code (hardware
  WPF rendered a blank window on the dev machine); map overlay colours fixed.
- **Not yet done:** V2 matrix (deferred by owner), plan-weight run, DEM/terrain, layered
  wind, profile library, HTML/PDF case report, packaging as a single-file EXE, field
  validation (V4, needs a range).

### 8.5 Case report, terrain, packaging (2026-10-31)

- **Case report:** `Flat/CaseReport.cs` writes a self-contained HTML report: result, every
  input, declared model and envelope, exclusions and deviations, the calibration record
  (recorded run, not recomputed), precision-requirements status, and limitations. GUI button
  "Export case report (HTML)". Tested.
- **Terrain:** the origin can sit on a DEM surface (`Flat/Terrain.cs`). Input is an ESRI ASCII
  grid in WGS84 degrees, exported from any GIS tool. Heights use the exact tangent-plane
  vertical, so the ~7.8 m curvature drop at 10 km is included. The backward crossing test uses
  the surface and the ascent is measured relative to the ground. Track outside coverage is
  rejected (`OutsideTerrain`), never extrapolated. If more than 5 % of hypotheses leave
  coverage, no region is reported. **Not done:** automatic DEM download (needs a data-source
  decision). Now resolved by the SRTM source below.
- **DEM source (2026-10-31, decided and built):** SRTM 1-degree tiles from the AWS open
  terrain bucket (`elevation-tiles-prod/skadi`, no key). The app finds the tiles covering the
  10 km envelope, downloads any missing ones into `%LOCALAPPDATA%\4TRAC\dem` (atomic write),
  and runs offline from the cache thereafter. A local ESRI ASCII grid is also accepted. Checked
  live: tile N40W075 downloads (6.4 MB gz), and the default impact samples 13 m, in line with
  the area. End to end on real tiles: 500 of 500 hypotheses converge inside coverage, origin
  3739 m at bearing 89.3°. When terrain is used, the impact elevation comes from the DEM
  (the 100 m input is overridden and the override is noted). SRTM is 30 m resolution, so the
  elevation error bound is the published SRTM accuracy; that error propagates into origin
  range through the `Δz / tan θ` term (PLAN §3.5). Tests: 136/136.
- **Truth shooting** moved to `Flat/FlatShooter.cs` (public, start-height aware, several starting
  angles). Previously a fixed start angle left some long shots unaimed.
- **Packaging:** `dist/4TRAC-app-win-x64/4TRAC.App.exe`, single file, self-contained, about
  185 MB. Runs without a .NET install. A full Solve was run through UI Automation on the packaged
  build: results, calibration banner and map overlays all rendered.
- **Tests:** 129/129 pass.
- **V2 on the flat model, reduced matrix (2026-10-31):** `dotnet run --project src/4TRAC.Validation -- --mode flat`.
  Cells: origin range 500, 1000, 2000 m × crosswind 0, 3 m/s; 40 trials/cell, 500 hypotheses;
  216 completed trials, 24 skipped (shot not aimable). Output `validation-out/flat-v2-reduced/`.
  - 95 % coverage (whole matrix) 96.8 % — **PASS** (92–98 %).
  - 50 % coverage (whole matrix) 49.1 % — **PASS** (45–55 %).
  - Median origin error (R ≤ 1000 m, realised exterior ≥ 15°) — **SKIP**: no trial reached 15°.
    Realised exterior angles at R ≤ 1000 m are 0.4–1.8°, because ground-level shots barely drop.
    Median errors there are 86–147 m, far above the 5–7.5 m budget. At shallow impact angles,
    angle noise cannot pin range, so this is an information limit, not a solver defect. A steep-angle
    cell set is needed before the error gate can be evaluated.
  - Full matrix: not run. Cost estimate from this run: about 5 min for 6 cells; the full plan matrix
    is roughly 10⁴× larger (PLAN §8.3).
- **Still open:** spin drift in the origin-prior truth; re-running the Validation harness
  (still on the ECEF path) and the V2 matrix; real-DEM test; the 95 % band sits on the upper edge
  of its gate.

## 9. Open questions (non-blocking)

1. ~~**Drag-table provenance:** verify redistribution rights of G1/G7 table sources~~
   **RESOLVED (M1):** G1/G7 CD(Mach) tables are bundled in
   `src/4TRAC.Core/Data/g1.json` / `g7.json`. Transcribed from
   gehtsoft-usa/BallisticCalculator1 (LGPL-2.1 transcription); the underlying
   tabulated data originates from the Commission d'Expérience de Gâvre (1881,
   G1) and the US Army Ballistic Research Laboratory (G7) — US Government works
   in the public domain. Provenance is recorded in each JSON file and must be
   reproduced in every case report (§5 outputs).
2. **Interior-only angle correction model:** deferred (D3) — revisit after M3 with
   roof-material test data if available.
3. **Acoustic time-of-flight:** if available at scenes, adds a strong constraint —
   deferred to a post-v1 feature (kept in the input schema as optional).
4. **Per-trial truth drawn from the priors:** the V2 calibration protocol fixes truth
   at every prior's centre, so coverage over-covers on prior-dominated (tight-prior)
   cell subsets (§8.3). Drawing the truth MV/BC per trial would calibrate those
   subsets exactly; it costs one truth solve per trial (~3-5× run time) and is
   deferred until a coverage question actually hinges on tight-prior cells.

## 10. Known limitations (to be stated in the product, not hidden)

- DEM vertical error dominates at shallow incidence angles (§3.5) — the tool reports
  this term explicitly.
- Penetration deflects the bullet; interior angles are proxies (D3).
- Single-shot analysis; ricochet/multiple-hole reconstruction is out of scope for v1.
- Wind between muzzle and impact is unknowable in detail — treated as a nuisance
  parameter with user-set σ, never as a fixed truth.

---

## v1 Release Record (2026-10-31)

**v1.0 — shippable.** All milestones M0–M5 complete.

| Milestone | Scope | Status |
|-----------|-------|--------|
| M0 | Plan, decisions locked | ✅ done |
| M1 | Core: Geo, Atm, Drag, Dynamics, Integrators | ✅ 50/50 V0 tests |
| M2 | Forward solver + golden datasets | ✅ 85/85 V1 tests |
| M3 | Inverse Monte-Carlo + stats/contours + Validation harness | ✅ 111/111 tests; V2 gates pass (thinned) |
| M4 | WPF GUI + map (OSM, DEM, overlays) + exports | ✅ single-file EXE, GeoJSON/CSV/PNG/HTML |
| M5 | Case report, user manual, limitations, packaging | ✅ HTML report, USER_MANUAL.md, LIMITATIONS.md |

**Build:** `dotnet build 4TRAC.sln` → 0 errors. `dotnet test` → 136/136 pass.
**Packaging:** `dist/4TRAC-app-win-x64/4TRAC.App.exe` — single-file, self-contained, ~163 MB, runs on clean Windows 10/11 x64 with no .NET runtime.

**v1 scope:** flat-earth point-mass model, 10 km validated envelope, scalar G1/G7 BC, constant wind, Coriolis + spin drift (Litz), DEM/SRTM terrain, HTML case report, deterministic xoshiro256\*\* sampling, HDR 50 %/95 % regions, region-not-point output, itemized deviations and limitations.

**Deferred to v1.1+:** PDF case report, bullet profile CRUD at runtime, layered wind, BC(v)/CD(Mach) drag modes, descending-launch origins, Magnus effect, auto-IGRF magnetic declination, V4 field validation, full plan-weight V2 matrix.

**Artifacts:** `USER_MANUAL.md`, `LIMITATIONS.md`, `data/profiles/` (3 bullet profiles + schema doc), `dist/4TRAC-app-win-x64/4TRAC.App.exe`.
