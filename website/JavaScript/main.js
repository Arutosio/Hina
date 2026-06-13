// Hina landing page — kawaii × terminal. Vanilla JS, no build.
(function () {
  "use strict";

  // --- dark / light theme toggle (initial theme set inline in <head>) ---
  var themeBtn = document.getElementById("theme-toggle");
  if (themeBtn) {
    themeBtn.addEventListener("click", function () {
      var root = document.documentElement;
      var next = root.getAttribute("data-theme") === "dark" ? "light" : "dark";
      root.setAttribute("data-theme", next);
      try { localStorage.setItem("hina-theme", next); } catch (e) {}
    });
  }

  // --- sticky nav shadow ---
  var nav = document.getElementById("navbar");
  var onScroll = function () { if (nav) nav.classList.toggle("scrolled", window.scrollY > 12); };
  window.addEventListener("scroll", onScroll, { passive: true });
  onScroll();

  // --- OS install tabs ---
  var tabs = document.querySelectorAll(".os-tab");
  var panels = document.querySelectorAll(".os-panel");
  tabs.forEach(function (tab) {
    tab.addEventListener("click", function () {
      var os = tab.getAttribute("data-os");
      tabs.forEach(function (t) { t.classList.toggle("active", t === tab); });
      panels.forEach(function (p) { p.classList.toggle("active", p.getAttribute("data-os") === os); });
    });
  });

  // --- copy buttons ---
  document.querySelectorAll(".copy-btn").forEach(function (btn) {
    btn.addEventListener("click", function () {
      var target = document.getElementById(btn.getAttribute("data-copy"));
      if (!target) return;
      var text = target.textContent.trim();
      var done = function () {
        btn.textContent = "Copied!"; btn.classList.add("copied");
        setTimeout(function () { btn.textContent = "Copy"; btn.classList.remove("copied"); }, 1600);
      };
      if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(text).then(done).catch(fallback);
      } else { fallback(); }
      function fallback() {
        var ta = document.createElement("textarea");
        ta.value = text; ta.style.position = "fixed"; ta.style.opacity = "0";
        document.body.appendChild(ta); ta.select();
        try { document.execCommand("copy"); done(); } catch (e) {}
        document.body.removeChild(ta);
      }
    });
  });

  // --- close mobile navbar after a link tap ---
  var collapseEl = document.getElementById("nav");
  document.querySelectorAll("#nav .nav-link").forEach(function (link) {
    link.addEventListener("click", function () {
      if (collapseEl && collapseEl.classList.contains("show") && window.bootstrap) {
        var c = window.bootstrap.Collapse.getInstance(collapseEl);
        if (c) c.hide();
      }
    });
  });

  // --- hero terminal: typed command + chunk progress ---
  var body = document.getElementById("term-body");
  var reduce = window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches;

  function el(cls, html) {
    var d = document.createElement("div");
    d.className = cls; if (html != null) d.innerHTML = html; return d;
  }

  // Static render (no animation) used for reduced-motion or no terminal.
  function renderStatic() {
    if (!body) return;
    body.innerHTML = "";
    body.appendChild(el("t-line", '<span class="prompt">$</span> hina install game.json'));
    body.appendChild(el("t-line t-ok", "✓ manifest verified (ed25519)"));
    body.appendChild(el("t-line t-dim", "↓ chunks ██████████ 100% · 1320/1320"));
    body.appendChild(el("t-line t-ok", "✓ patched · only 142 chunks transferred (11%)"));
    body.appendChild(el("t-line t-dim", "# rsync-style: I only shipped what changed ✨"));
  }

  function bar(pct) {
    var total = 20, filled = Math.round((pct / 100) * total);
    return "█".repeat(filled) + "░".repeat(total - filled);
  }

  function runAnim() {
    if (!body) return;
    body.innerHTML = "";
    var cmd = "hina install game.json";
    var line = el("t-line", '<span class="prompt">$</span> ');
    var span = document.createElement("span");
    var cur = document.createElement("span"); cur.className = "cursor";
    line.appendChild(span); line.appendChild(cur);
    body.appendChild(line);

    var i = 0;
    function type() {
      if (i < cmd.length) {
        span.textContent += cmd.charAt(i++);
        setTimeout(type, 55);
      } else {
        cur.remove();
        setTimeout(verify, 450);
      }
    }
    function verify() {
      body.appendChild(el("t-line t-ok", "✓ manifest verified (ed25519)"));
      body.appendChild(el("t-line t-dim", "⟳ rolling-checksum scan · 13 files changed"));
      var prog = el("t-line t-dim", "");
      body.appendChild(prog);
      var pct = 0;
      (function step() {
        pct += Math.max(4, Math.round(Math.random() * 13));
        if (pct > 100) pct = 100;
        var n = Math.round((pct / 100) * 1320);
        prog.textContent = "↓ chunks " + bar(pct) + " " + pct + "% · " + n + "/1320";
        if (pct < 100) { setTimeout(step, 130); }
        else { setTimeout(finish, 400); }
      })();
    }
    function finish() {
      body.appendChild(el("t-line t-ok", "✓ patched · only 142 chunks transferred (11%)"));
      body.appendChild(el("t-line t-dim", "# I only shipped what changed ✨"));
      var done = el("t-line", '<span class="prompt">$</span> ');
      var c = document.createElement("span"); c.className = "cursor";
      done.appendChild(c); body.appendChild(done);
    }
    type();
  }

  if (body) {
    if (reduce) { renderStatic(); return; }
    // Start when the hero terminal is on screen.
    var started = false;
    var start = function () { if (!started) { started = true; runAnim(); } };
    if ("IntersectionObserver" in window) {
      var io = new IntersectionObserver(function (entries) {
        entries.forEach(function (e) { if (e.isIntersecting) { start(); io.disconnect(); } });
      }, { threshold: 0.4 });
      io.observe(body);
    } else { start(); }
  }
})();
