# yako-screenshot

A Windows region-screenshot tool for people who paste into Figma.

Windows UI scaling makes ordinary screenshots the wrong size: on a 200% display, a button
that is 80 pt in the design occupies 160 physical pixels on screen, so a normal screenshot
pastes into Figma at 160 px and no longer matches the design. This tool captures true
physical pixels and hands Figma the size information separately, so a paste measures what
the design measures **without** throwing away any pixels.

## Use

Run `Yako.Screenshot.exe`. It sits in the tray and waits.

| Action | |
|---|---|
| **Ctrl+PrtScn** | freeze the screen and start selecting (also: tray menu → *Capture now*, or double-click the tray icon) |
| **drag** | pick an area; the rest of the screen stays dimmed |
| **handles** | drag any of the 8 handles to adjust; drag inside to move; drag a handle past the opposite edge to flip |
| **arrows** | nudge by 1 px, **Shift+arrows** by 10 px, **Ctrl+arrows** resize from the bottom-right |
| **Copy for Figma** / **Ctrl+Shift+C** | pastes into Figma at 100%, ignoring the Windows UI scale |
| **Copy** / **Enter** / **Ctrl+C** | a plain bitmap with every captured pixel |
| **Save…** / **Ctrl+S** | write a PNG or JPEG file |
| **Esc** | cancel the whole operation |
| **right-click** | drop the current selection and start over |

Hover any button for a reminder of what it does.

The badge at the edge of the selection shows two numbers: the **captured pixels** (what
*Copy* and *Save* write out) and, below it, the **logical size** at `@1×` (what *Copy for
Figma* lands at). At 200% scaling a 700 × 400 drag reads `700 × 400` and `350 × 200 @1×`.

Preferences live in `%APPDATA%\Yako\settings.json`.

## Nothing is ever resampled

Every output keeps all captured pixels. That is deliberate, and it took a wrong turn to
learn why.

The first version resampled captures down by the DPI scale so they would paste at the right
size. It worked, and it looked soft — noticeably worse than the same shot taken with
Lightshot and scaled down inside Figma. The reason is arithmetic, not filtering:

- What is on screen is 700 × 400 real pixels: **280,000 samples**.
- A 350 × 200 image is **70,000 samples** - three quarters discarded.
- To show that image at the same physical size, something must paint 700 × 400 device
  pixels from 70,000 samples. It invents the missing 210,000 by interpolation. That
  invention is the blur.

No filter can escape that deficit. Measured on a text-heavy 700 × 400 region at 200%,
scoring each candidate as Figma actually displays it (mean gradient magnitude, and error
against the native pixels):

| filter | as displayed | error vs native |
|---|---|---|
| **native pixels, no resampling** | **6.04** | **0.00** |
| GDI+ HighQualityBicubic | 5.53 | 7.36 |
| box 2×2 average | 5.31 | 6.96 |
| Lanczos-3, sRGB | 5.47 | 7.25 |
| Lanczos-3, linear light | 5.65 | 7.76 |

Every resampler lands within a few percent of the others and all of them lose to keeping
the pixels, so the resampling path was removed rather than tuned.

## How Copy for Figma works

Size and resolution are separate things, and on a 200% display they differ by exactly 2×. A
plain bitmap on the clipboard can only say *"I am N pixels wide"*. An SVG can say *"draw
this N-pixel raster into an M-wide box"* - which carries both:

```
<svg width="350" height="200" viewBox="0 0 350 200">
  <image width="350" height="200" xlink:href="data:image/png;base64,(700x400 png)"/>
</svg>
```

Native pixels, logical size, one paste, nothing resampled anywhere.

Deliberately, **only text formats go on the clipboard** for this button - no bitmap. With a
bitmap present, apps take the bitmap and the size declaration is ignored, which defeats the
whole purpose. The trade-off is that this button only works in Figma: pasting into an app
that wants a picture (Paint, Word) will paste SVG markup as text instead. Use plain **Copy**
for those.

## Build

```
dotnet build Yako.Screenshot.csproj
dotnet run --project Yako.Screenshot.csproj
```

A headless check of the pixel invariants:

```
bin\Debug\net9.0-windows\Yako.Screenshot.exe --selftest
```

It verifies that the process really is per-monitor-V2 DPI aware, that the captured bitmap
matches the physical virtual-desktop size, that a crop keeps every pixel, that the logical
size divides the UI scale out correctly, that a clipboard bitmap round-trips at native size,
and that the SVG payload declares the logical size around a native raster while carrying no
bitmap format. Report goes to `%TEMP%\yako-selftest.txt`; exits non-zero on failure.

To ship a single self-contained exe:

```
dotnet publish Yako.Screenshot.csproj -c Release -r win-x64 --self-contained ^
  -p:PublishSingleFile=true -o publish
```

## How it works

- **`app.manifest`** declares per-monitor-V2 DPI awareness. This is load bearing: without it
  Windows virtualises `GetSystemMetrics` and `CopyFromScreen`, and the physical pixels the
  whole tool depends on never reach us.
- **`ScreenCapture`** grabs the bounding box of every display in one shot and records each
  monitor's bounds and DPI scale. The virtual origin is often negative (a display left of
  the primary one), so every coordinate downstream is relative to that origin.
- **`DpiScaler`** crops without resampling, and computes the logical size the SVG declares.
- **`OverlayForm`** is one borderless window over the whole virtual desktop with **no child
  controls** - WinForms would DPI-scale them per monitor. Every piece of chrome is drawn and
  hit-tested by hand in device pixels, sized by the scale of the display it sits on, so the
  UI looks physically identical on a 100% and a 200% monitor.
- **`FigmaGlyph` / `Glyphs`** draw the toolbar icons from GDI+ paths, so there are no binary
  assets and the icons stay crisp at any scale. Note the Figma mark is Figma's trademark:
  fine privately, but their brand guidelines do not permit uses implying endorsement, so
  swap it for a text label before distributing this to anyone else.
- **`HotkeyListener`** prefers `RegisterHotKey`; if another app owns Ctrl+PrtScn it falls
  back to a low-level keyboard hook and says so, so the app is never silently deaf.
- **`SingleInstance`** makes a second launch visible instead of vanishing, offering to
  capture straight away.

## Known limits

- A selection spanning two displays with different scale factors uses the scale of the
  display covering most of it - there is no single correct answer, so it stays predictable.
- **Copy for Figma** pastes as text outside Figma, by design (see above).
- DRM-protected windows capture as black - a platform restriction.
- The mouse cursor is not drawn into the capture.
