import { groups, repository } from './content.mjs';

export function escapeHtml(value) {
  return String(value).replace(/[&<>"']/g, character =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[character]);
}

const paths = {
  arrow: '<path d="M5 12h14m-6-6 6 6-6 6"/>',
  download: '<path d="M12 3v12m-5-5 5 5 5-5M4 16v5h16v-5"/>',
  search: '<circle cx="10.5" cy="10.5" r="6.5"/><path d="m16 16 5 5"/>',
  theme: '<path d="M20 14A8 8 0 0 1 10 4a8 8 0 1 0 10 10Z"/>',
  menu: '<path d="M4 6h16M4 12h16M4 18h16"/>',
  close: '<path d="m6 6 12 12M18 6 6 18"/>',
  capture: '<path d="M8 3H3v5m13-5h5v5M3 16v5h5m13-5v5h-5"/><rect x="7" y="7" width="10" height="10" rx="2"/>',
  edit: '<path d="m4 16 12-12 4 4L8 20H4v-4Zm10-10 4 4"/>',
  record: '<rect x="3" y="5" width="13" height="14" rx="3"/><path d="m16 9 5-3v12l-5-3"/>',
  compare: '<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M12 4v16M6 10h3m6 4h3"/>',
  folder: '<path d="M3 7V5h6l2 2h10v13H3V7Z"/>',
  shield: '<path d="m12 3 8 3v6c0 5-8 9-8 9s-8-4-8-9V6l8-3Z"/><path d="m8 12 3 3 5-6"/>',
  check: '<path d="m5 12 4 4L19 6"/>',
  code: '<path d="m8 5-6 7 6 7m8-14 6 7-6 7m-3-16-2 18"/>',
  sync: '<path d="M20 8a8 8 0 0 0-14-3L3 8m0-5v5h5M4 16a8 8 0 0 0 14 3l3-3m0 5v-5h-5"/>',
};
export const icon = name => `<svg class="icon" viewBox="0 0 24 24" aria-hidden="true">${paths[name]}</svg>`;

export function header({ base, logo, docs = false }) {
  return `<a class="skip-link" href="#main">Skip to content</a>
  <header class="site-header"><div class="container header-inner">
    <a class="brand" href="${base}" aria-label="Hotshot home"><img src="${logo}" alt="" width="36" height="36">Hotshot</a>
    <nav class="site-nav" id="site-nav" aria-label="Main navigation">
      <a href="${base}#features">Features</a><a href="${base}docs/"${docs ? ' aria-current="page"' : ''}>Documentation</a>
      <a href="${repository}">GitHub</a>
    </nav>
    <div class="header-tools">
      <a class="search-trigger" href="${base}docs/#browse" data-search-open aria-label="Search documentation">${icon('search')}<span>Search docs</span><kbd>Ctrl K</kbd></a>
      <button class="icon-button" data-theme-toggle aria-label="Switch color theme" title="Switch color theme">${icon('theme')}</button>
      <a class="button primary compact header-download" href="${base}#download">Download</a>
      <button class="icon-button nav-toggle" data-nav-toggle aria-controls="site-nav" aria-expanded="false" aria-label="Open navigation">${icon('menu')}</button>
    </div>
  </div></header>`;
}

export function footer({ base, version, logo }) {
  return `<footer class="site-footer"><div class="container footer-inner">
    <div><a class="brand" href="${base}"><img src="${logo}" alt="" width="30" height="30">Hotshot</a>
      <p>Native Windows capture. Open source. Yours to control.</p><p>Release ${escapeHtml(version)} · x64 + ARM64 · MIT license</p></div>
    <nav class="footer-links" aria-label="Footer navigation"><a href="${base}docs/">Documentation</a>
      <a href="${base}docs/contributing/">Contribute</a><a href="${base}docs/changelog/">Changelog</a>
      <a href="${base}docs/privacy/">Privacy</a><a href="${repository}/issues">Report an issue</a></nav>
  </div></footer>`;
}

export function searchDialog() {
  return `<dialog class="search-dialog" aria-labelledby="search-title">
    <div class="search-top"><div class="search-label-row"><span id="search-title">Search the Hotshot docs</span>
      <button class="icon-button" data-search-close aria-label="Close search">${icon('close')}</button></div>
      <label for="docs-query" class="small">What do you want to do?</label>
      <input class="search-input" id="docs-query" type="search" placeholder="Try OneDrive, redaction, filename tokens..." autocomplete="off" spellcheck="false"></div>
    <p class="search-status" role="status" aria-live="polite">Search guides, settings, tools, and troubleshooting.</p>
    <ul class="search-results" aria-label="Search results"></ul>
    <p class="search-hint">Ctrl/Cmd + K to search · Arrow keys to explore · Enter to open · Escape to close</p>
  </dialog><div class="toast" role="status" aria-live="polite" hidden></div>`;
}

export function home({ base, version, logo }) {
  const features = [
    ['capture', 'Capture without the interruption', 'Regions, windows, one monitor, or every monitor. Copy and save without popping open an editor.', 'screenshots', 'Explore capture'],
    ['edit', 'Make the point, not a mess', 'Arrows, text, steps, opaque redaction, and styled exports. Keep the editable version for yourself.', 'editor', 'See the editor workflow'],
    ['record', 'Show it in motion', 'MP4, optional audio, pause/resume, trimming, frame extraction, and background GIF conversion.', 'recording', 'Learn recording'],
    ['folder', 'Find the one you need', 'Filename-first browsing, tags, projects, saved searches, and local OCR. Saved captures stay searchable.', 'library', 'Organize your library'],
    ['compare', 'Know what actually changed', 'Full-resolution comparisons, swipe views, pixel tolerance, ignored areas, and project baselines.', 'library', 'Compare with confidence'],
    ['sync', 'Your setup. Your other PC.', 'Roam preferences with OneDrive while keeping paths, device IDs, AI choices, and agent permissions local.', 'onedrive', 'Set up settings sync'],
  ];
  const cards = features.map(([symbol, title, body, slug, link]) =>
    `<article class="feature-card">${icon(symbol)}<h3>${title}</h3><p>${body}</p><a href="${base}docs/${slug}/">${link}</a></article>`).join('');
  return `<main id="main">
    <section class="hero"><div class="container hero-grid">
      <div class="hero-copy"><span class="badge"><span class="dot"></span>Native Windows app · Release ${escapeHtml(version)}</span>
        <h1>Capture the moment.<br><em>Keep your momentum.</em></h1>
        <p>A screenshot should not break your flow. Hotshot quietly copies and saves it, then gives you a native workspace when you need to edit, organize, or explain.</p>
        <div class="hero-actions"><a class="button primary" href="#download">${icon('download')}Get Hotshot for Windows</a>
          <a class="button" href="${base}docs/getting-started/">Take your first capture ${icon('arrow')}</a></div>
        <a class="quiet-link" href="${base}docs/getting-started/#install-hotshot">x64 or ARM64? Find the right download.</a>
        <div class="hero-facts"><span>${icon('check')}No account for core features</span><span>${icon('check')}No preview pop-ups</span><span>${icon('check')}Open source</span></div>
      </div>
      <div class="preview-wrap">${workflowDemo(logo)}<p class="preview-caption">Interactive workflow illustration. Not a live capture or a product screenshot.</p></div>
    </div></section>
    <div class="trust-strip"><div class="container trust-inner">
      <div><strong>Made for Windows</strong><span>WinUI 3, not a browser wrapper</span></div>
      <div><strong>Stays in your tray</strong><span>One shortcut away, out of the way</span></div>
      <div><strong>Local image workflows</strong><span>Cloud AI and agent access are opt-in</span></div>
      <div><strong>x64 + ARM64</strong><span>Self-contained, no runtime setup</span></div>
    </div></div>
    <section class="section" id="features"><div class="container"><div class="section-heading">
      <p class="eyebrow">Small interruption. Big capability.</p><h2>Everything after the screenshot,<br>without getting in its way.</h2>
      <p>From a quick paste to a carefully annotated bug report, the next step is there when you want it.</p></div>
      <div class="feature-grid">${cards}</div></div></section>
    <section class="section alt"><div class="container"><div class="section-heading">
      <p class="eyebrow">The everyday workflow</p><h2>Three steps. Then back to work.</h2></div>
      <div class="steps">
        <article class="step"><span class="step-number">01</span><h3>Press Print Screen</h3><p>Drag a region or click a window. Escape cancels. Space chooses the monitor under your pointer.</p></article>
        <article class="step"><span class="step-number">02</span><h3>Paste it where it belongs</h3><p>Use Ctrl+V. By default, the PNG is also saved under Hotshot in your Pictures folder. No editor opens.</p></article>
        <article class="step"><span class="step-number">03</span><h3>Edit only when you need to</h3><p>Click the tray icon, choose a filename, and annotate, redact, export, or compare.</p></article>
      </div><a href="${base}docs/getting-started/">Read the complete quick start ${icon('arrow')}</a>
    </div></section>
    <section class="section"><div class="container split-section">
      <div><p class="eyebrow">Control is a feature</p><h2>Useful integrations.<br>Explicit choices.</h2>
        <p class="muted">Local OCR can make captures searchable without uploading an image. Copilot descriptions and coding-agent access are optional, separate decisions.</p>
        <p class="muted">OneDrive syncs portable preferences, not your library. Your chosen capture folder can still be backed up by another service.</p>
        <a class="button" href="${base}docs/privacy/">Understand privacy & permissions ${icon('arrow')}</a></div>
      <div class="privacy-panel">
        <div class="privacy-row">${icon('shield')}<div><strong>Image workflows stay local</strong><p>Capture, editing, comparison, and Windows OCR need no image upload.</p></div></div>
        <div class="privacy-row">${icon('code')}<div><strong>Agents need permission</strong><p>Live access and history access start off. Add timed grants, target restrictions, and native approval.</p></div></div>
        <div class="privacy-row">${icon('sync')}<div><strong>Permissions do not roam</strong><p>AI upload choices, agent permissions, device paths, and microphone IDs stay on each PC.</p></div></div>
      </div>
    </div></section>
    <section class="section alt"><div class="container split-section">
      <div><p class="eyebrow">For coding workflows</p><h2>Give your agent eyes.<br>Keep your hands on the controls.</h2>
        <p class="muted">A bundled local MCP companion lets your coding client take permitted screenshots, read OCR, and compare captures using Hotshot's settings. No shell execution or mouse/keyboard automation.</p>
        <div class="integration-list"><span>Copilot CLI</span><span>Claude Code</span><span>VS Code</span><span>Cursor</span><span>Codex</span></div>
        <a class="button" href="${base}docs/agents/">Connect a coding agent ${icon('arrow')}</a></div>
      <article class="feature-card">${icon('code')}<h3>Not sure where to start?</h3>
        <p>Use the generated-image connection test first. It checks the native transport without reading your desktop or capture history.</p>
        <p>Then choose the permissions you need. Returned images may be sent to the requesting client's model provider.</p>
        <a href="${base}docs/agents/#tools">Browse all MCP tools</a></article>
    </div></section>
    <section class="section" id="download"><div class="container"><div class="section-heading">
      <p class="eyebrow">Ready when you are</p><h2>Your next screenshot<br>can be a little easier.</h2>
      <p>Windows 10 build 19041+ or Windows 11. Per-user installation, no administrator privileges, and no separate .NET install.</p></div>
      <div class="download-grid">
        ${downloadCard('x64', 'Intel & AMD PCs', 'Most Windows desktops and laptops.', repository)}
        ${downloadCard('arm64', 'ARM64 PCs', 'Windows devices with an ARM processor.', repository)}
      </div><p class="small" style="margin-top:20px">Check Windows Settings &gt; System &gt; About &gt; System type if you are unsure. Packages are currently unsigned, so Windows may show a SmartScreen warning. Download only from <a href="${repository}/releases/latest">official releases</a>.</p>
    </div></section>
    <section class="section alt"><div class="container"><div class="section-heading"><p class="eyebrow">A few things worth knowing</p><h2>Clear answers, before you install.</h2></div>
      <div class="faq-list">
        <details><summary>Will a window pop up after every screenshot?</summary><p>No. Captures are silent. They are copied and saved according to your settings. Open the workspace from the tray only when you want it.</p></details>
        <details><summary>Do I need Copilot or another account?</summary><p>Not for capture, recording, editing, comparison, or local OCR. Optional Copilot descriptions require a signed-in account and use model quota.</p></details>
        <details><summary>Can I use Hotshot without an installer?</summary><p>Yes. Extract the entire managed-portable ZIP into a permanent writable folder and keep its files together. Both installers and managed-portable releases support automatic updates.</p></details>
        <details><summary>Does OneDrive upload my screenshots?</summary><p>Hotshot's settings-sync feature shares portable preferences, not captures or history. If your capture folder is itself backed up by OneDrive or another service, that service can upload the files independently.</p></details>
        <details><summary>What if Print Screen opens Snipping Tool?</summary><p>Disable Windows' screen-snipping shortcut for Print Screen, or choose another combination in Hotshot's Keyboard shortcuts settings. <a href="${base}docs/troubleshooting/">More troubleshooting</a>.</p></details>
        <details><summary>Is this a full ShareX replacement?</summary><p>Hotshot uses independently built, ShareX-inspired workflows with a native WinUI experience. It does not include upload services or fully automated scrolling. <a href="${base}docs/troubleshooting/#current-boundaries">See the current limits</a>.</p></details>
      </div></div></section>
  </main>`;
}

function downloadCard(architecture, title, description, repo) {
  const file = `Hotshot-win-${architecture}`;
  return `<article class="download-card"><span class="badge">${architecture.toUpperCase()}</span><h3 style="margin-top:20px">${title}</h3>
    <p>${description}</p><a class="button primary" href="${repo}/releases/latest/download/${file}-Setup.exe">${icon('download')}Download ${architecture} installer</a>
    <a class="portable" href="${repo}/releases/latest/download/${file}-Portable.zip">Or get the ${architecture} portable ZIP</a></article>`;
}

function workflowDemo(logo) {
  return `<div class="workflow-demo">
    <div class="demo-header"><span><img src="${logo}" alt="" width="22" height="22">Hotshot workflow</span><span class="small"><kbd>Print Screen</kbd> → <kbd>Ctrl V</kbd></span></div>
    <div class="demo-tabs" role="tablist" aria-label="Workflow preview">
      ${['Capture', 'Annotate', 'Find', 'Compare'].map((label, index) =>
        `<button role="tab" id="demo-tab-${index}" aria-controls="demo-panel-${index}" aria-selected="${index === 0}" tabindex="${index === 0 ? 0 : -1}" data-demo-tab="${index}">${label}</button>`).join('')}
    </div><div class="demo-stage">
    ${['Capture', 'Annotate', 'Find', 'Compare'].map((label, index) => `<div role="tabpanel" id="demo-panel-${index}" aria-labelledby="demo-tab-${index}"${index ? ' hidden' : ''}>
      <div class="demo-canvas">
        <div class="sample-window"><div class="sample-title"><strong>Product review</strong><span class="muted">Example content</span></div>
          <div class="sample-grid"><div class="sample-card"><span class="muted">Review checklist</span><strong>Ready.</strong><div class="sample-lines"><span></span><span></span><span></span></div></div>
          <div class="sample-card"><span class="muted">Next step</span><p style="margin:12px 0 0">Capture. Explain.<br>Keep moving.</p></div></div></div>
        ${index === 0 ? '<div class="capture-outline"></div>' : ''}
        ${index === 1 ? '<div class="demo-annotation">Make the point here</div>' : ''}
        ${index === 2 ? `<div class="demo-search"><div class="demo-query">project:website tag:review</div><div class="demo-result">${icon('capture')}<div><strong>homepage-review.png</strong><small>Website · review · today</small></div></div><div class="demo-result">${icon('capture')}<div><strong>settings-update.png</strong><small>Website · review · yesterday</small></div></div></div>` : ''}
        ${index === 3 ? '<div class="demo-compare"></div>' : ''}
      </div>
      ${index === 3 ? '<div class="compare-controls"><label for="compare-slider">Before</label><input id="compare-slider" aria-label="Comparison swipe position" type="range" min="0" max="100" value="50"><span>After</span></div>' : ''}
      <div class="demo-note">${icon(index === 3 ? 'compare' : 'check')}<span>${[
        'Copied and saved. No editor window opens.',
        'Open from the tray when you want to annotate or redact.',
        'Choose a filename to see it directly in the editor.',
        'Use a swipe view to inspect the difference at full resolution.',
      ][index]}</span></div>
    </div>`).join('')}
    </div></div>`;
}

export function sidebar({ base, documents, current }) {
  return `<aside class="docs-sidebar" id="docs-sidebar" aria-label="Documentation navigation">
    ${groups.map(group => `<section class="sidebar-group"><h2>${escapeHtml(group)}</h2>
      ${documents.filter(doc => doc.group === group).map(doc =>
        `<a href="${base}docs/${doc.slug}/"${current === doc.slug ? ' aria-current="page"' : ''}>${escapeHtml(doc.title)}</a>`).join('')}</section>`).join('')}
  </aside>`;
}

export function docsLanding({ base, documents }) {
  return `<main class="container docs-landing" id="main"><p class="eyebrow">The Hotshot handbook</p>
    <h1>A clear next step.<br>For every capture.</h1>
    <p class="docs-lead">Start with your first screenshot, go deeper on a workflow, or look up a setting. These guides come directly from the project's maintained documentation.</p>
    <div class="hero-actions"><a class="button primary" href="${base}docs/getting-started/">Start in a minute ${icon('arrow')}</a>
      <a class="button" href="#browse" data-search-open>${icon('search')}Search all documentation</a></div>
    <div id="browse">${groups.map(group => `<section class="docs-group"><h2>${escapeHtml(group)}</h2><div class="doc-grid">
      ${documents.filter(doc => doc.group === group).map(doc => `<a class="doc-card" href="${base}docs/${doc.slug}/"><h3>${escapeHtml(doc.title)}</h3><p>${escapeHtml(doc.description)}</p></a>`).join('')}
    </div></section>`).join('')}</div></main>`;
}

export function article({ base, documents, doc, rendered, headings, minutes }) {
  const index = documents.findIndex(item => item.slug === doc.slug);
  const pager = (entry, label) => entry ? `<a href="${base}docs/${entry.slug}/"><small>${label}</small>${escapeHtml(entry.title)}</a>` : '<span></span>';
  return `<div class="docs-shell">
    <div class="docs-toolbar"><button class="button compact" data-docs-toggle aria-controls="docs-sidebar" aria-expanded="false">${icon('menu')}Browse docs</button>
      <a href="${base}docs/" class="small">All guides</a></div>
    ${sidebar({ base, documents, current: doc.slug })}
    <main class="docs-main" id="main">
      <nav class="breadcrumbs" aria-label="Breadcrumb"><a href="${base}docs/">Docs</a><span aria-hidden="true">/</span><span>${escapeHtml(doc.group)}</span></nav>
      <h1>${escapeHtml(doc.title)}</h1><p class="doc-description">${escapeHtml(doc.description)}</p>
      <div class="doc-meta"><span>${minutes} min read · Maintained with the app</span><span><a href="${repository}/blob/main/${doc.source}">Source guide</a> · <a href="${repository}/edit/main/${doc.source}">Improve this page</a></span></div>
      <article class="prose">${rendered}</article>
      <nav class="doc-pager" aria-label="Previous and next guides">${pager(documents[index - 1], 'Previous')}${pager(documents[index + 1], 'Next')}</nav>
    </main>
    <aside class="docs-toc" aria-label="On this page"><p>On this page</p>
      ${headings.filter(heading => heading.depth <= 3).map(heading => `<a class="depth-${heading.depth}" href="#${heading.id}">${escapeHtml(heading.text)}</a>`).join('')}
      <a href="#main">Back to top</a>
    </aside>
  </div>`;
}
