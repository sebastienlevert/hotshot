import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { Marked } from 'marked';
import { documents, section, slugify, repository } from './content.mjs';
import { article, docsLanding, escapeHtml, footer, header, home, searchDialog } from './templates.mjs';

export const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
export const output = path.join(root, 'website', 'dist');
export const base = process.env.HOTSHOT_SITE_BASE ?? '/hotshot/';
export const origin = process.env.HOTSHOT_SITE_ORIGIN ?? 'https://sebastienlevert.github.io';
if (!/^\/(?:[\w-]+\/)*$/.test(base)) throw new Error('HOTSHOT_SITE_BASE must be a root-relative path with a trailing slash.');
if (!/^https?:\/\/[^/]+$/.test(origin)) throw new Error('HOTSHOT_SITE_ORIGIN must be an HTTP(S) origin without a path.');

export function plainText(html) {
  return html.replace(/<[^>]*>/g, ' ').replace(/&#(\d+);/g, (_, code) => String.fromCodePoint(Number(code)))
    .replace(/&(?:amp|lt|gt|quot|#39);/g, entity =>
      ({ '&amp;': '&', '&lt;': '<', '&gt;': '>', '&quot;': '"', '&#39;': "'" })[entity])
    .replace(/\s+/g, ' ').trim();
}

function destination(href, source) {
  if (/^(?:https?:|mailto:)/.test(href)) {
    const prefix = `${repository}/blob/main/`;
    if (href.startsWith(prefix)) return destination(href.slice(prefix.length), 'README.md');
    return href;
  }
  if (href.startsWith('../') && !href.endsWith('.md')) return href;
  const [file, hash] = href.split('#');
  const targetSource = file ? path.posix.normalize(path.posix.join(path.posix.dirname(source), file)) : source;
  if (targetSource.endsWith('.md')) {
    const candidate = documents.find(doc => doc.source === targetSource &&
      (!hash || doc.sections?.some(selection => slugify(typeof selection === 'string' ? selection : selection.heading) === hash))) ??
      documents.find(doc => doc.source === targetSource);
    if (candidate) return `${base}docs/${candidate.slug}/${hash ? `#${hash}` : ''}`;
  }
  return file ? `${repository}/blob/main/${targetSource}${hash ? `#${hash}` : ''}` : href;
}

export function renderMarkdown(markdown, source) {
  const headings = [];
  const counts = new Map();
  const marked = new Marked({ gfm: true, breaks: false });
  marked.use({ renderer: {
    heading({ tokens, depth }) {
      const content = this.parser.parseInline(tokens);
      const text = plainText(content);
      const slug = slugify(text) || 'section';
      const count = counts.get(slug) ?? 0;
      counts.set(slug, count + 1);
      const id = count ? `${slug}-${count}` : slug;
      const level = Math.max(2, depth);
      headings.push({ id, text, depth: level });
      return `<h${level} id="${id}">${content}<a class="heading-anchor" href="#${id}" aria-label="Link to ${escapeHtml(text)}">#</a></h${level}>\n`;
    },
    link({ href, title, tokens }) {
      return `<a href="${escapeHtml(destination(href, source))}"${title ? ` title="${escapeHtml(title)}"` : ''}>${this.parser.parseInline(tokens)}</a>`;
    },
  } });
  let html = marked.parse(markdown);
  html = html.replace(/<table>/g, '<div class="table-scroll" role="region" aria-label="Reference table" tabindex="0"><table>')
    .replace(/<\/table>/g, '</table></div>');
  html = html.replace(/<blockquote>\s*<p>\[!(NOTE|WARNING|TIP|IMPORTANT)\]/g,
    (_, kind) => `<blockquote class="callout"><p><span class="callout-title">${kind[0] + kind.slice(1).toLowerCase()}</span>`);
  return { html, headings };
}

async function build() {
  const version = (await fs.readFile(path.join(root, 'version.txt'), 'utf8')).trim();
  const logoBytes = await fs.readFile(path.join(root, 'src', 'Hotshot.App', 'Assets', 'Logo.png'));
  const logo = `data:image/png;base64,${logoBytes.toString('base64')}`;
  const css = await fs.readFile(path.join(root, 'website', 'src', 'theme.css'), 'utf8');
  const client = await fs.readFile(path.join(root, 'website', 'src', 'client.js'), 'utf8');
  const search = (await fs.readFile(path.join(root, 'website', 'src', 'search.mjs'), 'utf8')).replace('export function', 'function');
  const assets = { base, logo, version };
  const files = new Map();
  const index = [];
  const theme = `(() => {
    const param = new URLSearchParams(window.location.search).get("scoutTheme");
    const theme =
      param || (window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light");
    document.documentElement.setAttribute("data-theme", theme);
  })();`;
  const preferences = `(() => {
    const requested = new URLSearchParams(location.search).get('scoutTheme');
    let stored;
    try { stored = localStorage.getItem('hotshot-theme'); } catch {}
    const value = ['light', 'dark'].includes(requested) ? requested : stored;
    if (['light', 'dark'].includes(value)) document.documentElement.dataset.theme = value;
    document.documentElement.classList.add('js-ready');
  })();`;
  function page({ title, description, route = '', content, docs = false, noindex = false }) {
    const canonical = `${origin}${base}${route}`;
    return `<!doctype html>
<html lang="en"><head><meta charset="utf-8">
<script>${theme}</script><script>${preferences}</script>
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${escapeHtml(title)}${title === 'Hotshot' ? ' — Capture fast. Keep your momentum.' : ' | Hotshot'}</title>
<meta name="description" content="${escapeHtml(description)}">
${noindex ? '<meta name="robots" content="noindex">' : `<link rel="canonical" href="${canonical}">`}
<meta property="og:type" content="website"><meta property="og:title" content="${escapeHtml(title)}">
<meta property="og:description" content="${escapeHtml(description)}"><meta property="og:url" content="${canonical}">
<meta property="og:image" content="${origin}${base}assets/logo.png"><meta name="twitter:card" content="summary">
<link rel="icon" href="${base}assets/logo.png" type="image/png">
<style>${css}</style></head><body data-base="${base}">
${header({ ...assets, docs })}${content}${footer(assets)}${searchDialog()}
<script>(() => { ${search}\n${client} })();</script>
</body></html>`;
  }
  files.set('index.html', page({ title: 'Hotshot', description: 'A native Windows screenshot and recording app that quietly copies and saves, with editing, OCR, comparisons, and optional coding-agent tools.', content: home(assets) }));
  files.set('docs/index.html', page({ title: 'Documentation', description: 'Clear, searchable Hotshot guides for capture, editing, recording, OneDrive, AI, MCP tools, troubleshooting, and development.', route: 'docs/', docs: true, content: docsLanding({ base, documents }) }));
  for (const doc of documents) {
    const source = (await fs.readFile(path.join(root, ...doc.source.split('/')), 'utf8')).replace(/\r\n?/g, '\n');
    const markdown = [doc.introduction, ...(doc.sections ? doc.sections.map(selection => section(source, selection)) : [source.replace(/^# .+\n/, '')])].filter(Boolean).join('\n\n');
    const rendered = renderMarkdown(markdown, doc.source);
    const text = plainText(rendered.html);
    const minutes = Math.max(1, Math.ceil(text.split(/\s+/).length / 220));
    const route = `docs/${doc.slug}/`;
    files.set(`${route}index.html`, page({ title: doc.title, description: doc.description, route, docs: true,
      content: article({ base, documents, doc, rendered: rendered.html, headings: rendered.headings, minutes }) }));
    index.push({ title: doc.title, heading: doc.title, group: doc.group, text: `${doc.description} ${text}`, url: `${base}${route}` });
    const positions = [...rendered.html.matchAll(/<h([2-6]) id="([^"]+)"/g)];
    for (const heading of rendered.headings) {
      const position = positions.findIndex(match => match[2] === heading.id);
      const start = positions[position].index;
      const end = positions.slice(position + 1).find(match => Number(match[1]) <= heading.depth)?.index ?? rendered.html.length;
      index.push({ title: doc.title, heading: heading.text, group: doc.group,
        text: plainText(rendered.html.slice(start, end)), url: `${base}${route}#${heading.id}` });
    }
  }
  files.set('search.json', JSON.stringify(index));
  files.set('404.html', page({ title: 'Page not found', description: 'Find your way back to Hotshot or search the documentation.', noindex: true,
    content: `<main id="main" class="container not-found"><p class="eyebrow">404 · No capture here</p><h1>That page moved<br>out of frame.</h1><p class="muted">Try the documentation, search for a setting, or go back to the homepage.</p><div class="hero-actions"><a class="button primary" href="${base}">Hotshot home</a><a class="button" href="${base}docs/">Browse the docs</a></div></main>` }));
  const routes = ['', 'docs/', ...documents.map(doc => `docs/${doc.slug}/`)];
  files.set('sitemap.xml', `<?xml version="1.0" encoding="UTF-8"?><urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">${routes.map(route => `<url><loc>${origin}${base}${route}</loc></url>`).join('')}</urlset>`);
  files.set('robots.txt', `User-agent: *\nAllow: ${base}\nSitemap: ${origin}${base}sitemap.xml\n`);
  files.set('.nojekyll', '');
  files.set('assets/logo.png', logoBytes);
  await fs.mkdir(output, { recursive: true });
  const manifestPath = path.join(output, '.generated-files.json');
  let previous = [];
  try { previous = JSON.parse(await fs.readFile(manifestPath, 'utf8')); }
  catch (error) { if (error.code !== 'ENOENT') throw error; }
  for (const file of previous) {
    if (files.has(file)) continue;
    const target = path.resolve(output, file);
    if (!target.startsWith(`${output}${path.sep}`)) throw new Error('Generated file manifest contains an out-of-scope path.');
    await fs.unlink(target).catch(error => { if (error.code !== 'ENOENT') throw error; });
  }
  for (const [name, value] of files) {
    const target = path.join(output, ...name.split('/'));
    await fs.mkdir(path.dirname(target), { recursive: true });
    await fs.writeFile(target, value);
  }
  await fs.writeFile(manifestPath, JSON.stringify([...files.keys()]));
  console.log(`Built Hotshot ${version}: ${documents.length} documentation pages, ${index.length} search sections, ${base} base path.`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) await build();
