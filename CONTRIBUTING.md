# Contributing to 4TRAC

Thank you for helping. 4TRAC is meant to be tested hard, so the bar for a change is evidence, not opinion.

## Ground rules

- **Label, never smooth over.** A criterion that cannot be evaluated is reported as `SKIP`, not PASS. Rejected hypotheses are counted and reported, never clamped or dropped silently.
- **Declare assumptions.** Anything the model assumes goes into the result notes, `LIMITATIONS.md` and the case report.
- **Determinism.** Same seed and inputs must give identical output. Do not add unseeded randomness.
- **Do not change the physics or the priors without validation.** A change that can move results needs a before/after validation run (below).

## Setup

1. Install the .NET SDK (targets `net8.0`) on Windows.
2. `tools\run-tests.cmd` — all tests must pass before and after your change.
3. `tools\build-all.cmd` — tests plus a published app.

## What a pull request should include

- A clear description of what changed and why.
- Tests for new behaviour, including the failure case.
- If the change touches the solver, region calculation, priors or drag: a validation run (`tools\run-validation.cmd <suite> <trials> <hypotheses>`) with the report attached, and any change to the coverage numbers explained.
- Updates to `USER_MANUAL.md` and `LIMITATIONS.md` when behaviour or limits change.

## Reporting a bug

Include the inputs (or a saved session), the seed, the app version, what you expected and what you got. For a wrong region, say how you know the true origin.

## Test data

Use invented or anonymised scenes only. Do not add coordinates, names or other details of real incidents or people to tests, docs or examples.

## Licence

By contributing you agree your work is released under the GNU General Public License v3.0 (see `LICENSE`).
