// Hina — a glowing spirit companion that narrates each section as you scroll.
(function () {
  "use strict";

  var reduce = window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  var EXPR = { normal: "hina-normal.png", happy: "hina-happy.png", point: "hina-point.png", surprised: "hina-surprised.png" };
  var FALLBACK = "./Images/hina-logo.png";

  var sprite = document.getElementById("hina-sprite");
  var textEl = document.getElementById("hina-text");
  var companion = document.getElementById("companion");
  var stage = sprite ? sprite.parentElement : null;

  // ---------- theme ----------
  var themeBtn = document.getElementById("theme-toggle");
  if (themeBtn) themeBtn.addEventListener("click", function () {
    var root = document.documentElement;
    var next = root.getAttribute("data-theme") === "dark" ? "light" : "dark";
    root.setAttribute("data-theme", next);
    try { localStorage.setItem("hina-theme", next); } catch (e) {}
  });

  // ---------- copy buttons ----------
  document.querySelectorAll(".copy").forEach(function (btn) {
    btn.addEventListener("click", function () {
      var t = document.getElementById(btn.getAttribute("data-copy"));
      if (!t) return;
      var text = t.textContent.trim();
      var done = function () { btn.textContent = "copied!"; btn.classList.add("copied"); setTimeout(function () { btn.textContent = "copy"; btn.classList.remove("copied"); }, 1500); };
      if (navigator.clipboard && navigator.clipboard.writeText) { navigator.clipboard.writeText(text).then(done).catch(fb); } else { fb(); }
      function fb() { var a = document.createElement("textarea"); a.value = text; a.style.position = "fixed"; a.style.opacity = "0"; document.body.appendChild(a); a.select(); try { document.execCommand("copy"); done(); } catch (e) {} document.body.removeChild(a); }
    });
  });

  // ---------- install OS tabs ----------
  var tabs = document.querySelectorAll(".os-tab"), panels = document.querySelectorAll(".os-panel");
  tabs.forEach(function (tab) {
    tab.addEventListener("click", function () {
      var os = tab.getAttribute("data-os");
      tabs.forEach(function (t) { t.classList.toggle("active", t === tab); });
      panels.forEach(function (p) { p.classList.toggle("active", p.getAttribute("data-os") === os); });
    });
  });

  // ---------- companion: hide / show ----------
  var hideBtn = document.getElementById("hina-hide");
  var showBtn = document.getElementById("hina-show");
  function setHidden(h) {
    if (companion) companion.classList.toggle("hidden", h);
    if (showBtn) showBtn.hidden = !h;
    try { localStorage.setItem("hina-companion", h ? "off" : "on"); } catch (e) {}
  }
  if (hideBtn) hideBtn.addEventListener("click", function () { setHidden(true); });
  if (showBtn) showBtn.addEventListener("click", function () { setHidden(false); });
  try { if (localStorage.getItem("hina-companion") === "off") setHidden(true); } catch (e) {}

  // ---------- expression swap ----------
  var curExpr = "";
  function setExpr(name) {
    if (!sprite || name === curExpr) return;
    curExpr = name;
    var file = EXPR[name] || EXPR.normal;
    sprite.classList.remove("is-logo");
    sprite.onerror = function () { sprite.onerror = null; sprite.src = FALLBACK; sprite.classList.add("is-logo"); };
    sprite.src = "./Images/" + file;
    if (!reduce && stage) { sprite.classList.remove("talk"); void sprite.offsetWidth; sprite.classList.add("talk"); spark(); }
  }

  // ---------- sparkles ----------
  function spark() {
    var box = companion ? companion.querySelector(".sparkles") : null;
    if (!box) return;
    box.innerHTML = "";
    for (var i = 0; i < 3; i++) {
      var s = document.createElement("span");
      s.textContent = "✨";
      s.style.left = (20 + Math.random() * 90) + "px";
      s.style.top = (10 + Math.random() * 80) + "px";
      s.style.animationDelay = (i * 0.25) + "s";
      box.appendChild(s);
    }
  }

  // ---------- typewriter dialogue ----------
  var typeTimer = null;
  function say(line) {
    if (!textEl) return;
    if (typeTimer) { clearTimeout(typeTimer); typeTimer = null; }
    if (reduce) { textEl.textContent = line; return; }
    textEl.textContent = "";
    var i = 0;
    (function tick() {
      if (i < line.length) { textEl.textContent += line.charAt(i++); typeTimer = setTimeout(tick, line.charAt(i - 1) === " " ? 12 : 24); }
    })();
  }

  function narrate(section) {
    var line = section.getAttribute("data-line") || "";
    var expr = section.getAttribute("data-expr") || "normal";
    setExpr(expr);
    say(line);
  }

  // ---------- scroll → active section ----------
  var sections = Array.prototype.slice.call(document.querySelectorAll(".sec"));
  var active = null;
  if ("IntersectionObserver" in window && sections.length) {
    var io = new IntersectionObserver(function (entries) {
      // pick the most-visible intersecting section
      var best = null, bestRatio = 0;
      entries.forEach(function (e) { if (e.isIntersecting && e.intersectionRatio > bestRatio) { best = e.target; bestRatio = e.intersectionRatio; } });
      if (best && best !== active) { active = best; narrate(best); }
    }, { threshold: [0.35, 0.6] });
    sections.forEach(function (s) { io.observe(s); });
  }

  // first line (greet) once the sprite/image is ready-ish
  var first = document.getElementById("hero") || sections[0];
  if (first) { active = first; narrate(first); }
})();
