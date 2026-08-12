# Colorist — Jellyfin plugin

Samples colour across a video with ffmpeg, reduces each sampled frame to one
representative colour, and stores that sequence of colours beside the media. The
detail page draws it as a vertical-stripe barcode across the foot of the page.

**The stored artefact is colour data, not an image** (`<stem>-colorist.json`,
`{"v":1,"colors":"<hex triplets>"}`). Everything downstream of sampling — stripe
width, blending, display height — is a draw-time decision, so changing any of them
costs a page reload rather than another ffmpeg pass over the library. A PNG can be
written alongside it (`WriteImageSidecar`, off by default) for other software to
open; nothing in the plugin reads it.

**Scope discipline:** Colorist samples colour and draws a strip. It is not a general
image-processing plugin and does not manage artwork — it deliberately writes to a
sidecar name Jellyfin's scanner ignores rather than setting any `ImageType`. Reject
feature requests that amount to "also generate posters".

## Development commands

The .NET 9 SDK is installed per-user and is **not on `PATH` by default**:

```bash
export PATH="$HOME/.dotnet:$PATH"     # required first, in every shell

dotnet build Jellyfin.Plugin.Colorist.sln -c Release
dotnet test  Jellyfin.Plugin.Colorist.sln -c Release    # 185 tests, no network, no ffmpeg
./build/package.sh                                       # artifacts/Colorist_<version>/
VERSION=0.2.0.0 CHANGELOG="..." ./build/release.sh       # zip + manifest.json entry
```

Target framework is **net9.0** — Jellyfin 10.11.x runs on .NET 9, *not* .NET 8.
Build treats warnings as errors.

## Releasing

`build/release.sh` builds the zip (plugin files at zip **root**), computes the MD5
Jellyfin verifies on install, and inserts the version into `manifest.json`. Then
create a GitHub release tagged `v<VERSION>` and upload that exact zip — rebuilding or
re-zipping changes the checksum and breaks catalogue installs. Users add
`https://raw.githubusercontent.com/nitramivel/jellyfin-colorist/main/manifest.json`
as a plugin repository.

## Architecture

```text
Core/          pure, no I/O, no Jellyfin types — all of it unit tested
  Color/       Oklab, the three strategies, cluster scoring, the factory
  Sampling/    sample planning, ffmpeg argument construction, crop and pts parsing, binning
  Imaging/     PNG writer (ZLibStream + CRC32), barcode composer — only for the optional PNG
  BarcodeData  the stored format: pack, unpack, the JSON envelope
  SidecarPaths naming and location rules, one extension per artefact
Services/      everything that touches a process, a file or Jellyfin
  FfmpegRunner        process handling, below-normal priority
  FrameSampler        drives ffmpeg, turns rgb24 into colours
  BarcodeStore        sidecar write with data-directory fallback
  BarcodeService      orchestration, eligibility, crop resolution
  GenerateBarcodesTask  IScheduledTask, the only bulk path
  DeleteBarcodesTask    IScheduledTask, no default triggers, the only bulk removal
  Web/ScriptInjector  IStartupFilter middleware, patches index.html
Api/           ColoristController
Web/           colorist.js, embedded
```

**The Core/Services split is load-bearing.** There is no Jellyfin server and no
ffmpeg on the dev machine, so everything that can be decided without them is decided
in `Core` and tested there — including the ffmpeg command lines, which are built as
pure strings and asserted on. What is left unverified is genuinely only process
execution and DOM manipulation.

## Decisions worth not relitigating

**Enums reach the settings page as names, not numbers.** Verified by reflecting over
`Jellyfin.Extensions.Json.JsonDefaults.Options`, which registers `JsonGuidConverter`,
`JsonFlagEnumConverterFactory`, `JsonDefaultStringEnumConverterFactory`,
`JsonStringEnumConverter` and friends — so `CropMode` serialises as `"Auto"`. Property
names *are* PascalCase (`PropertyNamingPolicy` is null), so `config.CropMode` is
correct; it was only the value that was wrong. The `option` values in `configPage.html`
must be the enum member names. Reading is tolerant — the server accepts `"Fixed"`, `2`
and `"2"` alike — so only the page's outgoing value ever needed pinning.

**Changelog text in `manifest.json` is rendered as HTML by the dashboard.** Anything
in angle brackets is parsed as a tag: `<video filename>-colorist.json` opened a video
element and swallowed the rest of the entry, which is why 0.2.0.0 showed a large blank
space in the catalogue. Write changelogs as plain prose with no angle brackets.

