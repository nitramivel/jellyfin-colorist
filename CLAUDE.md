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
dotnet test  Jellyfin.Plugin.Colorist.sln -c Release    # 245 tests, no network, no ffmpeg
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
  Imaging/     PNG writer (ZLibStream + CRC32), barcode composer, band reduction for
               the gradient style — the writer and composer only for the optional PNG
  Runs/        run log document shape, and RunEstimate (throughput → time left)
  BarcodeData  the stored format: pack, unpack, the JSON envelope
  CpuBudget    share of the machine → items in flight
  SidecarPaths naming and location rules, one extension per artefact
Services/      everything that touches a process, a file or Jellyfin
  FfmpegRunner        process handling, below-normal priority
  FrameSampler        drives ffmpeg, turns rgb24 into colours
  BarcodeStore        sidecar write with data-directory fallback
  BarcodeService      orchestration, eligibility, crop resolution
  GenerateBarcodesTask  IScheduledTask, the only bulk path
  DeleteBarcodesTask    IScheduledTask, no default triggers, the only bulk removal
  Runs/RunLogStore      live run in memory, finished runs on disk, rotated at 20
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

**Run rows are patched, not rebuilt.** `renderRuns` replaces `innerHTML` only when
`structureSignature` changes — which runs exist, their status, which is open. A live
run changes its counts every two seconds while its shape does not, and rebuilding for
a number that moved threw the drawer's scroll position back to the top on every poll.
The poll path appends new `<tr>` elements and rewrites text nodes instead. The genuine
rebuild (a run ending) restores `drawerScrollTop`, which is tracked from a
capture-phase scroll listener rather than read at rebuild time — that rebuild is two
renders, and by the second there is no scroller left to read a position from.

**Memory answers the progress panel; the file answers history.** `RunLogStore` holds
the live run in memory and serves `Current()` from it, so a two-second poll costs a
lock rather than re-reading and re-parsing a file that grows with every item. Writes
are debounced to five seconds precisely because nothing is reading the file during a
run. `CurrentItem` is never persisted — it changes several times a second and is only
ever read from the snapshot.

**Every button carries `is="emby-button" class="emby-button raised"` — including
generated ones.** Write a new one exactly like this, from the start, and it will look
like the rest of the dashboard:

```html
<button is="emby-button" type="button" id="ColoristSomething" class="emby-button raised">
    <span>Do the thing</span>
</button>
```

There are four on the settings page — Save, **Generate for the whole library**,
**Build it** and **Delete saved barcodes** — plus the **Details** / **Hide** toggle
built as a string in `renderRuns`, which needs the same three pieces despite never
being touched by the upgrade. The class is written out deliberately, and that third
piece is what two earlier attempts at "make these buttons match" were missing: both
went after the attribute, one by adding it back and one by taking it away, and the
three action buttons stayed bare either way while `Details` beside them looked
correct. **No CSS rule
anywhere matches the `is=` attribute.** Verified against the running 10.11 server's
own stylesheets: `.emby-button` holds the entire geometry — `border:0`,
`border-radius:.2em`, `padding:.9em 1em`, `font-family:inherit`, `font-size:inherit`,
`font-weight:600` — and the theme's `.raised` holds one declaration,
`background:#303030`. A button missing the class therefore keeps the browser's native
button (bevelled border, 13px Arial, square corners) on a themed background, next to
identically-authored buttons that look right; that is the whole of the "ugly button"
bug. The class is added at runtime by `document.registerElement`, the **removed** V0
custom-elements API, which reaches a current browser only via `webcomponents.js` and
only for elements the polyfill happens to see — so whether any given button gets it is
not something this page can rely on. Writing it into the markup cannot double up:
`emby-button`'s `createdCallback` opens with
`if (this.classList.contains('emby-button')) return`. Keep `is=` as well: it is what
supplies the behaviour when the upgrade does happen. Labels go in a `<span>`.

