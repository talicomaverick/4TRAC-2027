# 4TRAC 2027 — master build prompt

Paste this as the first message (or the system prompt) to a capable coding model. It is
self-contained: it assumes no prior knowledge of the project, and it defines the
acceptance criteria that will be used to audit the result.

---

You are building **4TRAC 2027**, a retrograde ballistic solver for Windows, from an empty
repository. Your output will be audited against published ballistic tables and against
Monte Carlo coverage statistics. Anything you cannot defend must be *reported as
unvalidated*, never presented as working. Read this whole brief before writing code, then
work in the phases in §12 and stop to ask when something is genuinely ambiguous.

## 1. The product

Given a projectile impact point, the impact geometry, and a scene description (location,
atmosphere, wind, weapon and ammunition), the solver runs the trajectory **backwards** and
produces a **probability region** — the area that contains the firing position with (at
least) 50 % and 95 % probability — and draws it on a map.

It must never present a single point as "the answer". The output is a region plus an
itemized account of every assumption, exclusion and deviation that produced it.

## 2. Non-negotiable principles

1. **Region, not point.** The primary output is an HDR (highest-density region) polygon
   pair (50 %, 95 %). A point estimate may be shown only as an annotation inside it.
2. **Itemize, never absorb.** Every excluded cell, clamped parameter, skipped criterion,
   assumption and tolerance breach gets an explicit line in the report. Silent smoothing,
   silent truncation and silent fallbacks are defects.
3. **Label, never smooth over.** Partial, uncertain or out-of-envelope results carry a
   visible label (e.g. `LOW-MACH`, `OUTSIDE VALIDATED ENVELOPE`, `SKIP: not evaluable`).
   A `SKIP` is not a `PASS` and not a `FAIL`; it must state why it could not be evaluated.
4. **Determinism.** Same seed + same inputs = same output, on any machine and any core
   count. A seeded PRNG (e.g. xoshiro256\*\*), untruncated normals, deterministic cell
   ordering, and the seed printed in every report.
5. **SI internally.** Metres, kilograms, seconds, radians, kelvin, pascals. Imperial units
   exist only in the profile/test-data boundary and in the UI.
6. **No unsupported claims.** If a criterion cannot be evaluated at the precision the
   physics allows, it is `SKIP` with the information-theoretic reason — never a fabricated
   pass, never a red `FAIL` that implies the model is wrong.
7. **Tests are the contract.** A milestone is done when its tests actually execute the
   production code path. A test that compares files without running the program is a
   false pass and is worse than no test.

## 3. Deliverable and architecture

- .NET 8, WPF desktop app, Windows 10/11 x64, **single-file self-contained** executable,
  fully offline (no network, no telemetry).
- Projects, strictly layered:
  - `Core` — physics, numerics, statistics. No UI or file-system dependencies.
  - `Validation` — headless CLI harness that runs the golden tables and Monte Carlo
    matrices and writes reports.
  - `App` — WPF shell: scene input, map with the HDR regions, deviation panel, export.
  - `Tests` — golden-data tests, numerical-convergence tests, end-to-end resume tests.
- Bullet/rifle/scene definitions are JSON documents with a documented schema and version
  field. Unknown fields are an error, not silently ignored.

## 4. Forward physics (required model)

- Flat-earth point-mass model inside the validated envelope; state the envelope in the UI
  and the report. Gravity `g = 9.80665 m/s²`.
- Moist-air density as a function of temperature, station pressure and relative humidity;
  document the formula and its validity range.
- Drag: standard G1 and G7 drag functions with ballistic coefficient, a `BC(v)` law, and
  user-supplied `CD(Mach)` tables. Interpolate linearly between standard drag-function
  nodes. Use monotone cubic (PCHIP) **only** for sparse user tables; never smooth dense
  published tables.
- Coriolis enabled on the production path; document the frame (ECEF) and sign convention.
- Production integrator: fixed-step RK4, step size chosen by a documented rule. Ship an
  independent RK45 (Dormand–Prince) reference integrator and a convergence test proving
  RK4 is within a stated tolerance of it over the whole envelope.
