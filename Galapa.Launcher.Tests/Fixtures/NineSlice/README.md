# Nine-slice rendering fixtures

`diagnostic.9.svg` gives every slice a unique hue and an asymmetric four-quadrant pattern. The pattern makes swapped, mirrored, stretched, clipped, and repeated cells visible in a pixel diff. Its frame also declares a two-pixel outset and four-pixel content inset.

The tests render through Avalonia's CPU Skia backend at 96 DPI and compare decoded RGBA pixels exactly. A failure writes the actual image and a magenta-highlighted diff to `Galapa.Launcher.Tests/TestResults/NineSlice`.

To regenerate the approved fixtures in PowerShell after an intentional renderer change:

```powershell
$env:GALAPA_UPDATE_RENDER_FIXTURES = '1'
dotnet test Galapa.Launcher.Tests/Galapa.Launcher.Tests.csproj --filter 'FullyQualifiedName~NineSliceRenderingTests'
Remove-Item Env:GALAPA_UPDATE_RENDER_FIXTURES
```

Review every changed PNG before accepting it, then run the tests again without the update variable.
