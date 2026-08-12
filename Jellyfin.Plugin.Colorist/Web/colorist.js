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
    var SMOOTH = false;

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

    function buildStrip(itemId, colours) {
        var painted = SMOOTH ? paintGradient(colours) : paintCanvas(colours);

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
            'overflow: hidden'
        ].join(';');

        container.appendChild(painted);

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
                fn();
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
