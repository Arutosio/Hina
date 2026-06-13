// Hina landing page — light vanilla JS (no framework, no build).
(function () {
  "use strict";

  // Sticky-nav border once scrolled.
  var nav = document.getElementById("navbar");
  var onScroll = function () {
    if (!nav) return;
    nav.classList.toggle("scrolled", window.scrollY > 12);
  };
  window.addEventListener("scroll", onScroll, { passive: true });
  onScroll();

  // OS install tabs.
  var tabs = document.querySelectorAll(".os-tab");
  var panels = document.querySelectorAll(".os-panel");
  tabs.forEach(function (tab) {
    tab.addEventListener("click", function () {
      var os = tab.getAttribute("data-os");
      tabs.forEach(function (t) { t.classList.toggle("active", t === tab); });
      panels.forEach(function (p) {
        p.classList.toggle("active", p.getAttribute("data-os") === os);
      });
    });
  });

  // Copy-to-clipboard buttons.
  var copyBtns = document.querySelectorAll(".copy-btn");
  copyBtns.forEach(function (btn) {
    btn.addEventListener("click", function () {
      var target = document.getElementById(btn.getAttribute("data-copy"));
      if (!target) return;
      var text = target.textContent.trim();
      var done = function () {
        var original = "Copy";
        btn.textContent = "Copied!";
        btn.classList.add("copied");
        setTimeout(function () {
          btn.textContent = original;
          btn.classList.remove("copied");
        }, 1600);
      };
      if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(text).then(done).catch(fallback);
      } else {
        fallback();
      }
      function fallback() {
        var ta = document.createElement("textarea");
        ta.value = text;
        ta.style.position = "fixed";
        ta.style.opacity = "0";
        document.body.appendChild(ta);
        ta.select();
        try { document.execCommand("copy"); done(); } catch (e) { /* ignore */ }
        document.body.removeChild(ta);
      }
    });
  });

  // Close the mobile navbar after clicking a link.
  var navLinks = document.querySelectorAll("#nav .nav-link");
  var collapseEl = document.getElementById("nav");
  navLinks.forEach(function (link) {
    link.addEventListener("click", function () {
      if (collapseEl && collapseEl.classList.contains("show") && window.bootstrap) {
        var c = window.bootstrap.Collapse.getInstance(collapseEl);
        if (c) c.hide();
      }
    });
  });
})();
