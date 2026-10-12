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

function brand(base, logo, labelled = false) {
  return `<a class="brand" href="${base}"${labelled ? ' aria-label="Hotshot home"' : ''}>
    <img src="${logo}" alt="" width="36" height="36"><span class="brand-wordmark">hotshot<span aria-hidden="true">.</span></span></a>`;
}

export function header({ base, logo, docs = false }) {
  return `<a class="skip-link" href="#main">Skip to content</a>
  <header class="site-header"><div class="container header-inner">
    ${brand(base, logo, true)}
    <nav class="site-nav" id="site-nav" aria-label="Main navigation">
      <a href="${base}#features">What it does</a><a href="${base}docs/"${docs ? ' aria-current="page"' : ''}>Documentation</a>
      <a href="${repository}">GitHub</a>
    </nav>
    <div class="header-tools">
      <a class="search-trigger" href="${base}docs/#browse" data-search-open aria-label="Search documentation">${icon('search')}<span>Search docs</span><kbd>Ctrl K</kbd></a>
      <button class="icon-button" data-theme-toggle aria-label="Switch color theme" title="Switch color theme">${icon('theme')}</button>
      <a class="button primary compact header-download" href="${base}#download">Get Hotshot ${icon('arrow')}</a>
      <button class="icon-button nav-toggle" data-nav-toggle aria-controls="site-nav" aria-expanded="false" aria-label="Open navigation">${icon('menu')}</button>
    </div>
  </div></header>`;
}

export function footer({ base, version, logo }) {
  return `<footer class="site-footer"><div class="container footer-inner">
    <div>${brand(base, logo)}<p>Capture it. Make your point. Get on with it.</p>
      <p>Release ${escapeHtml(version)} · Windows x64 + ARM64 · MIT license</p></div>
    <nav class="footer-links" aria-label="Footer navigation"><a href="${base}docs/">Documentation</a>
      <a href="${base}docs/contributing/">Contribute</a><a href="${base}docs/changelog/">Changelog</a>
      <a href="${base}docs/privacy/">Privacy</a><a href="${repository}/issues">Report an issue</a></nav>
  </div></footer>`;
}

export function searchDialog() {
  return `<dialog class="search-dialog" aria-labelledby="search-title">
    <div class="search-top"><div class="search-label-row"><span id="search-title">Find your next step.</span>
      <button class="icon-button" data-search-close aria-label="Close search">${icon('close')}</button></div>
      <label for="docs-query" class="small">Search the Hotshot documentation</label>
      <input class="search-input" id="docs-query" type="search" placeholder="OneDrive, redaction, filename tokens..." autocomplete="off" spellcheck="false"></div>
    <p class="search-status" role="status" aria-live="polite">Search guides, settings, tools, and troubleshooting.</p>
    <ul class="search-results" aria-label="Search results"></ul>
    <p class="search-hint">Ctrl/Cmd + K to search · Arrow keys to explore · Enter to open · Escape to close</p>
  </dialog><div class="toast" role="status" aria-live="polite" hidden></div>`;
}

