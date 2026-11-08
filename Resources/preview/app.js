'use strict';

/*
 * Markdown preview renderer.
 *
 * The native side calls:
 *   render(markdown, topLine, atBottom)  - incremental re-render, then re-sync scroll
 *   setBase(url)                         - folder of the open document (relative images/links)
 *   syncToLine(line, atBottom)           - editor scrolled; follow it
 *
 * and receives messages on window.webkit.messageHandlers:
 *   ready            - scripts are loaded
 *   scroll {line, bottom} - the user scrolled the preview; editor should follow
 *   link  "href"     - a link was clicked
 */
(function () {
  const content = document.getElementById('content');

  const post = (name, payload) => {
    try { window.webkit.messageHandlers[name].postMessage(payload); } catch (_) { /* running outside the app */ }
  };

  const escapeHtml = (s) =>
    String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

  // ---------------------------------------------------------------- marked

  // $$ ... $$ on its own lines (display math)
  const mathBlock = {
    name: 'mathBlock',
    level: 'block',
    start(src) {
      const m = /(?:^|\n) {0,3}\$\$/.exec(src);
      return m ? m.index + (m[0].startsWith('\n') ? 1 : 0) : undefined;
    },
    tokenizer(src) {
      const m = /^ {0,3}\$\$[ \t]*\n?([\s\S]+?)\n?[ \t]*\$\$[ \t]*(?:\n|$)/.exec(src);
      if (m && m[1].trim()) return { type: 'mathBlock', raw: m[0], text: m[1].trim() };
      return undefined;
    },
    renderer(t) {
      return `<div class="math-display" data-tex="${escapeHtml(t.text)}">${escapeHtml(t.text)}</div>\n`;
    },
  };

  // $x$ (inline) and $$x$$ (inline, displayed)
  const mathInline = {
    name: 'mathInline',
    level: 'inline',
    start(src) {
      const i = src.indexOf('$');
      return i < 0 ? undefined : i;
    },
    tokenizer(src) {
      let m = /^\$\$([^\n]+?)\$\$/.exec(src);
      if (m) return { type: 'mathInline', raw: m[0], text: m[1].trim(), display: true };
      // Opening $ must be followed by non-space, closing $ preceded by non-space and not
      // followed by a digit, so prices like "$5 and $10" stay plain text.
      m = /^\$(?!\s)((?:\\.|[^\\$\n])+?)(?<!\s)\$(?!\d)/.exec(src);
      if (m) return { type: 'mathInline', raw: m[0], text: m[1], display: false };
      return undefined;
    },
    renderer(t) {
      const cls = t.display ? 'math-display' : 'math-inline';
      const tag = t.display ? 'div' : 'span';
      return `<${tag} class="${cls}" data-tex="${escapeHtml(t.text)}">${escapeHtml(t.text)}</${tag}>`;
    },
  };

  marked.use({
    gfm: true,
    breaks: false,
    extensions: [mathBlock, mathInline],
    renderer: {
      code({ text, lang }) {
        const l = ((lang || '').match(/^\S*/) || [''])[0].toLowerCase();
        let body;
        if (l && hljs.getLanguage(l)) {
          try { body = hljs.highlight(text, { language: l, ignoreIllegals: true }).value; }
          catch (_) { body = escapeHtml(text); }
        } else {
          body = escapeHtml(text);
        }
        const cls = l ? ` language-${escapeHtml(l)}` : '';
        return `<pre><code class="hljs${cls}">${body}</code></pre>\n`;
      },
    },
  });

  // ---------------------------------------------------------------- post-processing

  const ALERTS = { NOTE: 'Note', TIP: 'Tip', IMPORTANT: 'Important', WARNING: 'Warning', CAUTION: 'Caution' };

  function enhanceAlert(bq) {
    const p = bq.firstElementChild;
    if (!p || p.tagName !== 'P') return;
    const first = p.firstChild;
    if (!first || first.nodeType !== 3) return;
    const m = /^\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\][ \t]*\n?/i.exec(first.nodeValue);
    if (!m) return;
    const kind = m[1].toUpperCase();
    first.nodeValue = first.nodeValue.slice(m[0].length);
    bq.classList.add('alert', 'alert-' + kind.toLowerCase());
    const title = document.createElement('p');
    title.className = 'alert-title';
    title.textContent = ALERTS[kind];
    bq.insertBefore(title, bq.firstChild);
    if (!p.textContent.trim() && !p.children.length) p.remove();
  }

  function renderMath(root) {
    const nodes = [];
    if (root.matches && root.matches('[data-tex]')) nodes.push(root);
    if (root.querySelectorAll) nodes.push(...root.querySelectorAll('[data-tex]'));
    for (const el of nodes) {
      try {
        katex.render(el.getAttribute('data-tex'), el, {
          displayMode: el.classList.contains('math-display'),
          throwOnError: false,
          strict: 'ignore',
          output: 'html',
        });
      } catch (_) { /* leave the raw TeX visible */ }
    }
  }

  function enhance(el) {
    if (el.nodeType !== 1) return;
    if (el.tagName === 'BLOCKQUOTE') enhanceAlert(el);
    for (const li of el.querySelectorAll('li')) {
      const box = li.firstElementChild && li.firstElementChild.matches('input[type="checkbox"]')
        ? li.firstElementChild
        : li.querySelector(':scope > p:first-child > input[type="checkbox"]');
      if (box) {
        li.classList.add('task-list-item');
        if (li.parentElement) li.parentElement.classList.add('contains-task-list');
      }
    }
    renderMath(el);
  }

  // ---------------------------------------------------------------- incremental rendering

  /** @type {{key:string, startLine:number, tok:any, fm?:string, els:Element[]}[]} */
  let blocks = [];
  let totalLines = 1;

  function splitFrontMatter(md) {
    const m = /^---[ \t]*\r?\n([\s\S]*?)\r?\n(?:---|\.\.\.)[ \t]*(?:\r?\n|$)/.exec(md);
    if (!m) return { fm: null, body: md, offset: 0 };
    return { fm: m[1], body: md.slice(m[0].length), offset: (m[0].match(/\n/g) || []).length };
  }

  function renderBlock(b) {
    let html;
    if (b.fm !== undefined) {
      html = `<pre class="frontmatter"><code>${escapeHtml(b.fm)}</code></pre>`;
    } else {
      try { html = marked.parser([b.tok]); }
      catch (e) { html = `<pre class="render-error">${escapeHtml(e && e.message || e)}</pre>`; }
    }
    const frag = DOMPurify.sanitize(html, { RETURN_DOM_FRAGMENT: true });
    const els = [];
    for (const n of Array.from(frag.childNodes)) {
      if (n.nodeType === 1) els.push(n);
      else if (n.nodeType === 3 && n.nodeValue.trim()) {
        const p = document.createElement('p');
        p.appendChild(n);
        els.push(p);
      }
    }
    els.forEach(enhance);
    return els;
  }

  function slug(text) {
    return text.trim().toLowerCase()
      .replace(/[^\p{L}\p{N}\s_-]/gu, '')
      .replace(/\s/g, '-');
  }

  function assignHeadingIds() {
    const seen = new Map();
    for (const h of content.querySelectorAll('h1,h2,h3,h4,h5,h6')) {
      const base = slug(h.textContent) || 'section';
      const n = seen.get(base) || 0;
      seen.set(base, n + 1);
      h.id = n ? `${base}-${n}` : base;
    }
  }

  function render(md, topLine, atBottom) {
    const { fm, body, offset } = splitFrontMatter(md);
    const tokens = marked.lexer(body);
    const linksSig = JSON.stringify(tokens.links || {});

    const next = [];
    let line = offset;
    if (fm !== null) next.push({ key: 'fm\u0001' + fm, startLine: 0, fm, els: [] });
    for (const tok of tokens) {
      const nl = (tok.raw.match(/\n/g) || []).length;
      if (tok.type !== 'space') next.push({ key: tok.raw + '\u0001' + linksSig, startLine: line, tok, els: [] });
      line += nl;
    }
    totalLines = Math.max(line + 1, 1);

    // Reuse the unchanged head and tail; only re-render what is in between.
    let head = 0;
    while (head < blocks.length && head < next.length && blocks[head].key === next[head].key) {
      next[head].els = blocks[head].els;
      head++;
    }
    let tail = 0;
    while (tail < blocks.length - head && tail < next.length - head &&
           blocks[blocks.length - 1 - tail].key === next[next.length - 1 - tail].key) {
      next[next.length - 1 - tail].els = blocks[blocks.length - 1 - tail].els;
      tail++;
    }

    for (let i = head; i < blocks.length - tail; i++) blocks[i].els.forEach((e) => e.remove());

    let anchor = null;
    for (let i = next.length - tail; i < next.length && !anchor; i++) anchor = next[i].els[0] || null;

    for (let i = head; i < next.length - tail; i++) {
      next[i].els = renderBlock(next[i]);
      for (const e of next[i].els) content.insertBefore(e, anchor);
    }
    for (const b of next) b.tok = null;
    blocks = next;

    assignHeadingIds();
    if (typeof topLine === 'number') syncToLine(topLine, !!atBottom);
  }

  function setBase(url) {
    let base = document.querySelector('base');
    if (!base) {
      base = document.createElement('base');
      document.head.prepend(base);
    }
    if (base.href !== url) {
      base.href = url;
      // Relative URLs were resolved against the old folder: rebuild everything.
      blocks.forEach((b) => b.els.forEach((e) => e.remove()));
      blocks = [];
    }
  }

  // ---------------------------------------------------------------- scroll sync (by source line)

  let suppressUntil = 0;
  let ticking = false;

  const visibleBlocks = () => blocks.filter((b) => b.els.length);
  const topOf = (b) => b.els[0].getBoundingClientRect().top + window.scrollY;
  const maxScroll = () => Math.max(document.documentElement.scrollHeight - window.innerHeight, 0);
  const clamp = (v, lo, hi) => Math.min(Math.max(v, lo), hi);

  function endYOf(vb, i) {
    if (vb[i + 1]) return topOf(vb[i + 1]);
    const last = vb[i].els[vb[i].els.length - 1];
    return last.getBoundingClientRect().bottom + window.scrollY;
  }

  function lineToY(x) {
    const vb = visibleBlocks();
    if (!vb.length) return 0;
    let lo = 0, hi = vb.length - 1;
    while (lo < hi) {
      const mid = (lo + hi + 1) >> 1;
      if (vb[mid].startLine <= x) lo = mid; else hi = mid - 1;
    }
    const top = topOf(vb[lo]);
    const endY = endYOf(vb, lo);
    const endLine = vb[lo + 1] ? vb[lo + 1].startLine : totalLines;
    const frac = clamp((x - vb[lo].startLine) / Math.max(endLine - vb[lo].startLine, 1), 0, 1);
    return top + frac * (endY - top);
  }

  function yToLine(y) {
    const vb = visibleBlocks();
    if (!vb.length) return 0;
    let lo = 0, hi = vb.length - 1;
    while (lo < hi) {
      const mid = (lo + hi + 1) >> 1;
      if (topOf(vb[mid]) <= y) lo = mid; else hi = mid - 1;
    }
    const top = topOf(vb[lo]);
    const endY = endYOf(vb, lo);
    const endLine = vb[lo + 1] ? vb[lo + 1].startLine : totalLines;
    const frac = clamp((y - top) / Math.max(endY - top, 1), 0, 1);
    return vb[lo].startLine + frac * (endLine - vb[lo].startLine);
  }

  function syncToLine(line, atBottom) {
    const y = atBottom ? maxScroll() : clamp(lineToY(line), 0, maxScroll());
    if (Math.abs(window.scrollY - y) < 0.5) return;
    suppressUntil = performance.now() + 150;
    window.scrollTo(0, y);
  }

  window.addEventListener('scroll', () => {
    if (performance.now() < suppressUntil || ticking) return;
    ticking = true;
    requestAnimationFrame(() => {
      ticking = false;
      if (performance.now() < suppressUntil) return;
      const bottom = maxScroll() > 0 && window.scrollY >= maxScroll() - 1;
      post('scroll', { line: yToLine(window.scrollY), bottom });
    });
  }, { passive: true });

  // ---------------------------------------------------------------- links

  document.addEventListener('click', (e) => {
    const a = e.target.closest && e.target.closest('a');
    if (!a) return;
    e.preventDefault();
    const href = a.getAttribute('href');
    if (!href) return;
    if (href.startsWith('#')) {
      let id = href.slice(1);
      try { id = decodeURIComponent(id); } catch (_) { /* keep raw */ }
      const target = document.getElementById(id);
      if (target) target.scrollIntoView();
      return;
    }
    post('link', href);
  });

  // Never let the preview navigate away or accept drops.
  document.addEventListener('dragover', (e) => e.preventDefault());
  document.addEventListener('drop', (e) => e.preventDefault());

  window.render = render;
  window.setBase = setBase;
  window.syncToLine = syncToLine;
  post('ready', true);
})();
