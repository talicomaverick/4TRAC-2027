# Golden data — provenance, conventions, findings (V1 / M2)

This directory holds the external reference tables the forward solver is
cross-validated against (PLAN.md §7 V1) and the full derivation of every
non-obvious convention used to run them. Every statement below is pinned by a
named test in `tests/4TRAC.Tests/Golden/` or a unit test — nothing is assumed
and nothing is silently absorbed. Last full run: **85/85 passed (run 4,
2026-10-21)**.

V1 thresholds: velocity ≤ 0.5 %, TOF ≤ 1 %, drop/windage ≤ max(1 %, 2 cm).

---

## 1. Inventory and sources

### 1.1 Sources

| Source | Format | How obtained |
|---|---|---|
| **JBM** (jbm.org, JBM Ballistics) Trajectory Calculator / **Ballistics Explorer** (BE) | `;`-separated header + column table | Public test corpus of `goesoft-usa/BallisticCalculator1` (LGPL-2.1), a reimplementation whose source pinned our zero-solver, wind, and drop conventions (same corpus that supplies the G1/G7 tables, PLAN.md §9 Q1) |
| **Exbal** (Sierra) "Report Trace" printout | fixed-layout text/CSV report | Same corpus (`ExbalReportTest`) |
| **Warner Tool Company** Doppler-radar data sheets | transcribed one-page PDFs | Published PDFs, warner-tool.com, 2019-01-03 — citation is in each file's header comment |
| **Applied Ballistics** bullet config archive | `ab_archive/config.csv` | Used for published-scalar cross-reference (F-ELDM below); full use (B4/B5) is M3 |

Files are copied to the test output by the csproj (`golden/reference/…`
recursively); parsers tolerate the ANSI `°` bytes in the original corpus.

### 1.2 The 19 tables in `reference/`

**JBM/BE semicolon family (14 files).** Header lines `ammo;… rifle;… wind;…
atmosphere;… shot;…` then `yd;in;moa;in;moa;ft/s;mach;ft-lb;s[;in;moa]`
(range; drop; drop-moa; drift; drift-moa; fps; mach; energy; tof[; spin;
spin-moa]).