export function home({ base, version, logo }) {
  const capabilities = [
    ['capture', 'Catch exactly what matters.', 'A region, a window, one monitor, or all of them. Clipboard first. Saved quietly. No preview pop-up.', 'screenshots', 'Screenshot workflows'],
    ['edit', 'Less explaining. More showing.', 'Arrows, text, numbered steps, opaque redaction, and a polished PNG/JPEG export. Keep an editable original.', 'editor', 'Editing & redaction'],
    ['record', 'Some things need to move.', 'Record MP4 with optional audio. Pause, trim, extract a frame, or turn it into a GIF.', 'recording', 'Recording & GIFs'],
    ['compare', 'Spot the difference. Prove it.', 'Full-resolution comparisons, swipe views, tolerance, ignored regions, and project baselines.', 'library', 'Library & comparison'],
  ];
  return `<main id="main">
    <section class="hero"><div class="container hero-grid">
      <div class="hero-copy">
        <p class="release-line"><span class="release-pulse" aria-hidden="true"></span>Native Windows capture <span>v${escapeHtml(version)}</span></p>
        <h1><span>Make your</span><span class="hero-impact">point.</span></h1>
        <p class="hero-description">The screenshot is just the start. Capture, annotate, record, and explain — without breaking your flow.</p>
        <div class="hero-actions"><a class="button primary" href="#download">${icon('download')}Get Hotshot for Windows</a>
          <a class="text-action" href="${base}docs/getting-started/">First capture in a minute ${icon('arrow')}</a></div>
        <a class="text-action" href="#in-action">See the real native app ${icon('arrow')}</a>
        <p class="hero-facts">Free & open source. No account for core features.<br>Windows 10/11 · x64 + ARM64</p>
      </div>
      <div class="preview-wrap">${workflowDemo(logo)}
        <p class="preview-caption">Interactive illustration — not a live capture or a product screenshot.</p></div>
    </div></section>
    <div class="verb-strip" aria-label="Hotshot workflows"><div class="container">
      <span>Capture</span>${icon('arrow')}<span>Explain</span>${icon('arrow')}<span>Get on with it.</span>
      <a href="${base}docs/">See how it works ${icon('arrow')}</a>
    </div></div>
    <section class="section product-section" id="in-action"><div class="container">
      <div class="product-heading"><h2>Not a mockup.<br>Your next workflow.</h2>
        <p>Actual WinUI windows, captured with Hotshot's native pipeline. The library contains generated demo content only — no personal screenshots, clipboard, microphone, or account data.</p></div>
      <figure class="product-video"><video controls preload="metadata" playsinline poster="${base}media/editor.png" aria-label="Real Hotshot editor walkthrough using generated content, thirteen seconds, no audio">
        <source src="${base}media/workflow.mp4" type="video/mp4"><track kind="captions" src="${base}media/workflow.vtt" srclang="en" label="English descriptions">
        Your browser cannot play this video. <a href="${base}media/workflow.mp4">Download the walkthrough</a>.
      </video><figcaption>Real filename navigation and editable annotations. 13 seconds, no audio; optional captions describe the action.</figcaption></figure>
      <div class="product-gallery">
        <figure><a href="${base}media/settings.png"><img src="${base}media/settings.png" alt="Real Hotshot Settings showing file-naming patterns, token dropdowns and examples" loading="lazy" width="2178" height="1489"></a><figcaption>Native Settings, not a browser wrapper.</figcaption></figure>
        <figure><a href="${base}media/comparison.png"><img src="${base}media/comparison.png" alt="Real Hotshot comparison window showing the pixel difference between two generated checkout captures" loading="lazy" width="2178" height="1489"></a><figcaption>Full-resolution visual comparison.</figcaption></figure>
      </div><p class="small">High-DPI capture of fixture-owned windows. <a href="${base}docs/compatibility/">What was tested, and what was not</a>.</p>
    </div></section>
    <section class="section capability-section" id="features"><div class="container capability-layout">
      <div class="section-heading"><h2>One good shot.<br>A lot less back-and-forth.</h2>
        <p>Built for the moment you need to show, not tell. A native WinUI workspace when you want it. Out of your way when you do not.</p>
        <a class="text-action" href="${base}docs/">Explore the handbook ${icon('arrow')}</a></div>
      <div class="capability-list">${capabilities.map(([symbol, title, body, slug, link]) =>
        `<article class="capability-row"><div class="capability-symbol">${icon(symbol)}</div><div><h3>${title}</h3><p>${body}</p>
          <a href="${base}docs/${slug}/">${link} ${icon('arrow')}</a></div></article>`).join('')}</div>
    </div></section>
    <section class="section workflow-section"><div class="container workflow-layout">
      <div class="section-heading"><h2>Print Screen.<br>Paste.<br><span>Done.</span></h2>
        <p>There is no window to dismiss after every capture. Just a screenshot where you need it.</p></div>
      <ol class="workflow-steps">
        <li><span class="step-number">1</span><div><h3>Pick your shot.</h3><p>Press <kbd>Print Screen</kbd>. Drag a region or click a window. Escape cancels; Space picks the monitor.</p></div></li>
        <li><span class="step-number">2</span><div><h3>Put it to work.</h3><p>Paste with <kbd>Ctrl V</kbd>. By default, the PNG is also saved under Hotshot in your Pictures folder.</p></div></li>
        <li><span class="step-number">3</span><div><h3>Go further when you want to.</h3><p>Click the tray icon. Choose a filename. Annotate, redact, export, pin, or compare.</p></div></li>
      </ol>
    </div></section>
    <section class="section control-section"><div class="container control-layout">
      <div class="section-heading"><h2>Powerful.<br>Not presumptuous.</h2>
        <p>Use local OCR without uploading an image. Bring in Copilot descriptions or a coding agent only when you choose to.</p>
        <a class="text-action" href="${base}docs/privacy/">Know what leaves your machine ${icon('arrow')}</a></div>
      <div class="control-list">
        <div>${icon('shield')}<h3>Your images. Your choice.</h3><p>Capture, editing, comparison, and Windows OCR work locally. Cloud AI and agent/history access start off.</p></div>
        <div>${icon('sync')}<h3>Same setup. Different PC.</h3><p>OneDrive roams preferences, not your library. Device paths, AI choices, and agent permissions stay local.</p>
          <a href="${base}docs/onedrive/">Settings sync ${icon('arrow')}</a></div>
      </div>
    </div></section>
    <section class="section agent-section"><div class="container agent-layout">
      <div><h2>Give your agent eyes.<br>Keep the controls.</h2>
        <p>Local MCP tools for permitted screenshots, OCR, and comparisons. No shell execution. No mouse or keyboard automation.</p>
        <div class="integration-list"><span>Copilot CLI</span><span>Claude Code</span><span>VS Code</span><span>Cursor</span><span>Codex</span></div>
        <a class="button secondary" href="${base}docs/agents/">Connect a coding agent ${icon('arrow')}</a>
      </div><div class="agent-proof">
        <p class="proof-title">${icon('check')}Test first. Grant access second.</p>
        <p>The generated-image test checks the native connection without reading your screen or capture history.</p>
        <p>Add timed grants, target restrictions, and native Allow once / Deny approval. Returned images may reach the requesting client's model provider.</p>
        <a href="${base}docs/agents/#tools">All MCP tools ${icon('arrow')}</a>
      </div>
    </div></section>
    <section class="section download-section" id="download"><div class="container">
      <div class="download-heading"><h2>Take your<br>best shot.</h2><p>Windows 10 build 19041+ or Windows 11.<br>Per-user install. No admin rights. No separate .NET setup.</p></div>
      <div class="download-grid">
        ${downloadCard('x64', 'Intel & AMD', 'Most Windows desktops and laptops.')}
        ${downloadCard('arm64', 'ARM64', 'Windows devices with an ARM processor.')}
      </div>
      <p class="download-note">Not sure? Windows Settings &gt; System &gt; About &gt; System type. Packages are currently unsigned and may trigger SmartScreen. Get them only from <a href="${repository}/releases/latest">official releases</a>.</p>
    </div></section>
    <section class="section faq-section"><div class="container faq-layout"><h2>Good questions.<br>Straight answers.</h2><div class="faq-list">
      <details><summary>Will a window pop up after every screenshot?</summary><p>No. Captures are silent. They are copied and saved according to your settings. Open the workspace from the tray only when you want it.</p></details>
      <details><summary>Do I need Copilot or another account?</summary><p>Not for capture, recording, editing, comparison, or local OCR. Optional Copilot descriptions require a signed-in account and use model quota.</p></details>
      <details><summary>Can I skip the installer?</summary><p>Yes. Extract the complete managed-portable ZIP into a permanent writable folder. Keep the files together. Installers and managed-portable releases support automatic updates.</p></details>
      <details><summary>Does OneDrive upload my screenshots?</summary><p>Hotshot settings sync shares portable preferences, not captures or history. A capture folder backed up by another service can still be uploaded independently.</p></details>
      <details><summary>What if Print Screen opens Snipping Tool?</summary><p>Disable Windows' screen-snipping shortcut for Print Screen, or choose another shortcut in Hotshot. <a href="${base}docs/troubleshooting/">Get troubleshooting help</a>.</p></details>
      <details><summary>Is it a full ShareX replacement?</summary><p>Hotshot uses independently built, ShareX-inspired workflows with a native WinUI experience. It does not include upload services or fully automated scrolling. <a href="${base}docs/troubleshooting/#current-boundaries">Current limits</a>.</p></details>
    </div></div></section>
  </main>`;
}

