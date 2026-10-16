/* MdViewer — page logic: rendering, table of contents, find, theme. */
(function () {
  "use strict";

  var host = window.chrome && window.chrome.webview ? window.chrome.webview : null;
  var docEl = document.getElementById("doc");
  var scroller = document.getElementById("scroller");
  var tocEl = document.getElementById("toc");
  var nameEl = document.getElementById("doc-name");
  var versionEl = document.getElementById("empty-version");
  var findBar = document.getElementById("findbar");
  var findInput = document.getElementById("find-input");
  var findCount = document.getElementById("find-count");
  var themeButton = document.getElementById("btn-theme");

  /* A heading counts as current once it reaches this far below the top of the page. */
  var ACTIVE_MARGIN = 44;
  /* A one-letter query in a big document can match tens of thousands of times, and
     every match costs a Range. Past this point the counter just says "and more". */
  var MAX_MATCHES = 4000;

  var theme = "system";
  var baseUrl = "";
  var headings = [];
  var headingOffsets = [];
  var activeIndex = -1;
  var matches = [];
  var matchIndex = -1;
  var matchesCapped = false;
  var textNodes = null;
  var remeasureQueued = false;

  function post(message) {
    if (host) host.postMessage(message);
  }

  /* Distance from the top of the scrollable content, robust to positioned ancestors. */
  function offsetWithin(element) {
    var top = 0;
    while (element && element !== scroller) {
      top += element.offsetTop;
      element = element.offsetParent;
    }
    return top;
  }

  /* ---------- Theme ---------- */

  function resolveTheme(value) {
    if (value === "dark" || value === "light") return value;
    return window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
  }

  var THEME_TITLES = {
    system: "Theme: follows Windows (Ctrl+D)",
    light: "Theme: light (Ctrl+D)",
    dark: "Theme: dark (Ctrl+D)"
  };

  function applyTheme(value) {
    theme = value;
    document.documentElement.dataset.theme = resolveTheme(value);
    document.documentElement.dataset.themeMode = value;
    themeButton.title = THEME_TITLES[value] || THEME_TITLES.system;
  }

  /* Light, dark, then back to following Windows: three states, one key. */
  function cycleTheme() {
    var next = theme === "light" ? "dark" : theme === "dark" ? "system" : "light";
    applyTheme(next);
    post({ type: "theme", value: next });
  }

  /* ---------- Table of contents ---------- */

  function setTocVisible(visible) {
    document.body.classList.toggle("no-toc", !visible);
    post({ type: "toc", value: visible });
    scheduleRemeasure();
  }

  function slugify(text, used) {
    var base = text.toLowerCase().replace(/[^\p{L}\p{N}]+/gu, "-").replace(/^-|-$/g, "") || "section";
    var id = base;
    var n = 2;
    while (used[id]) id = base + "-" + n++;
    used[id] = true;
    return id;
  }

  function buildToc() {
    headings = Array.prototype.slice.call(docEl.querySelectorAll("h1, h2, h3, h4"));
    tocEl.textContent = "";
    activeIndex = -1;

    if (headings.length === 0) {
      var note = document.createElement("div");
      note.className = "toc-empty";
      note.textContent = "No headings";
      tocEl.appendChild(note);
      headingOffsets = [];
      return;
    }

    var used = {};
    var top = Math.min.apply(null, headings.map(function (h) { return Number(h.tagName[1]); }));
    var list = document.createDocumentFragment();

    headings.forEach(function (heading) {
      if (!heading.id) heading.id = slugify(heading.textContent, used);
      var link = document.createElement("a");
      link.href = "#" + heading.id;
      link.textContent = heading.textContent;
      link.className = "lvl-" + Math.min(Number(heading.tagName[1]) - top, 4);
      list.appendChild(link);
    });

    tocEl.appendChild(list);
    measureHeadings();
  }

  /* Heading positions are read once here instead of on every scroll frame: asking
     each heading for its rectangle while scrolling forced a layout per frame, which
     is what made long documents stutter. */
  function measureHeadings() {
    headingOffsets = headings.map(offsetWithin);
    activeIndex = -1;
    updateActiveHeading();
  }

  function scheduleRemeasure() {
    if (remeasureQueued || headings.length === 0) return;
    remeasureQueued = true;
    requestAnimationFrame(function () {
      remeasureQueued = false;
      measureHeadings();
    });
  }

  function updateActiveHeading() {
    if (headingOffsets.length === 0) return;

    var limit = scroller.scrollTop + ACTIVE_MARGIN;
    var low = 0;
    var high = headingOffsets.length - 1;
    var found = 0;

    while (low <= high) {
      var mid = (low + high) >> 1;
      if (headingOffsets[mid] <= limit) {
        found = mid;
        low = mid + 1;
      } else {
        high = mid - 1;
      }
    }

    if (found === activeIndex) return;
    activeIndex = found;

    var links = tocEl.children;
    for (var i = 0; i < links.length; i++) links[i].classList.toggle("active", i === found);

    var active = links[found];
    if (!active || !active.scrollIntoView) return;
    var box = active.getBoundingClientRect();
    var pane = tocEl.parentElement.getBoundingClientRect();
    if (box.top < pane.top + 8 || box.bottom > pane.bottom - 8) {
      active.scrollIntoView({ block: "nearest" });
    }
  }

  function scrollToElement(element) {
    scroller.scrollTo({ top: Math.max(offsetWithin(element) - 16, 0), behavior: "smooth" });
  }

  /* ---------- Rendering ---------- */

  /* Two or more characters before the colon: a real scheme, not a "C:" drive letter. */
  var SCHEME = /^[a-z][a-z0-9+.-]+:/i;
  var ABSOLUTE = /^([a-z][a-z0-9+.-]+:|\/\/|#)/i;

  /* Relative paths are resolved against the document's own folder, which the host
     spells out in baseUrl. A backslash in a path is a Windows habit, not a URL. */
  function resolveMedia(path) {
    if (!path || !baseUrl || ABSOLUTE.test(path)) return path;
    try {
      return new URL(path.replace(/\\/g, "/"), baseUrl).href;
    } catch (error) {
      return path;
    }
  }

  function rewriteMedia() {
    docEl.querySelectorAll("img[src], source[src], video[src], audio[src], video[poster]").forEach(function (element) {
      if (element.hasAttribute("src")) element.setAttribute("src", resolveMedia(element.getAttribute("src")));
      if (element.hasAttribute("poster")) element.setAttribute("poster", resolveMedia(element.getAttribute("poster")));
    });
    docEl.querySelectorAll("img[srcset], source[srcset]").forEach(function (element) {
      var rewritten = element.getAttribute("srcset").split(",").map(function (candidate) {
        var parts = candidate.trim().split(/\s+/);
        parts[0] = resolveMedia(parts[0]);
        return parts.join(" ");
      }).join(", ");
      element.setAttribute("srcset", rewritten);
    });
  }

  /* Raw HTML in the document is allowed to style and structure, not to steer the
     page. A <meta http-equiv="refresh"> would navigate without a click (the host
     refuses that navigation too); <base> would silently repoint every link. The
     policy header blocks the rest. */
  function stripControlElements() {
    docEl.querySelectorAll("meta, base, link, iframe, object, embed, form").forEach(function (element) {
      element.remove();
    });
  }

  function highlightBlock(block) {
    try {
      window.hljs.highlightElement(block);
    } catch (error) {
      block.classList.add("hljs"); /* An unsupported language just stays unstyled. */
    }
  }

  /* Measured on a document with 400 fenced blocks: highlighting the lot costs about
     18 ms, so it is done up front. Deferring it until each block scrolls into view
     saved that 18 ms and cost an observer plus a stale find index. */
  function highlightCode() {
    if (!window.hljs) return;
    docEl.querySelectorAll("pre code:not(.hljs)").forEach(highlightBlock);
  }

  function render(message) {
    var scrollTop = message.keepScroll ? scroller.scrollTop : 0;
    baseUrl = message.baseUrl || "";

    clearFind();
    textNodes = null;
    docEl.innerHTML = message.html;
    document.body.classList.remove("empty");
    nameEl.textContent = message.name || "";
    nameEl.title = message.path || "";

    stripControlElements();
    rewriteMedia();
    scroller.scrollTop = scrollTop;
    /* Highlight before measuring: the table of contents caches heading positions and
       must not read them while the code blocks are still being rewritten. */
    highlightCode();
    buildToc();

    scroller.focus({ preventScroll: true });
    if (!findBar.hidden && findInput.value) newSearch(findInput.value, false);
  }

  function showError(text) {
    clearFind();
    textNodes = null;
    docEl.textContent = "";
    var box = document.createElement("div");
    box.className = "load-error";
    box.textContent = text;
    docEl.appendChild(box);
    document.body.classList.remove("empty");
    tocEl.textContent = "";
    headings = [];
    headingOffsets = [];
  }

  /* ---------- Find ---------- */

  var supportsHighlight = typeof CSS !== "undefined" && CSS.highlights;

  function clearFind() {
    matches = [];
    matchIndex = -1;
    matchesCapped = false;
    if (supportsHighlight) {
      CSS.highlights.delete("find-all");
      CSS.highlights.delete("find-current");
    }
    findCount.textContent = "0/0";
  }

  /* Walking the whole document per keystroke was the expensive part of typing in the
     find bar, so the node list is kept until the document changes. */
  function documentTextNodes() {
    if (textNodes) return textNodes;

    var walker = document.createTreeWalker(docEl, NodeFilter.SHOW_TEXT, {
      acceptNode: function (node) {
        if (!node.nodeValue || !node.nodeValue.trim()) return NodeFilter.FILTER_REJECT;
        var parent = node.parentElement;
        if (!parent || parent.closest("script, style")) return NodeFilter.FILTER_REJECT;
        return NodeFilter.FILTER_ACCEPT;
      },
    });

    textNodes = [];
    var node;
    while ((node = walker.nextNode())) textNodes.push(node);
    return textNodes;
  }

  function showCount() {
    if (matches.length === 0) {
      findCount.textContent = "0/0";
      return;
    }
    findCount.textContent = (matchIndex + 1) + "/" + matches.length + (matchesCapped ? "+" : "");
  }

  function search(query) {
    clearFind();
    if (!query) return;

    var needle = query.toLowerCase();
    var nodes = documentTextNodes();

    for (var i = 0; i < nodes.length && !matchesCapped; i++) {
      var node = nodes[i];
      var text = node.nodeValue.toLowerCase();
      var from = 0;
      var at;
      while ((at = text.indexOf(needle, from)) !== -1) {
        var range = document.createRange();
        range.setStart(node, at);
        range.setEnd(node, at + needle.length);
        matches.push(range);
        from = at + needle.length;
        if (matches.length >= MAX_MATCHES) {
          matchesCapped = true;
          break;
        }
      }
    }

    if (supportsHighlight && matches.length) {
      CSS.highlights.set("find-all", new Highlight(...matches));
    }
  }

  /* A query the user just typed jumps to the match nearest where they are looking.
     Pass scroll false when the document was reloaded under them: the matches have to
     be rebuilt, but moving the page they were reading would be rude. */
  function newSearch(query, scroll) {
    search(query);
    if (matches.length === 0) {
      showCount();
      return;
    }
    matchIndex = nearestMatch();
    focusMatch(scroll === false ? null : "instant");
  }

  function nearestMatch() {
    for (var i = 0; i < matches.length; i++) {
      if (matches[i].getBoundingClientRect().top > 60) return i;
    }
    return 0;
  }

  function focusMatch(behavior) {
    if (matches.length === 0) return;
    showCount();

    if (supportsHighlight) {
      CSS.highlights.set("find-current", new Highlight(matches[matchIndex]));
    }
    if (!behavior) return;

    var box = matches[matchIndex].getBoundingClientRect();
    var target = scroller.scrollTop + box.top - scroller.clientHeight / 2;
    scroller.scrollTo({ top: Math.max(target, 0), behavior: behavior });
  }

  function step(delta) {
    if (matches.length === 0) return;
    matchIndex = (matchIndex + delta + matches.length) % matches.length;
    focusMatch("smooth");
  }

  function openFind() {
    findBar.hidden = false;
    findInput.focus();
    findInput.select();
  }

  function closeFind() {
    findBar.hidden = true;
    clearFind();
    findInput.value = "";
  }

  /* ---------- Wiring ---------- */

  document.getElementById("btn-toc").addEventListener("click", function () {
    setTocVisible(document.body.classList.contains("no-toc"));
  });
  document.getElementById("btn-open").addEventListener("click", function () {
    post({ type: "open" });
  });
  themeButton.addEventListener("click", cycleTheme);
  document.getElementById("btn-find").addEventListener("click", openFind);
  document.getElementById("find-next").addEventListener("click", function () { step(1); });
  document.getElementById("find-prev").addEventListener("click", function () { step(-1); });
  document.getElementById("find-close").addEventListener("click", closeFind);

  var findTimer = 0;
  findInput.addEventListener("input", function () {
    clearTimeout(findTimer);
    findTimer = setTimeout(function () { newSearch(findInput.value); }, 120);
  });

  findInput.addEventListener("keydown", function (event) {
    if (event.key === "Enter") {
      event.preventDefault();
      step(event.shiftKey ? -1 : 1);
    } else if (event.key === "Escape") {
      event.preventDefault();
      closeFind();
    }
  });

  docEl.addEventListener("click", function (event) {
    var link = event.target.closest ? event.target.closest("a[href]") : null;
    if (!link) return;
    event.preventDefault();

    var href = link.getAttribute("href");
    if (!href) return;

    if (href.charAt(0) === "#") {
      var target = null;
      try {
        target = document.getElementById(decodeURIComponent(href.slice(1)));
      } catch (error) {
        target = document.getElementById(href.slice(1));
      }
      if (target) scrollToElement(target);
    } else if (SCHEME.test(href)) {
      post({ type: "external", url: href });
    } else {
      post({ type: "openPath", path: href });
    }
  });

  document.addEventListener("keydown", function (event) {
    var ctrl = event.ctrlKey || event.metaKey;
    var key = event.key.toLowerCase();

    if (ctrl && key === "f") { event.preventDefault(); openFind(); }
    else if (ctrl && key === "o") { event.preventDefault(); post({ type: "open" }); }
    else if (ctrl && key === "b") { event.preventDefault(); setTocVisible(document.body.classList.contains("no-toc")); }
    else if (ctrl && key === "d") { event.preventDefault(); cycleTheme(); }
    else if (event.key === "F3") { event.preventDefault(); step(event.shiftKey ? -1 : 1); }
    else if (event.key === "Escape" && !findBar.hidden) { event.preventDefault(); closeFind(); }
  });

  var ticking = false;
  scroller.addEventListener("scroll", function () {
    if (ticking) return;
    ticking = true;
    requestAnimationFrame(function () {
      ticking = false;
      updateActiveHeading();
    });
  }, { passive: true });

  /* Images finishing, the window resizing and the sidebar opening all move the
     headings; one observer covers the lot. */
  if (window.ResizeObserver) {
    new ResizeObserver(scheduleRemeasure).observe(docEl);
  } else {
    window.addEventListener("resize", scheduleRemeasure);
  }

  window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", function () {
    if (theme === "system") applyTheme("system");
  });

  if (host) {
    host.addEventListener("message", function (event) {
      var message = event.data;
      if (!message || !message.type) return;

      if (message.type === "init") {
        applyTheme(message.theme || "system");
        document.body.classList.toggle("no-toc", message.tocVisible === false);
        if (versionEl && message.version) versionEl.textContent = "MdViewer " + message.version;
      } else if (message.type === "document") {
        render(message);
      } else if (message.type === "error") {
        showError(message.message || "Unknown error");
      }
    });
  }

  document.body.classList.add("empty");
  scroller.focus({ preventScroll: true });
  post({ type: "ready" });
})();