**Deleting is scoped by configuration, not by the include switches.**
`DeleteBarcodesTask` enumerates via `BarcodeService.GetAllItems`, which ignores
`IncludeMovies`/`IncludeEpisodes` — those say what a *generation* run builds, and
letting them gate a delete would strand thousands of files because a switch was
flipped afterwards. Scope (everything vs. images only) rides in `DeleteImagesOnly`
because a scheduled task takes no arguments; the settings page saves the
configuration before queueing so the checkbox next to the button is what runs. The
task has no default triggers on purpose.

**The client draws the strip; the server does not.** `colorist.js` paints a canvas at
one pixel per stripe for hard edges, or a `linear-gradient(to right in oklab, …)` for
blended ones. The `in oklab` is the whole reason a gradient is acceptable here —
browsers interpolate perceptually and reproduce what `BarcodeComposer.Interpolated`
does server-side, without a second copy of the Oklab maths in JavaScript. Browsers
predating it (Chrome 111, Safari 16.2, Firefox 128) reject the declaration outright,
which leaves `style.backgroundImage` empty and is how the sRGB fallback is detected.
Do not "simplify" this to a plain gradient: sRGB interpolation puts a dark seam at
every transition, which is the bug Oklab is there to prevent.

**No imaging library.** `Jellyfin.Controller` does not carry SkiaSharp — it lives in
the server's `Jellyfin.Drawing.Skia`, which is not published for plugins. Adding it
would mean shipping native `libSkiaSharp` per architecture into a process that
already has its own copy. Instead ffmpeg emits `rgb24` raw frames and `PngWriter`
writes the file in about eighty lines. PNG mandates the zlib wrapper that
`ZLibStream` produces; `DeflateStream` would emit a raw stream every decoder rejects.

**Middleware, not the File Transformation plugin,** for injecting the client script.
No second plugin to depend on, no cross-plugin contract, no ordering agreement. Same
conclusion Concierge reached, and the mechanism Jellyfin Enhanced uses.

**Below-normal ffmpeg priority instead of transcode-aware backoff.** Verified against
the 10.11 assemblies: `ITranscodeManager` can only look a job up by play session ID,
and `ISessionManager.GetSessions` needs a user context a scheduled task lacks. There
is no API to poll, so the OS scheduler gets the job.

**`ImageType` is a closed enum** on 10.11 (`Primary, Art, Backdrop, Banner, Logo,
Thumb, Disc, Box, Screenshot, Menu, Chapter, BoxRear, Profile`). A custom image type
was never an option. Colorist writes sidecars and serves them over its own endpoint
instead.

**There is no `Policies.DefaultAuthorization`** in this version. Endpoints readable by
any signed-in viewer use bare `[Authorize]`.

## Verifying API surfaces

Do not recall Jellyfin signatures from memory — they shift between majors, and 12.0
is coming. The real 10.11.11 assemblies are in `~/.nuget/packages/jellyfin.controller`
and `jellyfin.model`. Build a throwaway console project referencing both and reflect
over the types. That is how every interface used here was confirmed.

## Unverified

Nothing in this repository has run against a live Jellyfin server, and there is no
ffmpeg on the development machine. Specifically unverified:

- **`FfmpegRunner` and the live ffmpeg invocations.** Argument *construction* is
  tested; execution is not. The deadlock risk is real and handled — stderr is drained
  concurrently, because `showinfo` emits one line per frame and a full stderr pipe
  would block ffmpeg forever on longer files.
- **`colorist.js` against a real client.** Jellyfin Web has no supported detail-page
  extension point and no stable DOM contract. Anchor selectors have fallbacks and the
  script no-ops when they all miss, but expect this file to need adjustment. It must
  never touch a node it did not create.

  Its *rendering* has now been exercised against a browser, using a throwaway page
  that mimics the 10.11 layout (`.page` with `padding-bottom: 5em`,
  `.detailPageContent` with a `vw` indent) and a stubbed `ApiClient`. That confirmed,
  from identical colour data: hard mode paints a canvas reproducing every input
  colour exactly and in order; blended mode emits
  `linear-gradient(to right in oklab, …)`; both span the viewport and end flush with
  the document; and an unparseable gradient does leave `style.backgroundImage` empty,
  which is what the sRGB fallback detects. What remains unverified is the selectors
  matching a *real* Jellyfin page.
- **The bottom bleed.** `bottomBleed` sums `padding-bottom` from the anchor up to the
  nearest `.page` and cancels the total with a negative margin, so the strip ends
  where the document does instead of floating above `.page`'s `5em` + safe-area
  inset. The walk stops at `.page` deliberately — going further would pull the strip
  through the application shell and over the navigation bar. If the strip sits too
  low or too high on a real client, this is the function to look at.
- **Whether `-skip_frame nokey` plus `showinfo` reports timestamps rebased by the
  input seek** as assumed. If sampling comes out time-shifted, this is the first
  place to look.
- **Sidecar writes into real library folders**, including permission behaviour and
  whether any client or scanner reacts to the new file.