function downloadCard(architecture, title, description) {
  const file = `Hotshot-win-${architecture}`;
  return `<article class="download-card"><div class="download-card-title"><h3>${title}</h3><span>${architecture.toUpperCase()}</span></div>
    <p>${description}</p><a class="button primary" href="${repository}/releases/latest/download/${file}-Setup.exe">${icon('download')}Download ${architecture} installer</a>
    <a class="portable" href="${repository}/releases/latest/download/${file}-Portable.zip">Or get the ${architecture} portable ZIP ${icon('arrow')}</a></article>`;
}

function workflowDemo(logo) {
  const labels = ['Capture', 'Annotate', 'Find', 'Compare'];
  return `<div class="workflow-demo">
    <div class="demo-header"><span><img src="${logo}" alt="" width="22" height="22">A shot worth sharing.</span><span class="small">PNG · Example</span></div>
    <div class="demo-tabs" role="tablist" aria-label="Workflow preview">${labels.map((label, index) =>
      `<button role="tab" id="demo-tab-${index}" aria-controls="demo-panel-${index}" aria-selected="${index === 0}" tabindex="${index === 0 ? 0 : -1}" data-demo-tab="${index}">${label}</button>`).join('')}</div>
    <div class="demo-stage">${labels.map((label, index) => `<div role="tabpanel" id="demo-panel-${index}" aria-labelledby="demo-tab-${index}"${index ? ' hidden' : ''}>
      <div class="demo-canvas">
        <div class="sample-window"><div class="sample-title"><strong>Homepage review.png</strong><span>Example content</span></div>
          <div class="sample-art"><p>Good ideas.<br>Clearly captured.</p><div class="sample-orbit" aria-hidden="true"><span></span></div><span class="sample-caption">One detail worth pointing out.</span></div>
        </div>
        ${index === 0 ? '<div class="capture-outline"><span class="capture-crosshair" aria-hidden="true"></span></div>' : ''}
        ${index === 1 ? '<div class="demo-annotation">Make the point here</div>' : ''}
        ${index === 2 ? `<div class="demo-search"><div class="demo-query">project:website tag:review</div><div class="demo-result">${icon('capture')}<div><strong>homepage-review.png</strong><small>Website · review · today</small></div></div><div class="demo-result">${icon('capture')}<div><strong>settings-update.png</strong><small>Website · review · yesterday</small></div></div></div>` : ''}
        ${index === 3 ? '<div class="demo-compare"></div>' : ''}
      </div>
      ${index === 3 ? '<div class="compare-controls"><label for="compare-slider">Before</label><input id="compare-slider" aria-label="Comparison swipe position" type="range" min="0" max="100" value="50"><span>After</span></div>' : ''}
      <div class="demo-note">${icon(index === 3 ? 'compare' : 'check')}<span>${[
        'Copied. Saved. Nothing to dismiss.',
        'Open the workspace when you want to make a point.',
        'One filename. One capture. Right there in the editor.',
        'Swipe through the change. Inspect it at full resolution.',
      ][index]}</span></div>
    </div>`).join('')}</div></div>`;
}