- Declare everything you did **not** model (spin drift, Magnus, Magnus moment, wind
  gradients). This list is a report line item, not a footnote.
- Ballistic validity floor: Mach ≥ 0.2. Cells below the published 400 fps (121.92 m/s)
  bound are flagged `LOW-MACH`: they still validate the solver (truth and inverse share
  the same curve) but they do **not** validate the bullet model. Say so in the report.
- Angle conventions: exactly one launch-elevation convention, documented, matching
  JBM/BE within 0.15 MOA when cross-checked against Exbal's FLAT mode. Never mix
  conventions between the truth solve and the inverse solve. Report the **exterior
  (impact) angle** as the primary angle and provide the reference-frame identity
  `ReferenceEffectiveFromDeg(from) = (180 − from + 360) % 360`, with a unit test.
  `1 MOA = 2.908882e-4 rad`.

## 5. Inverse problem

- Integrate the **same equations of motion backwards** with a negative step from the
  impact point. Do not invert the model with a separate approximation.
- Resolve the wind **once** into the ECEF frame before the run; never re-derive it per
  step.
- Measure observables in an **impact-anchored** local ENU frame; report the residual of
  every observable you match.
- Firing point = **first crossing of the origin plane** (the plane through the true firing
  point normal to the horizontal line of sight) with ENU vertical velocity **> 0**; the
  sign test rejects the descending branch. Guard against spurious crossings and report if
  more than one crossing occurs.
- Muzzle velocity is a **prior**, not a free parameter: root-find the muzzle velocity that
  reproduces the impact observables, using a bracketed Illinois method. This makes the
  Monte Carlo weights identically 1 — state that explicitly in the report.

## 6. Uncertainty to HDR region

- Draw measurement errors per the declared sigmas (launch angle, muzzle velocity, BC, and
  wind per axis), run the inverse per draw, and keep the resulting origin cloud.
- Density estimate: 2-D Gaussian KDE with Scott's rule, `h = σ̂ · n^(−1/6)`.
- Contours: rank cells by mass and take the smallest set reaching the target mass — do
  **not** use fixed density thresholds — then extract the polygon with marching squares.
- **Calibration (this is the subtle part):** the target mass must be inverted through the
  exact Gaussian-ellipse coverage integral over the **eigen-axes of the cloud
  covariance** (Abramowitz & Stegun 7.1.26 `erf`, bisection on the mass). A single
  pooled-sigma `k` under-covers elongated clouds; if you implement `k = const` you will
  pass narrow clouds and fail long ones. Report the achieved coverage of the generated
  region, not just the nominal one.

## 7. Validation protocol

**V1 — deterministic golden tables.**
- Forward trajectories against JBM/BE/Exbal at 1000 yd (914.4 m) and Warner Doppler radar
  data at 1500 yd (~1372 m). Use a tolerance table per quantity (drop, windage, TOF,
  velocity) rather than one blanket number.
- Compare Warner TOF only beyond 100 yd; closer in, instrumentation error dominates.
- Every check produces PASS/FAIL with the numeric delta. No "approximately correct".

**V2 — Monte Carlo matrix.**
- Grid over horizontal range × launch elevation (e.g. 200 × 250 points), sampled with a
  stride coprime to the grid dimensions so the sample is not aliased to the grid.
- Typical weight: 40 trials × 80 hypotheses per cell, fixed seed.
- Gates:
  - 95 % region coverage ∈ [92 %, 98 %]
  - 50 % region coverage ∈ [45 %, 55 %]
  - per-cell median origin error ≤ max(5 m, 1.5 % of range), evaluated only for
    launch elevation ≥ 15° and range ≤ 1000 m
- The error gate is evaluated only on cells whose scene measurements meet the
  **achievable protocol**: σθ 0.25°, MV half-width ±1 m/s, BC 3 %, σwind 0.5 m/s per
  axis. If the 7.5 m budget cannot be met at nominal precision (a ±0.5° inclinometer at
  500 m / 15° is ~15 m of information limit), the criterion is **SKIP**, with the
  information bound quoted — never FAIL, never hidden.
