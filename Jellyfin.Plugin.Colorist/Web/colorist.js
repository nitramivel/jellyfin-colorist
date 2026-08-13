/*
 * Colorist — puts the barcode across the foot of a movie or episode page.
 *
 * Jellyfin Web has no supported extension point for the detail page, so the
 * selectors here were taken from the shipped stylesheet of a real 10.11.11
 * server rather than guessed. What that told us:
 *
 *   .itemDetailPage                 the page root. Only ever styled with
 *                                   padding-top, so nothing fights a full-width
 *                                   child. This is the anchor.
 *   .detailPageContent              padding-left: 32.45vw on wide layouts, to
 *                                   clear the poster. Anchoring here would
 *                                   indent the strip a third of the way across
 *                                   the screen, which is exactly the bug this
 *                                   layout is written to avoid.
 *   .page                           padding-bottom: 5em + safe-area inset. This
 *                                   is the gap that used to sit under the strip;
 *                                   see bottomBleed below.
 *
 * The strip is drawn here from the colours the server measured, rather than
 * fetched as a picture. That is what lets the display height and the blending
 * change on a page reload instead of on another pass of ffmpeg over the whole
 * library, and it means the request carries a real Authorization header — an
 * <img> cannot, which is why the old version had to put an access token in a
 * query string.
 *
 * The rules this file lives by:
 *   1. Only ever touch nodes it created.
 *   2. Every lookup has a fallback, and when they all miss, do nothing.
 *   3. Never throw out of an event handler.
 */