export function sidebar({ base, documents, current }) {
  return `<aside class="docs-sidebar" id="docs-sidebar" aria-label="Documentation navigation">
    ${groups.map(group => `<section class="sidebar-group"><h2>${escapeHtml(group)}</h2>${documents.filter(doc => doc.group === group).map(doc =>
      `<a href="${base}docs/${doc.slug}/"${current === doc.slug ? ' aria-current="page"' : ''}>${escapeHtml(doc.title)}</a>`).join('')}</section>`).join('')}
  </aside>`;
}

export function docsLanding({ base, documents }) {
  return `<main class="container docs-landing" id="main"><div class="docs-index-heading"><h1>Less guessing.<br>More doing.</h1>
    <div><p class="docs-lead">Your first screenshot, a better workflow, or one setting you cannot find. Start here.</p>
      <div class="hero-actions"><a class="button primary" href="${base}docs/getting-started/">Take your first capture ${icon('arrow')}</a>
        <a class="text-action" href="#browse" data-search-open>${icon('search')}Search the handbook</a></div></div></div>
    <div id="browse">${groups.map(group => `<section class="docs-group"><h2>${escapeHtml(group)}</h2><div class="doc-grid">
      ${documents.filter(doc => doc.group === group).map(doc =>
        `<a class="doc-card" href="${base}docs/${doc.slug}/"><div><h3>${escapeHtml(doc.title)}</h3><p>${escapeHtml(doc.description)}</p></div>${icon('arrow')}</a>`).join('')}
    </div></section>`).join('')}</div></main>`;
}

export function article({ base, documents, doc, rendered, headings, minutes }) {
  const index = documents.findIndex(item => item.slug === doc.slug);
  const pager = (entry, label) => entry ? `<a href="${base}docs/${entry.slug}/"><small>${label}</small>${escapeHtml(entry.title)} ${icon('arrow')}</a>` : '<span></span>';
  return `<div class="docs-shell"><div class="docs-toolbar"><button class="button compact" data-docs-toggle aria-controls="docs-sidebar" aria-expanded="false">${icon('menu')}Browse docs</button><a href="${base}docs/" class="small">All guides</a></div>
    ${sidebar({ base, documents, current: doc.slug })}
    <main class="docs-main" id="main"><nav class="breadcrumbs" aria-label="Breadcrumb"><a href="${base}docs/">Docs</a><span aria-hidden="true">/</span><span>${escapeHtml(doc.group)}</span></nav>
      <h1>${escapeHtml(doc.title)}</h1><p class="doc-description">${escapeHtml(doc.description)}</p>
      <div class="doc-meta"><span>${minutes} min read · Maintained with the app</span><span><a href="${repository}/blob/main/${doc.source}">Source guide</a> · <a href="${repository}/edit/main/${doc.source}">Improve this page</a></span></div>
      <article class="prose">${rendered}</article><nav class="doc-pager" aria-label="Previous and next guides">${pager(documents[index - 1], 'Previous')}${pager(documents[index + 1], 'Next')}</nav>
    </main><aside class="docs-toc" aria-label="On this page"><p>On this page</p>${headings.filter(heading => heading.depth <= 3).map(heading =>
      `<a class="depth-${heading.depth}" href="#${heading.id}">${escapeHtml(heading.text)}</a>`).join('')}<a href="#main">Back to top</a></aside>
  </div>`;
}