**The delete button is an ordinary button at rest.** It used to be hard-coded
`#c62828`, which was a second button style on a page that wants one — and pointless
besides, because a custom dashboard theme (Abyss, here, via Branding → Custom CSS)
sets `.raised { background: … !important }` and flattened it anyway. The danger cue
lives on the card (`.colDanger`) and in the armed state, which says what the next
click will do; `#ColoristDeleteAll.colArmed` needs `!important` on both background
*and* colour to beat such a theme, colour included or the theme's `:hover` writes dark
text onto dark red.

**A gradient is a reduction, not a smoother blend.** `Blended` interpolates every
sample into its neighbour and still shows a band per cut, because each colour is
reproduced exactly at its own position — no amount of softening the joins removes that.
`Gradient` therefore *averages the detail away first*, to `GradientBands` colours
(default 16), and interpolates between those. **The averaging is in linear light, not
Oklab**, which inverts this codebase's usual rule and is deliberate: Oklab is for
perceptual *distance* — interpolating along a straight line, measuring cluster
separation — while a mean combines light, and light adds linearly. Averaging the
encoded bytes is the classic downscaling bug and darkens (half black, half white gives
128 rather than the 188 that reflects half the light). It also means the browser needs
only the sRGB transfer function rather than a second copy of the Oklab matrices, so
`ColourBands.Reduce` and `reduceToBands` in `colorist.js` agree byte for byte — pinned
by `TheClientReducesToTheSameColoursThisDoes`, whose expected strings came from running
the *JavaScript* under Node, not from running the C#.

**`Style` is a nullable enum, and the old `Smooth` boolean is still read.** An enum
cannot spell "not chosen", and its zero value here is `Stripes` — a real choice. Every
configuration written before 0.4.0.0 has no `Style` element at all, so defaulting it
would have read that absence as a deliberate request for hard stripes and silently
turned blending off for everyone who had it on. Null means the question predates the
setting, and `ResolveStyle()` is the one place that answers it from `Smooth`. The
settings page writes both, so a downgrade still draws what was asked for.

**The hover readout costs nothing until somebody hovers.** The time under the pointer
needs the item's runtime, which is per-item and so cannot ride along with the injected
script the way the height and the trims do. Fetching it while drawing would add a
second request to every detail page for a number most visits never use, so
`runtimeFor` is called on `mouseenter` and cached per item, with a failure remembered
as 0 so a server that will not answer is asked once. The window is
`sampledWindow`, which reproduces `SamplePlanner` — the strip covers the *sampled*
window, so reading the bar as 0-to-runtime is several minutes out on a feature at the
default trims. **The trims it uses are the ones configured now**, because the stored
file holds colours and nothing else: change them without regenerating and the readout
drifts by the difference. `HEAD_TRIM`/`TAIL_TRIM` are emitted through
`CultureInfo.InvariantCulture`, or a comma-decimal server would serve
`var HEAD_TRIM = 0,5;` — valid JavaScript that assigns 0 and would be found by nobody.

**Clicking the strip plays through a session command, because there is no local
handle on the player.** `playbackManager` is a webpack module, and the only thing
10.11 puts on `window` is `Emby.Page`, the router — verified by grepping the served
bundles, including all 927 lazy chunks, which never call `sendPlayCommand` either. So
`playFrom` takes the route Jellyfin's own remote control takes: find this device's
session with `getSessions({ deviceId })`, `sendPlayCommand(id, { itemIds, playCommand:
'PlayNow', startPositionTicks })`, and let the command arrive back over the websocket
this tab already holds, where the client's own handler calls
`playbackManager.play(...)` with the start position. `ApiClient` carries it, so the
authorization header, server address and device identity are not ours to assemble.
This is the mechanism the Media Bar plugin uses on this same server, which is the only
reason to believe the query names are right — the endpoint is in `Jellyfin.Api`, not in
the `Jellyfin.Controller` assemblies this repo can reflect over, and the server's
OpenAPI document is not served. **If a click ever stops starting playback, those three
query parameters are the first thing to check.** Note the consequence of asking for a
session: on a server that restricts `GET /Sessions`, a viewer who is not the owner may
be refused and a click then does nothing at all, deliberately.