(function () {
    'use strict';

    var DISPLAY_HEIGHT = 90;
    var STYLE = 'stripes';
    var GRADIENT_BANDS = 16;
    var HEAD_TRIM = 0.5;
    var TAIL_TRIM = 4;

    var CONTAINER_ID = 'colorist-strip';
    var inFlight = null;

    function currentItemId() {
        // Read from the URL rather than from any app state object. The hash carries
        // the item ID on every client build; the internal view model has moved and
        // been renamed repeatedly.
        var hash = window.location.hash || '';
        var match = hash.match(/[?&]id=([a-f0-9-]{32,36})/i);

        return match ? match[1] : null;
    }

    function isDetailRoute() {
        var hash = window.location.hash || '';
        return hash.indexOf('/details') !== -1 || hash.indexOf('/item') !== -1;
    }

    function fetchJson(path) {
        if (!window.ApiClient || typeof window.ApiClient.ajax !== 'function') {
            return Promise.reject(new Error('no ApiClient'));
        }

        return window.ApiClient.ajax({
            type: 'GET',
            url: window.ApiClient.getUrl(path),
            dataType: 'json'
        });
    }

    /*
     * Splits the packed hex string the server stores into CSS colours.
     *
     * Six characters per stripe, no separators and no leading hash — a thousand
     * columns is 6 KB this way against roughly 10 KB as a JSON array of strings,
     * on a request that happens on every visit to every detail page.
     */
    function unpack(payload) {
        if (!payload || payload.v !== 1 || typeof payload.colors !== 'string') {
            return null;
        }

        var packed = payload.colors;

        if (packed.length === 0 || packed.length % 6 !== 0 || /[^0-9a-f]/i.test(packed)) {
            return null;
        }

        var colours = new Array(packed.length / 6);

        for (var i = 0; i < colours.length; i++) {
            colours[i] = packed.substr(i * 6, 6);
        }

        return colours;
    }

    /*
     * Hard stripes, drawn one pixel per sample and scaled up by the browser.
     *
     * A canvas at the true column count rather than a gradient with two stops per
     * stripe: hard stops land on fractional percentages and get antialiased into
     * faint seams, whereas nearest-neighbour scaling of an exact bitmap is what
     * this strip has always looked like.
     */
    function paintCanvas(colours) {
        var canvas = document.createElement('canvas');
        canvas.width = colours.length;
        canvas.height = 1;

        var context = canvas.getContext('2d');

        if (!context) {
            return null;
        }

        var image = context.createImageData(colours.length, 1);

        for (var i = 0; i < colours.length; i++) {
            var packed = parseInt(colours[i], 16);

            image.data[(i * 4) + 0] = (packed >> 16) & 0xFF;
            image.data[(i * 4) + 1] = (packed >> 8) & 0xFF;
            image.data[(i * 4) + 2] = packed & 0xFF;
            image.data[(i * 4) + 3] = 255;
        }

        context.putImageData(image, 0, 0);

        canvas.style.cssText = [
            'display: block',
            'width: 100%',
            'height: ' + DISPLAY_HEIGHT + 'px',
            // fill, not contain: the strip is a data image whose aspect ratio
            // carries no meaning, so stretching it to the requested height is
            // exactly what is wanted.
            'object-fit: fill',
            // One pixel per stripe stretched across the viewport. Smoothing would
            // blur neighbouring stripes into each other and quietly undo the
            // choice not to blend them.
            'image-rendering: pixelated'
        ].join(';');

        return canvas;
    }

    /*
     * Blended stripes, as a gradient the browser interpolates perceptually.
     *
     * The `in oklab` is not decoration. Interpolating in sRGB sends the midpoint
     * between two vivid colours of different hue through a darker, greyer place
     * than either endpoint, so a blended strip picks up a dark seam at every
     * transition — the same reason the server-side composer works in Oklab.
     * Browsers without the syntax (before Chrome 111, Safari 16.2, Firefox 128)
     * reject the whole declaration, which leaves backgroundImage empty and is how
     * the fallback below detects them.
     */
    /*
     * Averages the samples down to a few bands, in linear light.
     *
     * This is what makes the gradient style a gradient. Blending a thousand samples
     * still reproduces all thousand — each at its own position — so the strip keeps a
     * visible band per cut however smooth the joins are; the detail has to be
     * averaged away before anything is drawn.
     *
     * Linear light rather than Oklab, matching Core's ColourBands exactly, so the
     * strip and the optional PNG are the same picture. Averaging combines light and
     * light adds linearly, which Oklab is not built for — and it needs only the sRGB
     * transfer function, so no second copy of the Oklab matrices comes into the
     * browser. Averaging the encoded bytes instead is the usual downscaling bug and
     * darkens everything: half black and half white would come out 128 rather than
     * the 188 that reflects half the light.
     */
    function toLinear(channel) {
        var c = channel / 255;
        return c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
    }

    function fromLinear(linear) {
        var v = linear <= 0.0031308
            ? linear * 12.92
            : (1.055 * Math.pow(linear, 1 / 2.4)) - 0.055;

        return Math.max(0, Math.min(255, Math.round(v * 255)));
    }

    function reduceToBands(colours, bands) {
        if (!(bands >= 1) || colours.length <= bands) {
            return colours;
        }

        var reduced = new Array(bands);

        for (var band = 0; band < bands; band++) {
            // From the band index rather than by accumulating a step, so the last band
            // ends exactly on the last sample whether or not the count divides evenly.
            var from = Math.floor((band * colours.length) / bands);
            var to = Math.max(from + 1, Math.floor(((band + 1) * colours.length) / bands));
            var r = 0;
            var g = 0;
            var b = 0;

            for (var i = from; i < to; i++) {
                var packed = parseInt(colours[i], 16);

                r += toLinear((packed >> 16) & 0xFF);
                g += toLinear((packed >> 8) & 0xFF);
                b += toLinear(packed & 0xFF);
            }

            var count = to - from;

            reduced[band] = hex(fromLinear(r / count))
                + hex(fromLinear(g / count))
                + hex(fromLinear(b / count));
        }

        return reduced;
    }

    function hex(value) {
        return (value < 16 ? '0' : '') + value.toString(16);
    }

    function paintGradient(colours) {
        var element = document.createElement('div');
        var stops = [];

        for (var i = 0; i < colours.length; i++) {
            stops.push('#' + colours[i]);
        }

        var joined = stops.join(',');

        element.style.cssText = [
            'display: block',
            'width: 100%',
            'height: ' + DISPLAY_HEIGHT + 'px'
        ].join(';');

        element.style.backgroundImage = 'linear-gradient(to right in oklab,' + joined + ')';

        if (!element.style.backgroundImage) {
            element.style.backgroundImage = 'linear-gradient(to right,' + joined + ')';
        }

        return element;
    }

    /*
     * Where the strip goes: the page root, so it is the last thing on the page and
     * inherits no horizontal padding. The fallbacks are progressively worse but
     * still produce something visible; applyBleed below deals with the padding
     * each of them carries.
     */
    function findAnchor() {
        return document.querySelector('.itemDetailPage:not(.hide)')
            || document.querySelector('#itemDetailPage:not(.hide)')
            || document.querySelector('.detailPageWrapperContainer')
            || document.querySelector('.detailPageContent')
            || null;
    }

    function paddingOf(node) {
        try {
            var style = window.getComputedStyle(node);

            return {
                left: parseFloat(style.paddingLeft) || 0,
                right: parseFloat(style.paddingRight) || 0,
                bottom: parseFloat(style.paddingBottom) || 0
            };
        } catch (error) {
            // Zeroes are the right answer on the primary anchor anyway, which has
            // no horizontal padding and is itself the .page.
            return { left: 0, right: 0, bottom: 0 };
        }
    }

    /*
     * How far the strip has to be pulled down to sit flush with the end of the
     * document rather than floating above it.
     *
     * The gap is not the anchor's: `.page` carries `padding-bottom: 5em` plus the
     * safe-area inset, which is what keeps the last of the normal page content
     * clear of the bottom edge on phones. A strip appended inside that padding
     * inherits the gap, so every ancestor's bottom padding up to and including the
     * page has to be cancelled — not just the anchor's, which on the fallback
     * anchors is zero and would leave the bug exactly as it was.
     *
     * Measured rather than hard-coded for the same reason the horizontal bleed is:
     * 5em is a font-size away from being a different number of pixels, and the
     * safe-area inset is a property of the device.
     */
    function bottomBleed(anchor) {
        var node = anchor;
        var total = 0;

        for (var levels = 0; node && levels < 6; levels++) {
            total += paddingOf(node).bottom;

            if (node.classList && node.classList.contains('page')) {
                return total;
            }

            node = node.parentElement;
        }

        // No .page above us within reach. Cancel what the anchor itself carries and
        // nothing more — the rest is the application shell, and pulling the strip
        // through that would put it over the navigation bar.
        return paddingOf(anchor).bottom;
    }

    /*
     * Cancels the padding standing between the strip and the edges of the page, so
     * it spans the full width whichever anchor was used and ends where the page
     * ends.
     *
     * Measured rather than hard-coded because the padding is not a constant: the
     * shipped stylesheet uses 32.45vw on wide layouts, 5% on narrow ones, and
     * mirrors left and right for right-to-left languages. A fixed negative margin
     * would be correct at exactly one window size.
     */
    function applyBleed(container, anchor) {
        var padding = paddingOf(anchor);
        var bottom = bottomBleed(anchor);

        container.style.marginLeft = padding.left ? '-' + padding.left + 'px' : '0';
        container.style.marginRight = padding.right ? '-' + padding.right + 'px' : '0';
        container.style.marginBottom = bottom ? '-' + bottom + 'px' : '0';
    }

    function removeStrip() {
        var existing = document.getElementById(CONTAINER_ID);

        if (existing && existing.parentNode) {
            existing.parentNode.removeChild(existing);
        }
    }

    /*
     * Where the strip's left and right edges fall in the runtime.
     *
     * Reproduces SamplePlanner, because the strip covers the sampled window rather
     * than the whole film: with the default trims the left edge is half a percent in
     * and the right edge four percent from the end, so reading the bar as 0 to
     * runtime would put every hover several minutes out on a feature.
     *
     * The trims are the ones configured now, not necessarily the ones in force when
     * this item was sampled — the stored file holds colours and nothing else. Change
     * them without regenerating and the readout drifts by the difference; regenerating
     * puts it back. Worth knowing, and still far closer than ignoring them.
     */
    function sampledWindow(runtimeSeconds) {
        if (!(runtimeSeconds > 0)) {
            return null;
        }

        var head = Math.min(40, Math.max(0, HEAD_TRIM));
        var tail = Math.min(40, Math.max(0, TAIL_TRIM));

        if (head + tail > 80) {
            var scale = 80 / (head + tail);
            head *= scale;
            tail *= scale;
        }

        var start = runtimeSeconds * (head / 100);
        var end = runtimeSeconds * (1 - (tail / 100));

        // The planner's own escape hatch: a window too short to be worth sampling is
        // abandoned for the whole runtime, so trims of 40/40 on a two-minute extra
        // must map the same way here or the readout would be wrong on exactly the
        // items where the trims matter most.
        if (end - start < 5) {
            start = 0;
            end = runtimeSeconds;
        }

        return { start: start, end: end };
    }

    function twoDigits(value) {
        return (value < 10 ? '0' : '') + value;
    }

    function clockOf(seconds) {
        var whole = Math.max(0, Math.floor(seconds));
        var hours = Math.floor(whole / 3600);
        var minutes = Math.floor((whole % 3600) / 60);

        return (hours > 0 ? hours + ':' + twoDigits(minutes) : String(minutes))
            + ':' + twoDigits(whole % 60);
    }

    /*
     * The item's runtime, fetched at most once per item and only when somebody
     * actually hovers the strip.
     *
     * Deliberately not part of drawing it. The colours arrive in one request that a
     * revalidating ETag usually answers with a 304, and adding a second request to
     * every detail page — for a number only used if a pointer arrives — would be a
     * poor trade. A cached miss is remembered as 0 so a server that will not answer is
     * asked once rather than on every mouse move.
     */
    var runtimes = {};

    function runtimeFor(itemId) {
        if (Object.prototype.hasOwnProperty.call(runtimes, itemId)) {
            return Promise.resolve(runtimes[itemId]);
        }

        var client = window.ApiClient;

        if (!client || typeof client.getItem !== 'function'
            || typeof client.getCurrentUserId !== 'function') {
            runtimes[itemId] = 0;
            return Promise.resolve(0);
        }

        return client.getItem(client.getCurrentUserId(), itemId).then(function (item) {
            // Ticks are 100-nanosecond units, so ten million to the second.
            var seconds = item && item.RunTimeTicks ? item.RunTimeTicks / 10000000 : 0;

            runtimes[itemId] = seconds;
            return seconds;
        }, function () {
            runtimes[itemId] = 0;
            return 0;
        });
    }

    /*
     * The hover readout: where in the film the colour under the pointer came from.
     *
     * Built rather than left to the title attribute, which cannot follow a pointer —
     * a native tooltip is placed once and would answer for wherever the mouse first
     * stopped. Both nodes are ours and are pointer-events: none, so nothing here can
     * intercept a click meant for the page.
     */
    function attachReadout(container, itemId) {
        var line = document.createElement('div');
        line.style.cssText = [
            'position: absolute',
            'top: 0',
            'bottom: 0',
            'width: 1px',
            'background: rgba(255,255,255,0.75)',
            'box-shadow: 0 0 2px rgba(0,0,0,0.6)',
            'pointer-events: none',
            'display: none'
        ].join(';');

        var label = document.createElement('div');
        label.style.cssText = [
            'position: absolute',
            'top: 50%',
            'transform: translate(-50%, -50%)',
            'padding: 0.25em 0.5em',
            'border-radius: 0.25em',
            'background: rgba(0,0,0,0.72)',
            'color: #fff',
            'font-size: 0.82rem',
            'line-height: 1.3',
            'font-variant-numeric: tabular-nums',
            'white-space: nowrap',
            'pointer-events: none',
            'display: none'
        ].join(';');

        container.appendChild(line);
        container.appendChild(label);

        // Resolved once on the way in, so a mouse move is arithmetic and two layout
        // reads rather than a promise per pixel. Null until it arrives, which is the
        // first fraction of a second of the first hover and nothing after that.
        var span = null;

        function hide() {
            line.style.display = 'none';
            label.style.display = 'none';
        }

        function enter() {
            runtimeFor(itemId).then(function (runtimeSeconds) {
                span = sampledWindow(runtimeSeconds);

                // The descriptive title would otherwise pop up over the readout a
                // second into every hover. Dropped only once there is something better
                // to say: a server that will not give up a runtime keeps it.
                if (span) {
                    container.removeAttribute('title');
                }
            }, function () {
                span = null;
            });
        }

        function place(event) {
            if (!span) {
                // No runtime to map onto — either still arriving, or the server would
                // not say. Leave the strip alone rather than show a percentage nobody
                // asked for.
                return;
            }

            var rect = container.getBoundingClientRect();

            if (rect.width <= 0) {
                hide();
                return;
            }

            var x = Math.min(rect.width, Math.max(0, event.clientX - rect.left));

            label.textContent = clockOf(
                span.start + ((x / rect.width) * (span.end - span.start)));

            line.style.left = x + 'px';
            line.style.display = 'block';
            label.style.display = 'block';

            // Kept inside the strip at both ends, so the readout is not clipped by the
            // overflow that stops the bleed spilling sideways. Measured after the text
            // is set, since that is what decides the width.
            label.style.left = Math.min(
                rect.width - (label.offsetWidth / 2) - 4,
                Math.max((label.offsetWidth / 2) + 4, x)) + 'px';
        }

        container.addEventListener('mouseenter', safely(enter));
        container.addEventListener('mousemove', safely(place));
        container.addEventListener('mouseleave', safely(hide));
    }

    function buildStrip(itemId, colours) {
        var painted = STYLE === 'stripes'
            ? paintCanvas(colours)
            : paintGradient(STYLE === 'gradient'
                ? reduceToBands(colours, GRADIENT_BANDS)
                : colours);

        if (!painted) {
            return null;
        }

        var container = document.createElement('div');
        container.id = CONTAINER_ID;
        container.setAttribute('data-item-id', itemId);
        container.title = 'Colour sampled across the runtime, left to right';
        container.style.cssText = [
            'margin-top: 2.5em',
            'padding: 0',
            'line-height: 0',
            'overflow: hidden',
            // So the readout below positions against the strip rather than the page.
            'position: relative'
        ].join(';');

        container.appendChild(painted);
        attachReadout(container, itemId);

        return container;
    }

    function render(itemId, colours) {
        var anchor = findAnchor();

        if (!anchor) {
            return;
        }

        var existing = document.getElementById(CONTAINER_ID);

        if (existing && existing.getAttribute('data-item-id') === itemId) {
            // Same item, still on the page: re-measure only, because a resize or a
            // layout switch may have changed the anchor's padding underneath it.
            applyBleed(existing, anchor);
            return;
        }

        // Taken away first, before anything can go wrong building the replacement.
        // Whatever is on the page at this point belongs to a different item — the
        // same-item case returned above — and leaving one film's barcode under
        // another film's title is worse than showing none.
        removeStrip();

        var container = buildStrip(itemId, colours);

        if (!container) {
            return;
        }

        anchor.appendChild(container);
        applyBleed(container, anchor);
    }

    function update() {
        if (!isDetailRoute()) {
            removeStrip();
            return;
        }

        var itemId = currentItemId();

        if (!itemId) {
            removeStrip();
            return;
        }

        // The 404 for an unprocessed item is the answer, so nothing is added to the
        // page before it arrives: a film that has never been sampled simply looks
        // like a page without a barcode, which is what it is.
        var token = itemId;
        inFlight = token;

        fetchJson('Colorist/Barcode/' + encodeURIComponent(itemId) + '/Colors').then(function (payload) {
            // Navigating away mid-request is normal, and applying a stale answer
            // would paint one film's barcode onto another film's page.
            if (inFlight !== token || currentItemId() !== token) {
                return;
            }

            var colours = unpack(payload);

            if (colours) {
                render(itemId, colours);
            } else {
                removeStrip();
            }
        }, function () {
            // No barcode, no server, no permission — all the same outcome here.
            if (inFlight === token) {
                removeStrip();
            }
        });
    }

    function schedule() {
        // The router swaps the view in after the hash changes, so reading the DOM
        // immediately finds the previous page. A short delay plus one retry covers
        // both a fast local server and a slow first paint without polling forever.
        window.setTimeout(update, 300);
        window.setTimeout(update, 1200);
    }

    function safely(fn) {
        return function () {
            try {
                // Arguments forwarded, because the readout's handlers need the event.
                // The schedule and resize callers pass none, so nothing changes there.
                fn.apply(this, arguments);
            } catch (error) {
                if (window.console && window.console.debug) {
                    window.console.debug('Colorist:', error);
                }
            }
        };
    }

    var resizeTimer = null;

    function onResize() {
        // Re-measure, because the anchor's padding is written in vw and percent and
        // therefore changes with the window. Debounced: a drag fires this
        // continuously and each call reads layout.
        if (resizeTimer) {
            window.clearTimeout(resizeTimer);
        }

        resizeTimer = window.setTimeout(safely(function () {
            var existing = document.getElementById(CONTAINER_ID);
            var anchor = findAnchor();

            if (existing && anchor) {
                applyBleed(existing, anchor);
            }
        }), 150);
    }

    window.addEventListener('hashchange', safely(schedule));
    document.addEventListener('viewshow', safely(schedule));
    window.addEventListener('resize', onResize);

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', safely(schedule));
    } else {
        schedule();
    }
})();
