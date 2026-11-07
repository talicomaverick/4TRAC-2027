# Third-party notices

4TRAC is released under the GNU General Public License v3.0. It uses the components below, each under its own licence. **Licence names here are the author's understanding and must be checked against each package's current licence file before a release.**

## NuGet packages (see `src/4TRAC.App/4TRAC.App.csproj` and `dotnet list package --include-transitive`)

| Package | Use | Licence (verify) |
|---|---|---|
| Mapsui, Mapsui.Tiling, Mapsui.Wpf 5.1.0 | Map control and tiles | MIT |
| SkiaSharp / SkiaSharp.Views.WPF, HarfBuzzSharp | Map rendering | MIT |
| OpenTK, OpenTK.GLWpfControl | Transitive dependency of the map control | MIT |
| xUnit | Tests only | Apache-2.0 |

## Data

- **Map tiles:** © OpenStreetMap contributors (ODbL). Tiles are fetched from the public OpenStreetMap tile server; keep the attribution visible in the map and follow the [tile usage policy](https://operations.osmfoundation.org/policies/tiles/). Heavy or commercial use needs a different tile provider.
- **SRTM elevation:** NASA SRTM, downloaded on demand from AWS open data (public domain / open data terms; confirm current terms).
- **Calibre and bullet catalogue (`data/calibre_database.csv`):** nominal catalogue figures. Some ballistic coefficients are marked as unverified estimates. Confirm provenance before redistribution.
- **Reference ballistic tables (`data/golden`):** produced with or derived from JBM, Ballistics Explorer, Exbal and Warner Doppler data, and from the test corpus of the `goesoft-usa/BallisticCalculator1` project (LGPL-2.1). See `data/golden/README.md`. **Confirm that these files may be redistributed**, or replace them with data you can publish, before release.
- **G1/G7 drag tables:** standard published reference functions.