| File | Bullet (header) | Conditions | Role |
|---|---|---|---|
| `gehtsoft_g1_nowind_65gr_2600fps.txt` | 0.365 G1, 65 gr, 2600 fps | calm, 59 °F/78 %/29.92 inHg/0 ft, 100 yd | V1 baseline |
| `gehtsoft_g1_twist_220gr_3007fps.txt` | 0.5 G1, 220 gr, 3006.6 fps | calm; rifle line declares twist (`12in`, right) | V1 velocity/TOF/drop; spin column skipped (M4) |
| `g1_wind.txt` | 0.365 G1, 65 gr, 2600 fps | 20 mph @30°, 59 °F/78 %/29.92 | V1 + wind-convention pin |
| `g1_wind_cold.txt` | 0.465 G1, 65 gr, 3200 fps | 15 mph @45°, 10 °F/5 %/32.00 inHg | V1 + wind pin + cold front |
| `g1_wind_hot.txt` | 0.465 G1, 65 gr, 3200 fps | 15 mph @45°, 80 °F/50 %/29.5 inHg | V1 + wind pin + humidity direction |
| `g7_nowind.txt` | 0.465 **G7**, 65 gr, 3200 fps | calm, 59 °F/78 %/29.92 | V1 G7-standard coverage |
| `g1_nowind_up.txt` | 0.365 G1, 65 gr, 2600 fps | calm, **shot 10°** | **excluded from V1** (F-ELEV, audited) |
| `g1_nowind_up_supersonic.txt` | (same family) | **shot 10°** | **excluded from V1** (F-ELEV, audited) |
| `b1_bthp.txt` | 0.395 G1, 75 gr, 2790 fps | calm, 59 °F/50 %/29.92/0 ft | F-B1 fitted BC(v) |
| `b1_bthp_hot.txt` | same bullet | 100 °F/80 %/29.92/0 ft | F-B1 own profile + humidity pin |
| `b1_bthp_7kft.txt` | same bullet | 59 °F/50 %/29.92/**7000 ft** | F-B1 (shares `b1_bthp` knots) |
| `b1_eldm.txt` | 0.351 G7, Hornady 147 gr 6.5 mm ELD-M, 2700 fps | calm, 59 °F/50 %/29.92/0 ft | F-B1 fitted + F-ELDM band |
| `b1_eldm_10kft.txt` | same bullet | 10 °F/20 %/20.58 inHg/**10000 ft** | F-B1 scalar 0.322 (narrow span) |
| `be_coriolis_east.txt` / `be_coriolis_north.txt` | 0.365 G1, 65 gr, 2600 fps | shot line azimuth 90°/0°, **latitude 45°** | Coriolis drift; asserted ≤ 1000 m (F-SPIN) |

**Exbal "Report Trace" family (3 files).**

| File | Header (as printed) | Role |
|---|---|---|
| `ref_g5.txt` | "22 CCI Stinger", **G5 BC 0.318**, MV 2700 fps, 100 yd zero, Sight Adj 5.437 MOA, 59 °F/29.53 inHg/0 %, spin drift & Coriolis **off** | V1, flat-launch + sight-dial path convention |
| `ref_gi.txt` | same name, **GI BC 0.389**, Sight Adj 5.476 MOA | V1, second standard |
| `exbal_sierra168.csv` | Sierra MatchKing .308 168 gr, **banded G1 BC** (0.405/0.424/0.447/0.462), MV 2561 fps, 100 yd/1.5 in, **10 mph @90°** (pure crosswind), 59 °F/78 %/29.53 | V1, physical wind convention + banding band |

**Warner family (2 files).** `warner_65_flatline.txt` (6.5 mm Flatline,
123 gr) and `warner_338_flatline.txt` (.338 Flatline, 285 gr): smoothed
Mach/CD/BC-G1/BC-G7 column + Distance/Velocity/Time table. The CD/BC columns
are density-independent; the velocity table is raw measurement at ≈ 0.77 of
standard density — reproduced at their test-suite conditions (6000 ft,
23.98 inHg station, 78 °F, 25 % RH).

---

## 2. Scenario conventions per source family (normative)

Pinned against the producing implementations' source and the files' own
numbers; implemented in `GoldenScenarios` (tests/…/Golden/GoldenData.cs).

**JBM/BE semicolon tables — lob launch.**
- Launch elevation θ₀ = atan((d_flat(Z) + h_s) / Z): a calm flat pre-run, its
  drop at the zero range d(Z), sight height h_s. Pinned digit-for-digit against
  the reference's zero solver (their sight test: 5.489 MOA = (2.72 in drop +
  3.0 in sight) / 100 yd).
- Deep target (−600 m) and MaxTimeS 30 s: the lob flies *through* the zero, so
  the target must never ground the run early.
- Drop column = y_lob − h_s (line of sight runs flat from the sight at launch
  height): row 0 = −h_s and row Z = 0 pin this exactly.
- Muzzle velocity, sight height, zero, atmosphere, wind, shot angle, azimuth,
  latitude all read from the header — never hard-coded.

**Exbal Report Trace — flat launch.**
- Launch at the range slope: their printout's zero is a *sight dial*, not a
  bore tilt.
- Drop = y(x) below the launch horizontal (row 0 = 0.00 pins it).
- Path = y(x) − h_s + x·tan(adj): verified exact on the file's own numbers.
- Zero cross-check: our calm pre-run zero angle vs the printed `Sight Adj`,
  asserted ≤ 0.15 MOA (their own drop tolerance is ~0.1 MOA).
- Parse sanity pinned on row 0: Drop 0.00, Path = −h_s.

**Warner sheets — flat, calm, measured atmosphere** (6000 ft, 23.98 inHg
station, 78 °F, 25 % RH); CD-table mode uses the sheet's physical CD(Mach);
BC columns are mapped to fps through the scenario's anchor sound speed (their
BC column is against Mach; the trajectory stays at the anchor altitude, so the
mapping is exact to well under the measurement's density uncertainty).
Muzzle velocity = the `v;0yd` row.

---

## 3. Convention pins (each empirically determined, none assumed)

### 3.1 Wind — the reference's along-wind inversion (F-WIND)

`ReferenceEffectiveFromDeg(from) = (180 − from + 360) % 360`.

The reference's `WindVectorRaw` builds the air-motion vector straight from the
FROM-direction angle: `(v·cos φ, 0, v·sin φ)` in the (downrange, up, LEFT)
shot frame. The crosswind term comes out physically correct, but the along-wind
term carries the FROM sign into the air velocity — a "from φ" line behaves like
air moving *toward* bearing (180 − φ): their from-NNE headwind reduces drag like
a tailwind. Their wind tables were generated with that model, so full-column
acceptance runs at the effective direction.

Pin (`WindConvention_ReferenceAlongWindInverted_Pin`), deviation at the last
row, mirrored (their effective) vs physical:

| Table | ref v | mirrored | physical |
|---|---|---|---|
| `g1_wind.txt` | 1013.6 fps | **0.015 %** | 5.737 % |
| `g1_wind_cold.txt` | 1260.7 fps | **0.049 %** | 2.900 % |
| `g1_wind_hot.txt` | 1519.3 fps | **0.010 %** | 2.019 % |

Asserted: mirrored meets V1; physical deviates > 2× mirrored **and** > 3 m/s.
The crosswind convention is direction-independent and physically correct; the
physical-vs-mirrored lateral gap is only the |v_air| coupling of the along-wind
term, asserted ≤ 6 % + 1 cm. For φ = 90°/270° (pure crosswind) the two
conventions coincide — the Exbal 10 mph @90° table therefore tests the
*physical* wind convention directly, with no mirroring.

### 3.2 Humidity — moist density is the reference treatment (F-HUM)

Density differs ~2 % between moist (vapor-pressure-corrected) and dry air at
100 °F/80 % RH — far above the velocity threshold, so the hot/humid tables
*decide* the reference's humidity convention:

| Table | moist | dry |
|---|---|---|
| `g1_wind_hot.txt` | **0.0105 %** | 0.5530 % |
| `b1_bthp_hot.txt` | **0.2316 %** | 2.0595 % |

`HumidityTreatment_DeterminedByHotHumidTables` asserts moist < dry and the
winner meets V1. Production atmosphere: moist is the default (`Atmosphere.FromMeasurements`).

### 3.3 Interpolation — linear for bundled G-standards

The entire reference ecosystem (JBM, gehtsoft, Exbal) evaluates the G-tables
**linearly** between nodes, and the tables are published as piecewise-linear
data. M2 pin: linear reproduced the golden tables measurably better than PCHIP
in the transonic zone — PCHIP smoothing shifts integrated drag by ~0.3 %, which
shows up as ~1 % drop error at 1000 yd. **Linear** is therefore the bundled
G-standard convention (`InterpolationKind.Linear`); **PCHIP** stays for
user-supplied sparse CD tables where linear chords would cut the transonic rise.
(This supersedes the M1 blanket-PCHIP note in PLAN.md §8.1 for bundled
standards; V0's monotonicity/node-exactness tests cover both kinds.)

---

## 4. Findings (each with its measured evidence)

### F-B1 — the `b1_*` tables do not imply their header's scalar BC

**Symptom.** `b1_bthp*` (header 0.395 G1) and `b1_eldm*` (header 0.351 G7)
carry velocity columns that imply a *curve*, not the printed scalar. A single
constant fitted to the near rows (0.349 / 0.322) holds only near the muzzle and
leaves the far field 0.6–15 % off velocity — cross-validating at the header BC
would measure the header/table inconsistency, not solver accuracy. The most
consistent reading: the tables were generated from a BC(v) input while the
header prints one scalar.

**Procedure (the fit).** One knot per velocity row, air-relative fps axis (the
JBM/Litz "BC vs velocity" convention, PLAN.md §8.1), fitted per file from that
file's own velocity column by a multiplicative loss-ratio iteration:
`BC_i *= (v0 − v_ours)/(v0 − v_ref)` per row per pass, re-zeroing through
`ZeroAngleDeg` every pass; full step for 50 passes, then square-root damping
(`ratio^0.5`). Convergence: ≤ 0.01 % through 750 yd.

**Tail treatment.** Two measured problems below ~1130 fps:
1. *Weak leverage* — the bullet reaches those speeds only in the last 250 yd,
   so the iteration random-walks the last knots.
2. *Far-field wiggle* — the `b1_bthp*`/`b1_bthp_hot` columns carry a non-smooth
   ~2–3 fps oscillation at 800–1000 yd (segment-implied BC 0.21 → 0.19 → 0.193);
   no smooth BC(v) absorbs it, and the undamped fit limit-cycles at
   0.22–0.30 %.

Resolution (deterministic, reproducible): the **last six knots are pooled
non-increasing** by PAVA (pool-adjacent-violators), then **Chebyshev-centered**
by a single 1-D scale search, s ∈ [0.80, 1.20] step 0.005, minimizing the worst
|dV| over rows ≥ 750 yd. Applied scales: `b1_bthp` 0.990, `b1_bthp_hot` 0.995,
`b1_eldm` 1.000.

**Results (worst over the full column, run 4).**

| File | max \|dV\| | max \|dT\| | max \|dDrop\| |
|---|---|---|---|
| `b1_bthp.txt` | 0.268 % | 0.61 ms | 0.0387 m |
| `b1_bthp_7kft.txt` (shares bthp knots) | 0.268 % | 0.61 ms | 0.0555 m |
| `b1_bthp_hot.txt` (own profile) | 0.232 % | 0.72 ms | 0.0464 m |
| `b1_eldm.txt` | 0.048 % | 0.49 ms | 0.0531 m |
| `b1_eldm_10kft.txt` (scalar 0.322) | 0.312 % | 0.60 ms | 0.0673 m |

All ≤ V1 (0.5 % / 1 %) with margin; drops are covered by the F-DROP envelope
(§4.2). `b1_bthp_7kft` reproducing on the same fps-axis curve supports the
fps (not Mach) reading — same bullet, same temperature, lower density.

**Circularity, stated plainly.** The velocity column is circular *by
construction* — it is the fit target — and validates only the fit's
convergence. TOF, drop and windage remain independent checks of the same fitted
runs and pass at full V1 tightness (bthp ≤ 0.72 ms ≈ 0.04 % of the printed
TOF).

**Not a physics claim.** The fitted curves are empirical table reproduction:
`b1_bthp` runs 0.348 @2657 fps → 0.322 @1605 → 0.237 @1213 → a pooled
subsonic tail ≈ 0.187–0.206; `b1_eldm` runs 0.328 → a flat 0.3116 plateau below
~1720 fps. The steep mid-range decline is required by the columns and is not
asserted as the bullet's real aerodynamics. Converged knots live in
`GoldenData.cs` (`BthpKnots` / `BthpHotKnots` / `EldmKnots`) with the
derivation comment; `b1_eldm_10kft` keeps a scalar — its velocity span
(2647 → 1742 fps) is narrow enough that 0.322 stays inside V1 (max 0.31 %).

### F-DROP — far-field drop-column tail (measured envelope)

**Symptom.** The JBM-corpus drop columns (semicolon *and* Exbal Report Trace —
both produced by the same implementation) carry an unexplained far-field tail:
it crosses zero at the zero range and grows ≈ **8.5e-5 m per m** past it.
Measured maxima at 1000 yd: `b1_eldm_10kft` 0.067 m, `gehtsoft_g1_nowind_65`
0.059 m, `g7_nowind` 0.046 m, `g1_wind_hot` 0.048 m.

**Why it is attributed to the reference, not to us.** Velocity and TOF match
the same tables to ≤ 0.03 % (and stay at *full* V1 tightness), the headers
declare Coriolis and spin drift off, and their drift columns read 0.0 — so the
tail cannot be a physical term our solver omits; it lives in the reference's
drop columns.

**Envelope.** `JbmDropTailM(rangeM, zeroYd) = 8.5e-5 · max(0, rangeM −
zeroYd · 0.9144)` — an *additive tolerance term*, not absorbed into the V1
thresholds: velocity/TOF/drift assert at full tightness, and the drop
assertion still fails any deviation the measured artifact cannot explain.
Applied **only** to the JBM semicolon drop column and the `ref_*` drop *and*
path columns. Deliberately **not** applied to `exbal_sierra168.csv` (passes
without it) nor to Warner velocity/TOF (never).

### F-SPIN — spin drift in reference drift/spin columns

- `be_coriolis_*`: their drift column carries the reference's spin drift on top
  of Coriolis although the rifle line declares no twist; the residual over our
  Coriolis-only model grows ~quadratically — ~2 cm by 1100 yd, 9 cm at 2000 yd
  (65 gr probe). Drift is therefore asserted only within V1 range (≤ 1000 m);
  beyond-range rows are printed report-only by the test.
- `gehtsoft_g1_twist_*`: the trailing spin column (present under the declared
  twist) is skipped entirely — spin drift is an M4 item (Litz), PLAN.md §8.
- Drift columns with no signal (calm *and* no latitude) are skipped: nothing to
  assert against zeros.

### F-WTOF — Warner 65 gr sheet's first TOF row

The 100 yd TOF row (0.096 s) is internally inconsistent with the sheet's own
velocity column: 3121.5 → 2970 fps over 91.44 m requires 0.0985 s, i.e. the
printed value implies an average speed *above muzzle velocity*. A printout
anomaly of the producing tool — TOF is asserted only above 100 yd (exactly the
anomalous row is printed report-only); every row above 100 yd meets V1 on both
sheets.

### F-ELEV — elevated-shot tables are flat-fire-equivalent (V1 exclusion)

`g1_nowind_up.txt` and `g1_nowind_up_supersonic.txt` (shot 10°) come from a
flat-fire-equivalent model: their own code comments state TOF/velocity follow
flat fire and drop is the flat drop rotated perpendicular to the line of sight
(≈ flat·cos(shot angle)). A true inclined lob legitimately differs, so
cross-validating here would measure a *model difference*, not solver error.
The exclusion is **audited, not assumed** (`ElevatedShotTables_FlatModelConsistent_ExcludedFromV1`):
velocity flat ≤ 0.3 %, TOF ≤ 0.3 % + the ±0.0005 s print quantum, drop vs
flat·cos(10°) ≤ 2 %, plus a cross-file up-vs-flat pin (velocity/TOF ≤ 0.15 %,
drop ratio = cos 10°). True inclined validation: M3+.

### F-ELDM — published scalars vs the table (model band)

The bullet is the Hornady 147 gr 6.5 mm ELD-M; the AB archive lists it with
**G1 0.697 and G7 0.351** (`config.csv`, B1_baseline_02) — two published
representations of the same bullet. `EldM_PublishedScalars_ModelBand_Recorded`
runs both (`Retarget` renames the file so the F-B1 override lookup misses and
the published scalar is used) and asserts the band directly:

- G1 vs G7 band: velocity max **0.611 % @ 1000 yd**, drop ≤ 0.01 in — this is
  PLAN.md §7's model-discrepancy band for scalar representations.
- Published-scalar vs the table they nominally describe (report-only): G7
  misses by up to **8.13 %**, G1 by **8.74 %** — the table's own rows imply
  ~0.322 G7 (F-B1).

---

## 5. Acceptance summary (run 4, 2026-10-21 — 85/85)

- **Coverage:** bullets 65 / 75 / 123 / 147 / 150 / 168 / 220 / 285 gr; ranges
  50–2000 yd (V1 core 50–1000 m); three drag standards (G1/G5/G7/GI) plus two
  full CD tables and one banded BC(v); weather from 10 °F/5 %/32 inHg to
  100 °F/80 %, sea level to 10 000 ft, calm to 20 mph, Coriolis east/north at
  45° — well over the required "≥ 4 weather conditions".
- **Thresholds:** velocity ≤ 0.5 %, TOF ≤ 1 %, drop/windage ≤ max(1 %, 2 cm) —
  every out-of-tolerance cell fails the suite with full context
  (`DeviationCheck`), so the residuals above are exhaustive, not best-effort.
- **Known measured residuals (itemized, none absorbed into thresholds):**
  the F-B1 velocity floors (§4.1, ≤ 0.312 %), the F-DROP additive envelope
  (≤ 0.070 m at 1000 yd), the F-WTOF single-row skip, F-SPIN range limits,
  F-ELEV exclusion (audited).
- **Model-discrepancy band** (G1/G7/CD representation spread): recorded in
  [`model_discrepancy_band.md`](model_discrepancy_band.md), regenerated by
  `ModelDiscrepancyBand_RepresentationModes` on every run.

## 6. Explicitly excluded from V1

| Item | Reason | Audit |
|---|---|---|
| `g1_nowind_up.txt`, `g1_nowind_up_supersonic.txt` | reference's flat-fire-equivalent incline model (F-ELEV) | dedicated pin test |
| spin columns (`g1_twist`, `g1_wind*`) | spin drift is M4 (Litz) | drift asserted where a real drift signal exists |
| `be_coriolis_*` drift beyond 1000 m | includes reference spin drift (F-SPIN) | printed report-only |
| Warner TOF ≤ 100 yd rows | sheet first-row print anomaly (F-WTOF) | printed report-only |
| `b1_eldm_10kft` from BC(v) fitting | narrow velocity span passes at scalar 0.322 | asserted at V1 like the rest |
| `ab_archive/` B4/B5 | Applied Ballistics archive scope is M3 | B1_baseline used only for F-ELDM |
