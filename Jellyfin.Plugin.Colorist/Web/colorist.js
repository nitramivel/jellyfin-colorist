/*
 * Colorist — puts the barcode at the foot of a movie or episode detail page.
 *
 * This is the least verifiable file in the plugin, and it is written accordingly.
 * Jellyfin Web has no supported extension point for the detail page and no stable
 * DOM contract: class names, the nesting of the detail sections and the way the
 * router announces a view have all changed between releases and can change again.
 *
 * So the rules here are:
 *   1. Only ever touch nodes this script created. Nothing is moved, restyled or
 *      removed from the page's own markup.
 *   2. Every anchor lookup has a fallback, and when they all miss, do nothing at
 *      all. A missing barcode is a disappointment; a broken detail page is a
 *      support request.
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

    function apiUrl(path) {
        if (window.ApiClient && typeof window.ApiClient.getUrl === 'function') {
            return window.ApiClient.getUrl(path);
        }
        return null;
    }

    function fetchJson(path) {
        if (!window.ApiClient || typeof window.ApiClient.ajax !== 'function') {
            return Promise.reject(new Error('no ApiClient'));
        }

        return window.ApiClient.ajax({
            type: 'GET',
            url: apiUrl(path),
            dataType: 'json'
        });
    }

    /*
     * Finds where to put the strip.
     *
     * Ordered most specific to least. The last entry is the page container itself,
     * which every build has in some form — appending there still lands the strip at
     * the bottom of the page, which is where it was asked to go.
     */
    function findAnchor() {
        var page = document.querySelector('.itemDetailPage:not(.hide)')
            || document.querySelector('#itemDetailPage:not(.hide)')
            || document.querySelector('.detailPage:not(.hide)');

        if (!page) {
            return null;
        }

        return page.querySelector('.detailPageSecondaryContainer')
            || page.querySelector('.detailPageContent')
            || page;
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
            'margin: 2em 0 1.5em 0',
            'padding: 0',
            'width: 100%'
        ].join(';');

        var heading = document.createElement('h2');
        heading.className = 'sectionTitle';
        heading.textContent = 'Colour';
        heading.style.cssText = 'margin-bottom: 0.6em';

        var image = document.createElement('img');
        image.alt = 'Colour barcode';
        image.src = apiUrl('Colorist/Barcode/' + encodeURIComponent(itemId));
        image.style.cssText = [
            'display: block',
            'width: 100%',
            'height: ' + DISPLAY_HEIGHT + 'px',
            'object-fit: fill',
            'border-radius: 6px',
            // The strip is a run of one-pixel-wide columns stretched across the
            // viewport. Smoothing it would blur neighbouring stripes into each other
            // and quietly undo the choice not to blend them.
            'image-rendering: pixelated'
        ].join(';');

        // If the image 404s between the existence check and the fetch — a barcode
        // deleted mid-page-load — take the whole section away rather than leaving a
        // heading over a broken-image icon.
        image.addEventListener('error', function () {
            removeStrip();
        });

        container.appendChild(heading);
        container.appendChild(image);

        return container;
    }

    function render(itemId) {
        var existing = document.getElementById(CONTAINER_ID);

        if (existing && existing.getAttribute('data-item-id') === itemId) {
            return;
        }

        removeStrip();

        var anchor = findAnchor();

        if (!anchor) {
            return;
        }

        anchor.appendChild(buildStrip(itemId));
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

        // Asked before anything is added to the page, so an item that has never been
        // processed gets no empty heading and no broken image — it simply looks like
        // a page without a barcode, which is what it is.
        var token = itemId;
        inFlight = token;

        fetchJson('Colorist/Barcode/' + encodeURIComponent(itemId) + '/Exists').then(function (status) {
            // Navigating away during the request is normal, and applying a stale
            // answer would paint one film's barcode onto another's page.
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

    window.addEventListener('hashchange', safely(schedule));
    document.addEventListener('viewshow', safely(schedule));

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', safely(schedule));
    } else {
        schedule();
    }
})();
