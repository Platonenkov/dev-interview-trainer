namespace Trainer.Web.Startup;

/// <summary>
/// The inline script of every static page that tells the visitor to reload when part of the page did not load (design
/// 5.13). GitHub Pages replaces the whole site on each deployment, so a cached page can point at a file that no longer
/// exists; the script must work without that very file, which is why it is inline and allowed by hash
/// (<see cref="StaticCsp"/>). It listens, in the capture phase, for load errors of elements marked
/// <c>data-recover</c> and exposes <c>window.ditRecover</c> for islands-start.js. The banner is shown at most once per
/// page and browser session, so a file that stays broken cannot make the page flicker; the key is the path and the query
/// without <c>_r</c>, because the reload link below adds a fresh <c>_r</c> and the reloaded URL is still the same page. The reload link is a
/// query-only reference resolved against <c>location.href</c>: not against <c>&lt;base&gt;</c> (island pages set it to the
/// site root) and not a path rebuilt from <c>location.pathname</c> (which could start with <c>//</c> and leave the site).
/// It carries a fresh <c>_r</c> value so the browser and the CDN fetch the current HTML instead of the cached copy.
/// </summary>
public static class RecoveryScript
{
    // The banner sits in <body>, which does not exist yet when a stylesheet in <head> fails, so the work waits for the DOM.
    // The storage key (the path and the query without _r) is written before the banner is shown; a blocked storage only loses
    // the once-per-session limit.
    public const string Source =
        "(function(){var q=new URLSearchParams(location.search);q.delete('_r');var k='dit.recover.'+location.pathname+q.toString();" +
        "function run(){var b=document.getElementById('site-updated');if(!b){return;}" +
        "try{if(sessionStorage.getItem(k)){return;}sessionStorage.setItem(k,'1');}catch(e){}" +
        "var a=b.querySelector('[data-reload]');" +
        "if(a){var p=new URLSearchParams(location.search);p.set('_r',String(Date.now()));a.href=new URL('?'+p.toString(),location.href).href;}" +
        "b.hidden=false;}" +
        "function show(){if(document.readyState==='loading'){document.addEventListener('DOMContentLoaded',run);}else{run();}}" +
        "window.ditRecover=show;" +
        "window.addEventListener('error',function(e){var t=e.target;" +
        "if(t&&t!==window&&t.hasAttribute&&t.hasAttribute('data-recover')){show();}},true);})();";

    /// <summary>The CSP source expression that allows <see cref="Source"/>.</summary>
    public static readonly string CspSource = StaticCsp.Sha256Source(Source);
}
