# Build notes

## Environment

* .NET SDK **10.0.302** pinned via `global.json` (`rollForward: latestPatch`).
* All package versions live in `Directory.Packages.props`
  (`ManagePackageVersionsCentrally=true`), so individual csproj files list
  only package IDs, not versions.
* Avalonia **11.2.8**.

## Machine-specific gotchas hit during development

* **Machine-local NuGet source.** The Windows box has a package source
  defined at machine level pointing to `D:\Program Files (x86)\...` that
  isn't present on the Linux build box. `NuGet.config` in the repo root
  contains `<clear/>` before adding `nuget.org`, so `dotnet restore` on
  either box only sees the official feed.
* **`AllowUnsafeBlocks`.** `WarajevoNext.App` uses a small `unsafe`
  `Buffer.MemoryCopy` when blitting the ULA framebuffer into the Avalonia
  `WriteableBitmap`; the App csproj enables `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`.
* **`Tmds.DBus.Protocol 0.20.0` NU1903 warning.** Emitted transitively via
  Avalonia's Linux support. Not a runtime problem for a locally-hosted
  emulator; will be quietly resolved when Avalonia updates the pin.

## Verified on the Linux build box

```
$ dotnet --info | head -3
.NET SDK
 Version:           10.0.302
 Commit:            ...
$ dotnet build     → 0 errors, 3 harmless warnings (unused AY envelope state, NU1903)
$ dotnet test      → 1335 FUSE + 6 machine smoke = all pass
$ dotnet run --project src/WarajevoNext.App -- --selftest 500
  selftest ok: frames=500 tstates=34944000 pc=0x????
```
