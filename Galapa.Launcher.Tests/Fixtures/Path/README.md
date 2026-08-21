# Path rendering fixtures

These golden images exercise the production `ThemePart` renderer on the CPU
Skia backend. They cover every compiled `corner` value at a normal radius and
again with a radius larger than the control, where the renderer must clamp it
to half the short edge.

The fixture uses a dark background, blue fill, and yellow two-pixel border so
the cutout, antialiasing, and border silhouette are all visible. The tests also
assert symmetry, radius sensitivity, radius clamping, zero-radius degeneration,
and that all five corner vocabularies remain visually distinct.

To deliberately regenerate the approved PNGs:

```powershell
$env:GALAPA_UPDATE_RENDER_FIXTURES = '1'
dotnet test Galapa.Launcher.Tests --filter FullyQualifiedName~PathRenderingTests
Remove-Item Env:GALAPA_UPDATE_RENDER_FIXTURES
```

Always inspect the regenerated images before accepting them.
