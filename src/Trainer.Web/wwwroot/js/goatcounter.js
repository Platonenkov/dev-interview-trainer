// GoatCounter: https://www.goatcounter.com
// This file is released under the ISC license: https://opensource.org/licenses/ISC
//
// Reduced copy of https://gc.zgo.at/count.v4.js (SHA-384 sha384-nRw6qfbWyJha9LhsOtSb2YJDyZdKvvCFh0fJYlkquSFjUxp9FVNugbfy8q1jdxI+,
// SHA-256 b9f5c4c04edb8802d7b73f479a2f532f52367b633b56a09a224fc659fde86c03, as downloaded 2026-11-18) for the static edition
// of Dev Interview Trainer (docs/adr/0025). Changes against the upstream file, all of them to keep what a visitor typed or
// opened inside the browser:
//   - the "q" field (the page's query string, which holds catalog search terms) is not sent at all;
//   - the path is the page path only: any query or hash a callback or the page produces is cut off, and the canonical-link
//     lookup is gone (it carried the query too);
//   - the referrer is sent only when it comes from another origin, and then as that origin alone, never a path or query;
//   - Do Not Track (navigator.doNotTrack === '1') is honoured, like the Umami tag of the server edition;
//   - events, the visitor-counter widget, the data-goatcounter-settings attribute and the "toggle" hash are removed.
// Only the counting endpoint and the settings in goatcounter-settings.js are read; nothing else leaves the page.
;(function() {
	'use strict';

	window.goatcounter = window.goatcounter || {}

	var enc = encodeURIComponent

	// The page path with no query and no hash, whatever a path callback in the settings returns.
	var get_path = function() {
		var p = window.goatcounter.path
		if (typeof(p) === 'function') p = p()
		if (typeof(p) !== 'string' || p === '') p = location.pathname
		return p.split('?')[0].split('#')[0] || location.pathname.split('?')[0].split('#')[0]
	}

	// Another origin's address reduced to its origin; an empty string for a same-origin, malformed or non-http referrer.
	var get_referrer = function() {
		var r = document.referrer
		if (!r) return ''
		try {
			var u = new URL(r)
			if ((u.protocol !== 'http:' && u.protocol !== 'https:') || u.origin === location.origin) return ''
			return u.origin
		}
		catch (err) { return '' }
	}

	// Headless browsers are probably a bot; there is more filtering on the backend, but these can't be fetched from there.
	var is_bot = function() {
		var w = window, d = document
		if (w.callPhantom || w._phantom || w.phantom)
			return 150
		if (w.__nightmare)
			return 151
		if (d.__selenium_unwrapped || d.__webdriver_evaluate || d.__driver_evaluate)
			return 152
		if (navigator.webdriver)
			return 153
		return 0
	}

	var get_endpoint = function() {
		var s = document.querySelector('script[data-goatcounter]')
		return s && s.dataset.goatcounter ? s.dataset.goatcounter : ''
	}

	// Object to urlencoded string, starting with a ?.
	var urlencode = function(obj) {
		var p = []
		for (var k in obj)
			if (obj[k] !== '' && obj[k] !== null && obj[k] !== undefined && obj[k] !== false)
				p.push(enc(k) + '=' + enc(obj[k]))
		return '?' + p.join('&')
	}

	var filter = function() {
		if (navigator.doNotTrack === '1')
			return 'do not track'
		if ('visibilityState' in document && document.visibilityState === 'prerender')
			return 'visibilityState'
		if (!window.goatcounter.allow_frame && location !== parent.location)
			return 'frame'
		if (!window.goatcounter.allow_local && location.hostname.match(/(localhost$|^127\.|^10\.|^172\.(1[6-9]|2[0-9]|3[0-1])\.|^192\.168\.|^0\.0\.0\.0$)/))
			return 'localhost'
		if (!window.goatcounter.allow_local && location.protocol === 'file:')
			return 'localfile'
		return false
	}

	window.goatcounter.count = function() {
		var endpoint = get_endpoint()
		if (!endpoint || filter())
			return

		var url = endpoint + urlencode({
			p: get_path(),
			r: get_referrer(),
			t: document.title,
			s: [window.screen.width, window.screen.height, (window.devicePixelRatio || 1)],
			b: is_bot(),
			rnd: Math.random().toString(36).substr(2, 5),  // Browsers don't always listen to Cache-Control.
		})

		if (navigator.sendBeacon)
			navigator.sendBeacon(url)
		else {  // Fallback for (very) old browsers.
			var img = document.createElement('img')
			img.src = url
			img.style.position = 'absolute'
			img.style.bottom = '0px'
			img.style.width = '1px'
			img.style.height = '1px'
			img.setAttribute('alt', '')
			img.setAttribute('aria-hidden', 'true')
			img.addEventListener('load', function() { if (img.parentNode) img.parentNode.removeChild(img) }, false)
			document.body.appendChild(img)
		}
	}

	var on_load = function(f) {
		if (document.body === null)
			document.addEventListener('DOMContentLoaded', function() { f() }, false)
		else
			f()
	}

	if (!window.goatcounter.no_onload)
		on_load(function() {
			// A page that is not visible yet (a prerender or a background tab) is counted once it becomes visible.
			if (!('visibilityState' in document) || document.visibilityState === 'visible')
				window.goatcounter.count()
			else {
				var f = function() {
					if (document.visibilityState !== 'visible')
						return
					document.removeEventListener('visibilitychange', f)
					window.goatcounter.count()
				}
				document.addEventListener('visibilitychange', f)
			}
		})
})();