- An `anchors` mode evaluates the origin-error gate alone and SKIPs coverage **by
  design** (a tight-prior subset over-covers by construction).
- Excluded cells are itemized with reasons and counted in an invariant:
  `completed + excluded = total`. Never drop a cell silently.

**Sensitivity / precision publication.**
Publish the scene-measurement precision required to support the origin-error promise.
Reproduce the qualitative result that **muzzle-velocity precision dominates the region
area** (about 614 m² at ±1 m/s versus 4281 m² at ±5 m/s, with BC 3 % not binding).

**Reports.**
Markdown plus CSV. Every criterion is one row: name, verdict (PASS/FAIL/SKIP), the
measured value, the threshold, and the reason for any SKIP. Include run configuration,
seed, parallelism, elapsed time, and the full deviation list.

## 8. Engineering requirements that are part of the science

- **Checkpoint/resume.** Append-only per-cell CSV inside the output directory; one writer
  only; tolerate a torn final line (power loss) and re-run that cell; reject a checkpoint
  produced by a different matrix/seed; `""` in CSV means NaN and must round-trip as NaN,
  never as 0.
- **Resume must be byte-identical** to an uninterrupted run. Test it by actually invoking
  the program's entry point twice, not by comparing files.
- **Aggregation must tolerate resumed cells.** A resumed run returning no in-memory
  `CellOutcome` for already-finished cells must not crash the final report — enforce the
  invariant `completed + excluded = total` instead.
- **Artifacts are written once, at the end** (that is why checkpoints exist). A crashed run
  must leave the previous artifacts untouched, not half-overwritten.
- **Watchdog.** A supervisor that reports state, restarts the harness from the checkpoint,
  warns (not kills) on stalls, aborts on a crash-loop, and never deletes results. Exit
  codes: 0 done, 1 running, 2 stopped, 3 stalled.
- Never run two instances against one output directory.

## 9. UI (M4/M5)

- Map with the 50 % and 95 % regions, the point estimate as an annotation only, scale bar,
  north arrow, manual declination input (never guess magnetic declination).
- Scene editor with units shown in the user's system, stored in SI.
- A "deviations and assumptions" panel listing every item from the report, always
  reachable in one click from the map.
- Explicit banner when the scene is outside the validated envelope or when a criterion was
  SKIPped.

## 10. Traps that have already cost real time

- Cell cost is wildly non-uniform (short-range cells are milliseconds, long-range
  high-angle cells are tens of minutes). Never extrapolate an ETA from early progress.
- Launch-angle convention mismatches produce plausible-but-wrong answers that still pass
  eyeball checks. Pin the convention and test it against a second solver.
- Symmetric priors centred on truth over-cover tight-prior subsets. Coverage is a
  *whole-matrix* criterion; do not apply it to anchor-only runs.
- KDE bandwidth errors look like physics errors. Calibrate mass, and check coverage on an
  elongated synthetic cloud before trusting the contour code.
- Do not tune physics constants to make goldens match. If a golden disagrees, itemize the
  disagreement and explain it.

## 11. Definition of done

- V1 golden suite passes within tolerance, or every breach is itemized with a reason.
- V2 runs produce the three gate verdicts under the stated SKIP rules, reproducible from
  the seed on a different machine and core count.
- The single-file executable runs on a clean Windows 10/11 x64 machine with no runtime
  installed.
- Every report contains: configuration, seed, verdicts, exclusions, deviations, and the
  published precision requirements.

## 12. Working method

- Phase the work: M1 forward solver + goldens; M2 inverse solver + goldens; M3 uncertainty,
  HDR and coverage calibration; M4 WPF app and map; M5 packaging and reports.
- Each phase ends with tests that run the real entry point, plus a short written record of
  declared choices.
- Ambiguity: state it, choose the most defensible interpretation, record it as a
  **declared choice**, and continue. Do not invent numbers to fill gaps.
- Progress reports use numbers, not adjectives.
- If you cannot finish everything, deliver a smaller honest system: a working forward
  solver with golden tests and an explicitly unimplemented inverse solver beats a complete
  demo that fabricates its regions.
