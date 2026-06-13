// Hina — a terminal you chat with. Vanilla JS, no build.
(function () {
  "use strict";

  var log = document.getElementById("log");
  var form = document.getElementById("form");
  var input = document.getElementById("cmd");
  var chipsBox = document.getElementById("chips");
  var themeBtn = document.getElementById("theme-toggle");
  var reduce = window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches;

  var AVATAR = "./Images/hina-logo.png";
  var VERSION = "1.6.0";
  var REPO = "https://github.com/Arutosio/Hina";
  var DOCS = REPO + "/blob/master/docs/";

  var history = [];
  var hIndex = -1;
  var busy = false;

  // ---------- helpers ----------
  function scroll() { log.scrollTop = log.scrollHeight; }
  function esc(s) { return String(s).replace(/[&<>]/g, function (c) { return { "&": "&amp;", "<": "&lt;", ">": "&gt;" }[c]; }); }

  function elFrom(html) { var d = document.createElement("div"); d.innerHTML = html; return d.firstElementChild; }

  function printUser(cmd) {
    var row = document.createElement("div");
    row.className = "row user";
    row.innerHTML = '<span class="ps"><span class="who">you</span>@local <span style="color:var(--amber)">~</span> <span style="color:var(--pink)">$</span></span> ' + esc(cmd);
    log.appendChild(row); scroll();
  }

  function newHinaBubble() {
    var wrap = document.createElement("div");
    wrap.className = "hina";
    var img = document.createElement("img");
    img.className = "avatar"; img.src = AVATAR; img.alt = "Hina"; img.width = 30; img.height = 30;
    var bubble = document.createElement("div");
    bubble.className = "bubble";
    var name = document.createElement("div");
    name.className = "name"; name.textContent = "hina";
    bubble.appendChild(name);
    wrap.appendChild(img); wrap.appendChild(bubble);
    log.appendChild(wrap); scroll();
    return bubble;
  }

  function typeInto(parent, text, done) {
    var p = document.createElement("div");
    p.className = "say"; parent.appendChild(p);
    if (reduce) { p.textContent = text; scroll(); return done(); }
    var cur = document.createElement("span"); cur.className = "cursor"; p.appendChild(cur);
    var i = 0;
    (function tick() {
      if (i < text.length) {
        cur.insertAdjacentText("beforebegin", text.charAt(i++));
        scroll();
        setTimeout(tick, text.charAt(i - 1) === " " ? 10 : 16);
      } else { cur.remove(); done(); }
    })();
  }

  function renderRich(bubble, s) {
    if (s.list) {
      var ul = document.createElement("ul"); ul.className = "tlist";
      s.list.forEach(function (it) { var li = document.createElement("li"); li.innerHTML = it; ul.appendChild(li); });
      bubble.appendChild(ul);
    } else if (s.kv) {
      var dl = document.createElement("dl"); dl.className = "kv";
      s.kv.forEach(function (pair) {
        var dt = document.createElement("dt"); dt.textContent = pair[0];
        var dd = document.createElement("dd"); dd.innerHTML = pair[1];
        dl.appendChild(dt); dl.appendChild(dd);
      });
      bubble.appendChild(dl);
    } else if (s.cmd) {
      var box = document.createElement("div"); box.className = "cmd-out";
      box.innerHTML = '<span class="c">' + (s.sigil || "$") + '</span><code>' + esc(s.cmd) + '</code>';
      var btn = document.createElement("button"); btn.className = "copy-btn"; btn.textContent = "copy";
      btn.addEventListener("click", function () { copy(s.cmd, btn); });
      box.appendChild(btn); bubble.appendChild(box);
    } else if (s.links) {
      var wrap = document.createElement("div");
      s.links.forEach(function (l) {
        var a = document.createElement("a"); a.className = "tlink"; a.href = l[1]; a.target = "_blank"; a.rel = "noopener";
        a.textContent = "› " + l[0];
        var line = document.createElement("div"); line.appendChild(a); wrap.appendChild(line);
      });
      bubble.appendChild(wrap);
    } else if (s.html) {
      var d = document.createElement("div"); d.innerHTML = s.html; bubble.appendChild(d);
    }
    scroll();
  }

  function copy(text, btn) {
    var done = function () { var t = btn.textContent; btn.textContent = "copied!"; btn.classList.add("copied"); setTimeout(function () { btn.textContent = "copy"; btn.classList.remove("copied"); }, 1500); };
    if (navigator.clipboard && navigator.clipboard.writeText) { navigator.clipboard.writeText(text).then(done).catch(fb); } else { fb(); }
    function fb() { var ta = document.createElement("textarea"); ta.value = text; ta.style.position = "fixed"; ta.style.opacity = "0"; document.body.appendChild(ta); ta.select(); try { document.execCommand("copy"); done(); } catch (e) {} document.body.removeChild(ta); }
  }

  // Run a list of steps as one Hina turn (typed lines + instant rich blocks).
  function say(steps, after) {
    busy = true; input.setAttribute("disabled", "");
    var bubble = newHinaBubble();
    var i = 0;
    (function next() {
      if (i >= steps.length) { busy = false; input.removeAttribute("disabled"); input.focus(); if (after) after(); return; }
      var s = steps[i++];
      if (typeof s === "string") { typeInto(bubble, s, next); }
      else { renderRich(bubble, s); next(); }
    })();
  }

  // ---------- command content ----------
  var FEATURES = [
    "⚡ <span class='pk'>rsync-like delta patching</span> — transfer only changed blocks",
    "🧬 content-defined chunking (FastCDC) — dedup across inserts/deletes",
    "✍️ Ed25519 manifest signing — verified before any patch",
    "🔎 per-chunk & per-file hashing — integrity at every stage",
    "🗜️ Brotli chunk storage — content-addressed, compressed",
    "🔁 retry with exponential backoff",
    "↩️ automatic backup & rollback",
    "🧵 concurrent downloads",
    "🛡️ Flatpak-style sandboxing — Linux/macOS/Windows",
    "🌐 static hosting (bundled host, or any CDN/Nginx/S3)",
    "📋 structured logging (<code>--verbose</code>)",
    "🔓 open source end-to-end (Apache-2.0)"
  ];

  var INSTALL = {
    linux: { sigil: "$", cmd: "curl -fsSL https://raw.githubusercontent.com/Arutosio/Hina/master/scripts/install.sh | bash", note: "Linux / macOS · lands in ~/.local/bin/hina" },
    macos: { sigil: "$", cmd: "curl -fsSL https://raw.githubusercontent.com/Arutosio/Hina/master/scripts/install.sh | bash", note: "macOS · same script as Linux" },
    windows: { sigil: ">", cmd: "iwr -useb https://raw.githubusercontent.com/Arutosio/Hina/master/scripts/install.ps1 | iex", note: "Windows PowerShell · installs to %LOCALAPPDATA%\\Hina\\bin, no admin" },
    scoop: { sigil: ">", cmd: "scoop install https://github.com/Arutosio/Hina/releases/latest/download/hina.json", note: "Scoop · auto-updates with scoop update" }
  };

  var COMMANDS = {
    help: function () {
      return [
        "Sure! Here's what you can ask me:",
        { kv: [
          ["about", "who I am, in short"],
          ["features", "what I can do"],
          ["install", "get me on your machine (install &lt;os&gt;)"],
          ["how", "how delta patching works"],
          ["sandbox", "how I isolate apps"],
          ["components", "the 5 parts I'm made of"],
          ["docs", "links to the deep dives"],
          ["version", "my version"],
          ["github", "open my repo"],
          ["theme", "switch light / dark"],
          ["clear", "wipe the screen"]
        ] },
        { html: "<span class='muted'>tip: tap a chip below, or just type. ✨</span>" }
      ];
    },
    about: function () {
      return [
        "Hi! I'm Hina — your delivery girl for updates. 📦",
        "I'm an open-source rsync-style patcher for game clients & desktop apps, and a cross-platform package manager. I ship only the chunks that changed, then sandbox apps before they run.",
        { kv: [["version", "v" + VERSION], ["license", "Apache-2.0"], ["runtime", ".NET 10 · NativeAOT"], ["author", "Arutosio"]] }
      ];
    },
    features: function () { return ["Here's what's in my toolbox 🧰", { list: FEATURES }]; },
    how: function () {
      return [
        "Build once, ship chunks — clients fetch only what they're missing. 🚚",
        { html: "<span class='cy'># build pipeline</span>" },
        { list: [
          "hina-builder scans your build & cuts content-defined chunks",
          "writes <span class='ok'>manifest.json</span> (files, hashes, chunk map, signature)",
          "writes <span class='ok'>chunks/</span> — Brotli blocks by content hash",
          "you upload to Hina.Host / Nginx / S3 / any CDN"
        ] },
        { html: "<span class='cy'># client patch</span>" },
        { list: [
          "fetch manifest → verify Ed25519 signature",
          "rolling-checksum scan of local files",
          "download missing chunks (concurrent · retry + backoff)",
          "rebuild → verify hash → swap in place (or rollback)"
        ] }
      ];
    },
    sandbox: function () {
      return [
        "Before a sandboxed app starts, I lock it to only its declared files & network. 🛡️",
        { list: [
          "<span class='cy'>Linux</span> → Landlock (unprivileged, no root)",
          "<span class='cy'>macOS</span> → Seatbelt (sandbox-exec)",
          "<span class='cy'>Windows</span> → AppContainer <span class='tag-new'>new 1.6</span>"
        ] },
        "If a platform can't enforce it, I warn you and run unsandboxed — I never block a launch."
      ];
    },
    components: function () {
      return [
        "I come in five neat little parts 🧩",
        { kv: [
          ["hina (CLI)", "install · update · run · perms · host"],
          ["Hina.PackageManager", "install engine, descriptors, signatures, sandboxes"],
          ["Hina.Builder", "build dir → manifest + chunk store"],
          ["Hina.Host", "ASP.NET Core static host (rate-limit, health, multi-app)"],
          ["Hina.Core", "the reusable library: chunking, checksum, verify, retry"]
        ] }
      ];
    },
    install: function (args) {
      var os = (args[0] || "").toLowerCase();
      if (os === "mac" || os === "osx") os = "macos";
      if (os === "win") os = "windows";
      if (os === "msi") {
        return ["Prefer a double-click installer? Grab the per-user MSI (x64/arm64): 💾",
          { links: [["Latest release (Hina-windows-*.msi)", REPO + "/releases/latest"]] }];
      }
      if (INSTALL[os]) {
        var t = INSTALL[os];
        return ["Here you go — " + t.note + " 👇", { cmd: t.cmd, sigil: t.sigil }, { html: "<span class='muted'>then run </span><span class='cy'>hina --help</span>" }];
      }
      // no/unknown arg → show all
      return [
        "Pop me on your machine in one line! Pick your platform 👇",
        { html: "<span class='muted'>Linux / macOS</span>" }, { cmd: INSTALL.linux.cmd, sigil: "$" },
        { html: "<span class='muted'>Windows · PowerShell</span>" }, { cmd: INSTALL.windows.cmd, sigil: ">" },
        { html: "<span class='muted'>Scoop</span>" }, { cmd: INSTALL.scoop.cmd, sigil: ">" },
        { html: "<span class='muted'>or </span><span class='cy'>install msi</span><span class='muted'> for the double-click installer · verify with the published .sha256 sums</span>" }
      ];
    },
    docs: function () {
      return ["Deep dives live in my docs 📚", { links: [
        ["Architecture", DOCS + "Architecture.md"],
        ["Package Manager Guide", DOCS + "PackageManager-Guide.md"],
        ["CLI Guide", DOCS + "CLI-Guide.md"],
        ["Builder Guide", DOCS + "Builder-Guide.md"],
        ["Host Guide", DOCS + "Host-Guide.md"],
        ["Security", DOCS + "Security.md"],
        ["Integration Guide", DOCS + "Integration-Guide.md"],
        ["Configuration", DOCS + "Configuration.md"],
        ["Troubleshooting", DOCS + "Troubleshooting.md"],
        ["Changelog", DOCS + "Changelog.md"]
      ] }];
    },
    version: function () { return ["hina v" + VERSION + " · .NET 10 · Apache-2.0"]; },
    github: function () {
      window.open(REPO, "_blank", "noopener");
      return ["Opening my repo — give me a ★ if you like me!", { links: [["github.com/Arutosio/Hina", REPO]] }];
    },
    changelog: function () { return ["What's new lately:", { links: [["Changelog", DOCS + "Changelog.md"]] }]; },
    theme: function () {
      var root = document.documentElement;
      var next = root.getAttribute("data-theme") === "dark" ? "light" : "dark";
      root.setAttribute("data-theme", next);
      try { localStorage.setItem("hina-theme", next); } catch (e) {}
      return ["Switched to " + next + " mode ✨"];
    }
  };
  // aliases
  COMMANDS.who = COMMANDS.about; COMMANDS.whoami = COMMANDS.about; COMMANDS.hina = COMMANDS.about;
  COMMANDS["?"] = COMMANDS.help; COMMANDS.ls = COMMANDS.help; COMMANDS.commands = COMMANDS.help;
  COMMANDS.feature = COMMANDS.features; COMMANDS.repo = COMMANDS.github; COMMANDS.star = COMMANDS.github;

  function handle(raw) {
    var line = raw.trim();
    if (!line) return;
    history.push(line); hIndex = history.length;
    printUser(line);
    var parts = line.split(/\s+/);
    var name = parts[0].toLowerCase();
    var args = parts.slice(1);

    if (name === "clear" || name === "cls") { log.innerHTML = ""; return; }
    var fn = COMMANDS[name];
    if (fn) { say(fn(args)); return; }
    say([
      "Hmm, I don't know “" + line + "” yet 🤔",
      { html: "<span class='muted'>try </span><span class='cy'>help</span><span class='muted'> to see what I understand.</span>" }
    ]);
  }

  // ---------- chips ----------
  var CHIPS = ["help", "about", "features", "install", "how", "sandbox", "docs", "github"];
  CHIPS.forEach(function (c) {
    var b = document.createElement("button");
    b.className = "chip"; b.type = "button"; b.textContent = c;
    b.addEventListener("click", function () { if (busy) return; input.value = ""; handle(c); });
    chipsBox.appendChild(b);
  });

  // ---------- input ----------
  form.addEventListener("submit", function (e) {
    e.preventDefault();
    if (busy) return;
    var v = input.value; input.value = "";
    handle(v);
  });
  input.addEventListener("keydown", function (e) {
    if (e.key === "ArrowUp") { if (hIndex > 0) { hIndex--; input.value = history[hIndex] || ""; e.preventDefault(); } }
    else if (e.key === "ArrowDown") { if (hIndex < history.length) { hIndex++; input.value = history[hIndex] || ""; } }
  });
  // clicking anywhere in the terminal focuses the prompt
  log.addEventListener("click", function (e) { if (e.target.tagName !== "A" && e.target.tagName !== "BUTTON" && window.getSelection().toString() === "") input.focus(); });

  if (themeBtn) themeBtn.addEventListener("click", function () { say(COMMANDS.theme([])); });

  // ---------- boot ----------
  function boot() {
    var b = document.createElement("div"); b.className = "row muted";
    b.innerHTML = "hina v" + VERSION + " · type <span class='cy'>help</span> or tap a command below ✨";
    log.appendChild(b);
    say([
      "Hi! I'm Hina — your delivery girl for updates. 📦✨",
      "I ship only the chunks that changed, and I sandbox apps before they run.",
      { html: "<span class='muted'>Ask me things like </span><span class='cy'>install</span><span class='muted'>, </span><span class='cy'>features</span><span class='muted'>, or </span><span class='cy'>how</span><span class='muted'> — or type </span><span class='cy'>help</span><span class='muted'>.</span>" }
    ]);
  }
  boot();

  // Optional deep-link: ?cmd=install runs a command once the greeting finishes.
  try {
    var pre = new URLSearchParams(location.search).get("cmd");
    if (pre) { var iv = setInterval(function () { if (!busy) { clearInterval(iv); input.value = ""; handle(pre); } }, 120); }
  } catch (e) {}
})();
