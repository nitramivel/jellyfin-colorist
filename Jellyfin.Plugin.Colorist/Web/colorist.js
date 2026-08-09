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
 *   .page                           padding-bottom: 5em + safe-area inset, so
 *                                   appending last leaves the strip clear of
 *                                   the bottom edge on phones.
 *
 * The rules this file lives by:
 *   1. Only ever touch nodes it created.
 *   2. Every lookup has a fallback, and when they all miss, do nothing.
 *   3. Never throw out of an event handler.
 */
(function () {
    'use strict';

    var DISPLAY_HEIGHT = 90;

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
     * The image URL, carrying the access token in the query string.
     *
     * Required for anything the browser loads rather than ApiClient. An <img>
     * issues a plain GET and cannot be given an Authorization header, so an
     * [Authorize] endpoint answers 401 and the element renders as a broken image.
     * ApiClient.ajax is unaffected, which is why the Exists check works even when
     * the picture does not.
     */
    function imageUrl(path) {
        if (!window.ApiClient || typeof window.ApiClient.getUrl !== 'function') {
            return null;
        }

        var token = typeof window.ApiClient.accessToken === 'function'
            ? window.ApiClient.accessToken()
            : null;

        return token
            ? window.ApiClient.getUrl(path, { api_key: token })
            : window.ApiClient.getUrl(path);
    }

    /*
     * Where the strip goes: the page root, so it is the last thing on the page and
     * inherits no horizontal padding. The fallbacks are progressively worse but
     * still produce something visible; widthCorrection below deals with the
     * padding each of them carries.
     */
    function findAnchor() {
        return document.querySelector('.itemDetailPage:not(.hide)')
            || document.querySelector('#itemDetailPage:not(.hide)')
            || document.querySelector('.detailPageWrapperContainer')
            || document.querySelector('.detailPageContent')
            || null;
    }

    /*
     * Cancels whatever horizontal padding the anchor happens to have, so the strip
     * spans the full width of the page whichever anchor was used.
     *
     * Measured rather than hard-coded because the padding is not a constant: the
     * shipped stylesheet uses 32.45vw on wide layouts, 5% on narrow ones, and
     * mirrors left and right for right-to-left languages. A fixed negative margin
     * would be correct at exactly one window size.
     */
    function applyWidth(container, anchor) {
        var padLeft = 0;
        var padRight = 0;

        try {
            var style = window.getComputedStyle(anchor);
            padLeft = parseFloat(style.paddingLeft) || 0;
            padRight = parseFloat(style.paddingRight) || 0;
        } catch (error) {
            // Fall through with zeroes: on the primary anchor there is no
            // horizontal padding anyway, so this is the right answer there.
        }

        container.style.marginLeft = padLeft ? '-' + padLeft + 'px' : '0';
        container.style.marginRight = padRight ? '-' + padRight + 'px' : '0';
    }

    function removeStrip() {
        var existing = document.getElementById(CONTAINER_ID);

        if (existing && existing.parentNode) {
            existing.parentNode.removeChild(existing);
        }
    }

    function buildStrip(itemId) {
        var container = document.createElement('div');
        container.id = CONTAINER_ID;
        container.setAttribute('data-item-id', itemId);
        container.style.cssText = [
            'margin-top: 2.5em',
            'padding: 0',
            'line-height: 0',
            'overflow: hidden'
        ].join(';');

        var image = document.createElement('img');
        image.alt = 'Colour barcode';
        image.title = 'Colour sampled across the runtime, left to right';
        image.src = imageUrl('Colorist/Barcode/' + encodeURIComponent(itemId));
        image.style.cssText = [
            'display: block',
            'width: 100%',
            'height: ' + DISPLAY_HEIGHT + 'px',
            // fill, not contain: the strip is a data image whose aspect ratio
            // carries no meaning, so stretching it to the requested height is
            // exactly what is wanted.
            'object-fit: fill',
            // The source is one pixel per stripe stretched across the viewport.
            // Smoothing would blur neighbouring stripes into each other and
            // quietly undo the choice not to blend them.
            'image-rendering: pixelated'
        ].join(';');

        // If the image 404s between the existence check and the fetch — a barcode
        // deleted mid-page-load — take the section away rather than leaving a gap
        // with a broken-image icon in it.
        image.addEventListener('error', function () {
            removeStrip();
        });

        container.appendChild(image);

        return container;
    }

    function render(itemId) {
        var anchor = findAnchor();

        if (!anchor) {
            return;
        }

        var existing = document.getElementById(CONTAINER_ID);

        if (existing && existing.getAttribute('data-item-id') === itemId) {
            // Same item, still on the page: re-measure only, because a resize or a
            // layout switch may have changed the anchor's padding underneath it.
            applyWidth(existing, anchor);
            return;
        }

        removeStrip();

        var container = buildStrip(itemId);
        anchor.appendChild(container);
        applyWidth(container, anchor);
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

        // Asked before anything is added to the page, so an item that has never
        // been processed gets no empty container and no broken image — it simply
        // looks like a page without a barcode, which is what it is.
        var token = itemId;
        inFlight = token;

        fetchJson('Colorist/Barcode/' + encodeURIComponent(itemId) + '/Exists').then(function (status) {
            // Navigating away mid-request is normal, and applying a stale answer
            // would paint one film's barcode onto another film's page.
            if (inFlight !== token || currentItemId() !== token) {
                return;
            }

            if (status && status.Exists) {
                render(itemId);
            } else {
                removeStrip();
            }
        }, function () {
            // No barcode, no server, no permission — all the same outcome here.
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
                applyWidth(existing, anchor);
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