**The tabs are the exception, and are not buttons in the emby sense.** Plain
`<button class="colTab" role="tab">`, underlined when active — a tab labels which
panel you are looking at rather than being a raised, pressable thing, and Jellyfin's
button theming fights that. Same conclusion Curator reached, and the values are
Curator's exactly. The `border-bottom` on `.colTabs` is load-bearing rather than
decoration: without a baseline for it to sit on, the active tab's 2px marker is a short
line floating under one word and the bar stops reading as tabs at all.

**Cancellation is caught around the dispatch loop, not just the await.**
`GenerateBarcodesTask` spends its whole life inside `foreach`, because
`gate.WaitAsync` blocks there until a worker frees up — so that is where cancellation
lands. With only `Task.WhenAll` guarded, a cancelled run escaped `run.Cancel()` and
`IRunLog.Dispose` recorded it as **failed**, which is what a user pressing Cancel was
shown.

**Run files are named `<start time>-<run id>.json`.** Ordering used to come from the
file's modification time, which is when a run last *wrote* — effectively when it
finished. Those disagree whenever runs differ in length: a three-hour run started at
nine listed above a two-minute one started at ten. The start time in the name makes
the order a property of the run rather than of the filesystem, and costs no reads to
sort by. Files named by ID alone (0.3.0.0 and earlier) are renamed on the first listing after
an upgrade, so existing history reorders too rather than the fix applying only to new
runs; anything that will not move keeps its old name and its old ordering.

**A run's own process cannot record that it died.** A file left saying `running` that
is not the live run belongs to a server that was restarted mid-run, so `Abandoned` is
worked out on read rather than written. `IRunLog.Dispose` is the narrower safety net,
for a task that throws past its own handler.

**The estimate is throughput, not average item duration.** Items are processed several
at a time, so "mean duration × items left" overstates by the concurrency factor.
`RunEstimate` counts completions per wall-clock second over a 20-completion window —
windowed because Jellyfin hands items over grouped by library, so a run genuinely
changes pace when it crosses from films into episodes. Measured to *now* rather than
to the last completion, so a run stalled on one enormous file shows a growing estimate
instead of counting down through a hang.

**Polling has to survive the gap between queueing and starting.** Pressing Generate
hands the job to Jellyfin's task manager and returns; the task begins a moment later.
A poll landing in that window sees an idle server, and if idle simply meant "stop
polling", the loop would die exactly as the run began and the panel would never appear.
`watchForStart` keeps polling through a 30-second grace period, and a generation
counter stops overlapping chains from clearing each other's timers.

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

**CPU is spent by worker count, not by affinity.** `CpuBudget` turns a percentage of
`Environment.ProcessorCount` into a number of items in flight — one ffmpeg each, one
decoder thread each, so workers ≈ cores. `ProcessorAffinity` *is* available on Linux
and Windows (verified), and is deliberately not used: pinning workers to fixed cores
stops the scheduler moving them out of a transcode's way, which makes playback worse
in exactly the case below-normal priority exists to protect. The percentage governs an
idle machine; the priority governs a contended one. `MaxConcurrency` still wins when
non-zero, because a number somebody typed beats a number derived for them.

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

  **The play command has never reached a real session.** `playFrom` was exercised under
  Node against a stubbed `ApiClient` — the call shape, the tick arithmetic and both
  failure paths — which proves what it *sends*, not that the server accepts it or that
  the websocket round trip lands. The three query parameters come from a working plugin
  rather than from an assembly this repo can reflect over. Whether a tap on a
  touchscreen plays or merely shows the readout is also untested: a tap usually
  synthesises `mouseenter` before `click`, so the first one may only resolve the runtime
  and the second play.

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
