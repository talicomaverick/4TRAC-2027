# Model discrepancy band (PLAN.md §7 V1)

The G1 / G7 / CD-table *representation spread* for the same bullet is a model
choice, not solver error — PLAN.md §7 V1 requires it to be **recorded as a band,
not silently absorbed**. This document is transcribed from
`model_discrepancy_band_report.txt`, which is regenerated on every test run by
`ForwardGoldenTests.ModelDiscrepancyBand_RepresentationModes` (run 4,
2026-10-21). Each section also asserts the band ≤ 2 % (velocity) and the
representation run ≤ V1 against the sheet it claims to reproduce.

## 1. Warner Doppler sheets — CD table vs BC(v)-G1 vs BC(v)-G7 vs the sheet

Same measured bullet expressed three ways: the sheet's physical CD(Mach) table,
and its multi-BC column re-expressed as BC(v) against G1 and against G7.

### warner_65_flatline.txt (6.5 mm Flatline, 123 gr)
| point | CD-table | BC(v)-G1 | BC(v)-G7 | sheet | band |
|---|---|---|---|---|---|
| v @ 500 yd | 2538.5 fps | 2542.3 fps | 2545.2 fps | 2542.2 fps | **0.262 %** |
| v @ 1000 yd | 2016.7 fps | 2020.0 fps | 2022.9 fps | 2019.3 fps | **0.307 %** |
| v @ 1500 yd | 1552.9 fps | 1554.2 fps | 1558.3 fps | 1554.0 fps | **0.343 %** |

### warner_338_flatline.txt (.338 Flatline, 285 gr)
| point | CD-table | BC(v)-G1 | BC(v)-G7 | sheet | band |
|---|---|---|---|---|---|
| v @ 500 yd | 2689.4 fps | 2692.7 fps | 2694.4 fps | 2695.9 fps | **0.187 %** |
| v @ 1000 yd | 2328.9 fps | 2331.7 fps | 2332.8 fps | 2333.5 fps | **0.165 %** |
| v @ 1500 yd | 1989.9 fps | 1992.2 fps | 1993.7 fps | 1994.6 fps | **0.193 %** |

**Reading:** for a Doppler-measured bullet the three representations agree to
0.17–0.34 % in velocity over 1500 yd — the CD-table run tracks the sheet itself
closest at long range on the .338, while G7 sits between CD and G1 everywhere.
This sub-percent spread *is* the representation band a production user inherits
when choosing G1 vs G7 vs a CD table.

## 2. b1_eldm.txt — scalar G7 0.351 vs scalar G1 0.697 (same published bullet)

(F-B1: the table's own rows imply ~0.322 G7; the fitted run is included for
comparison.)

| point | G7 0.351 | G1 0.697 | band | drop band | fitted-0.322 vs sheet |
|---|---|---|---|---|---|
| @ 500 yd | 2086.5 fps | 2085.5 fps | **0.049 %** | 0.00 in | 2026.5 fps vs 2026.5 fps |
| @ 1000 yd | 1556.1 fps | 1564.9 fps | **0.611 %** | 0.01 in | 1439.2 fps vs 1439.1 fps |

**Reading:** the two *published* scalars of the Hornady 147 gr ELD-M diverge by
up to 0.61 % in velocity at 1000 yd — a band of the published representations
themselves, asserted in `EldM_PublishedScalars_ModelBand_Recorded`. Neither
scalar describes the table: both miss its velocity column by 8.13 % (G7) /
8.74 % (G1) at worst, while the F-B1-fitted curve reproduces it to 0.048 %
(README §4.1). The published-vs-table gap is a provenance finding about the
table's generation input, not a solver discrepancy.

## 3. exbal_sierra168 — banded BC(v) vs single-BC 0.447 control vs the printout

Exbal's published banded G1 BC (0.405 / 0.424 / 0.447 / 0.462 across velocity
bands) vs the same bullet at one flat 0.447 scalar:

| point | banded | single 0.447 | printout | band |
|---|---|---|---|---|
| v @ 500 yd | 1664.0 fps | 1684.7 fps | 1663.0 fps | **1.245 %** |
| v @ 1000 yd | 1064.8 fps | 1106.4 fps | 1069.0 fps | **3.893 %** |

**Reading:** banding is material — their own published control shows a single
BC is ~2.3 % optimistic in velocity at 1000 yd (asserted ≥ 1.5 % so the story
cannot go moot). The banded run meets V1 against the printout; the single-BC
control quantifies what *not* banding would cost a user. This is the largest
band in the set and the reason `DragMode.BcVelocity` exists next to scalar BC.

---

**Scope note:** these bands are the spread between *representations available
to the user for the same bullet*. They are distinct from (a) solver error vs a
reference (V1 thresholds) and (b) the measured reference-artifact itemizations
in README §4 (F-B1, F-DROP, F-WTOF) — three different things, never mixed.
