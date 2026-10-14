import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import { documents, section, slugify } from '../src/content.mjs';
import { output, base, renderMarkdown } from '../src/build.mjs';
import { searchDocuments } from '../src/search.mjs';

test('section extraction supports CRLF, nested headings, and intro-only selection', () => {
  const source = '# Guide\r\n\r\n## First\r\nIntroduction.\r\n\r\n### Child\r\nDetails.\r\n\r\n## Next\r\nNext body.';
  assert.match(section(source, 'First'), /### Child/);
  assert.doesNotMatch(section(source, 'First'), /## Next/);
  assert.equal(section(source, { heading: 'First', intro: true }), 'Introduction.');
  assert.throws(() => section(source, 'Missing'), /missing/);
});

test('Markdown renders stable anchors, callouts and scrollable tables', () => {
  const rendered = renderMarkdown('## Hello, world!\n\n## Hello, world!\n\n> [!WARNING]\n> Keep originals private.\n\n| A | B |\n|---|---|\n| a | b |', 'README.md');
  assert.deepEqual(rendered.headings.map(heading => heading.id), ['hello-world', 'hello-world-1']);
  assert.match(rendered.html, /callout-title/);
  assert.match(rendered.html, /table-scroll/);
  assert.equal(slugify('Privacy & permissions'), 'privacy-permissions');
});

test('search matches every term, ranks headings and handles empty or hostile-looking input as text', () => {
  const records = [
    { title: 'General', heading: 'Data', text: 'OneDrive settings are portable.', group: 'Setup', url: '/general/' },
    { title: 'Settings', heading: 'OneDrive settings', text: 'Portable preferences and paths.', group: 'Setup', url: '/onedrive/' },
  ];
  assert.equal(searchDocuments(records, 'onedrive settings')[0].url, '/onedrive/');
  assert.equal(searchDocuments(records, 'onedrive missing').length, 0);
  assert.deepEqual(searchDocuments(records, '   '), []);
  assert.deepEqual(searchDocuments(records, '<script>alert(1)</script>'), []);
});

test('all documented pages, search entries, and SEO files are generated', async () => {
  assert.ok(documents.length >= 20);
  for (const doc of documents) {
    const html = await fs.readFile(path.join(output, 'docs', doc.slug, 'index.html'), 'utf8');
    assert.match(html, new RegExp(`<h1>${doc.title.replace(/[.*+?^${}()|[\]\\]/g, '\\$&').replaceAll('&', '&amp;')}</h1>`));
    assert.match(html, /Skip to content/);
    assert.match(html, /data-theme/);
    assert.match(html, /rel="canonical"/);
    assert.doesNotMatch(html, /\[!WARNING\]/);
    assert.doesNotMatch(html, /share[-\s]?x/i);
  }
  const index = JSON.parse(await fs.readFile(path.join(output, 'search.json'), 'utf8'));
  assert.ok(index.length > documents.length);
  assert.ok(searchDocuments(index, 'OneDrive')[0].url.includes('onedrive'));
  assert.ok(searchDocuments(index, 'hotshot_capture_window').some(result => result.url.includes('agents')));
  assert.doesNotMatch(JSON.stringify(index), /share[-\s]?x/i);
  for (const file of ['sitemap.xml', 'robots.txt', '404.html', '.nojekyll'])
    await fs.access(path.join(output, file));
  await fs.access(path.join(output, 'assets', 'display-font-license.txt'));
  const landing = await fs.readFile(path.join(output, 'index.html'), 'utf8');
  assert.match(landing, /data-brand="hotshot"/);
  assert.match(landing, /data:font\/woff2;base64,/);
  assert.doesNotMatch(landing, /__HOTSHOT_DISPLAY_FONT__/);
  assert.doesNotMatch(landing, /class="feature-grid"/);
  assert.doesNotMatch(landing, /share[-\s]?x/i);
});

test('all internal links, assets, fragments and search URLs resolve under the project base path', async () => {
  const manifest = JSON.parse(await fs.readFile(path.join(output, '.generated-files.json'), 'utf8'));
  const anchors = new Map();
  for (const file of manifest.filter(name => name.endsWith('.html'))) {
    const html = await fs.readFile(path.join(output, file), 'utf8');
    const ids = [...html.matchAll(/\bid="([^"]+)"/g)].map(match => match[1]);
    assert.equal(new Set(ids).size, ids.length, `Duplicate ID in ${file}`);
    anchors.set(file, new Set(ids));
  }
  async function check(target, source) {
    if (/^(?:https?:|data:|mailto:)/.test(target)) return;
    const sourcePath = `${base}${source.replace(/index\.html$/, '')}`;
    const url = new URL(target.replaceAll('&amp;', '&'), `https://example.com${sourcePath}`);
    assert.ok(url.pathname.startsWith(base), `Link escaped project base: ${target} in ${source}`);
    const relative = decodeURIComponent(url.pathname.slice(base.length));
    const file = relative.endsWith('/') || !relative ? `${relative}index.html` : relative;
    await fs.access(path.join(output, ...file.split('/')));
    if (url.hash) assert.ok(anchors.get(file)?.has(decodeURIComponent(url.hash.slice(1))), `Missing anchor ${target} in ${source}`);
  }
  for (const file of anchors.keys()) {
    const html = await fs.readFile(path.join(output, file), 'utf8');
    for (const match of html.matchAll(/\b(?:href|src|data-video-src)="([^"]+)"/g)) await check(match[1], file);
  }
  const index = JSON.parse(await fs.readFile(path.join(output, 'search.json'), 'utf8'));
  for (const record of index) await check(record.url, 'docs/index.html');
});

test('native publication media has correct dimensions, placement and direct playback without downloads', async () => {
  const media = JSON.parse(await fs.readFile(path.join(output, 'media', 'device-evidence.json'), 'utf8'));
  assert.match(media.sourceCommit, /^[0-9a-f]{40}$/);
  assert.match(media.applicationInformationalVersion, new RegExp(`\\+${media.sourceCommit}$`));
  for (const name of ['editor.png', 'settings.png', 'comparison.png']) {
    const png = await fs.readFile(path.join(output, 'media', name));
    assert.equal(png.readUInt32BE(16), media.screenshots[name].width, `${name} width`);
    assert.equal(png.readUInt32BE(20), media.screenshots[name].height, `${name} height`);
  }
  assert.ok(media.annotationPlacement.text.Top >= media.annotationPlacement.arrow.Bottom + media.annotationPlacement.minimumGap);
  assert.ok(media.walkthrough.durationSeconds >= 10);
  assert.equal(media.walkthrough.hasAudio, false);
  const html = await fs.readFile(path.join(output, 'index.html'), 'utf8');
  assert.match(html, /<video[^>]*controls[^>]*controlslist="nodownload"/);
  assert.match(html, /<source src="[^"]*\/media\/workflow\.mp4"/);
  assert.doesNotMatch(html, /data-video-src|data-video-play|Download the walkthrough|href="[^"]*workflow\.mp4"/);
});
