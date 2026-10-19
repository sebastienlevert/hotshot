import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { documents } from '../../src/content.mjs';

for (const theme of ['light', 'dark']) {
  test(`landing page is accessible and complete in ${theme} theme`, async ({ page }) => {
    await page.goto(`?scoutTheme=${theme}`);
    await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
    await expect(page.getByRole('heading', { level: 1 })).toContainText('Make your');
    await expect(page.getByRole('heading', { level: 1 })).toContainText('point.');
    await page.evaluate(() => document.fonts.ready);
    expect(await page.evaluate(() => document.fonts.check('800 24px "Hotshot Display"'))).toBeTruthy();
    await expect(page.locator('html')).toHaveAttribute('data-brand', 'hotshot');
    await expect(page.getByRole('link', { name: 'Download x64 installer', exact: true })).toHaveAttribute('href', /win-x64-Setup\.exe$/);
    await expect(page.getByRole('link', { name: 'Download arm64 installer', exact: true })).toHaveAttribute('href', /win-arm64-Setup\.exe$/);
    const result = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze();
    expect(result.violations).toEqual([]);
  });
}

test('workflow tabs and comparison slider work with mouse and keyboard', async ({ page }) => {
  await page.goto('');
  await page.getByRole('tab', { name: 'Capture', exact: true }).focus();
  await page.keyboard.press('ArrowRight');
  await expect(page.getByRole('tab', { name: 'Annotate' })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByText('Make the point here')).toBeVisible();
  await page.getByRole('tab', { name: 'Compare', exact: true }).click();
  await page.getByRole('slider', { name: 'Comparison swipe position' }).focus();
  await page.keyboard.press('ArrowRight');
  await expect(page.locator('.comparison-images')).toHaveAttribute('style', /51%/);
});

test('search supports deep results, keyboard opening, no matches and safe query text', async ({ page }) => {
  await page.goto('');
  await page.keyboard.press('Control+k');
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.getByRole('searchbox').fill('OneDrive');
  await expect(page.locator('.search-results a').first()).toHaveAttribute('href', /docs\/onedrive\//);
  await page.getByRole('searchbox').fill('hotshot_capture_window');
  await expect(page.locator('.search-results')).toContainText('Tools');
  await page.getByRole('searchbox').fill('<img src=x onerror=alert(1)>');
  await expect(page.locator('.search-status')).toContainText('No matching');
  await expect(page.locator('.search-results img')).toHaveCount(0);
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog')).not.toBeVisible();
});

test('documentation has navigation, callouts, code copying and remembered theme', async ({ page, context }) => {
  await context.grantPermissions(['clipboard-read', 'clipboard-write']);
  await page.goto('docs/development/?scoutTheme=light');
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('Build & run from source');
  await expect(page.locator('.callout-title')).toContainText('Warning');
  await page.getByRole('button', { name: 'Copy powershell code' }).first().click();
  await expect(page.locator('.copy-code').first()).toHaveText('Copied');
  expect(await page.evaluate(() => navigator.clipboard.readText())).toContain('dotnet build');
  await page.getByRole('button', { name: 'Switch to dark theme' }).click();
  await page.goto('docs/onedrive/');
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  await expect(page.locator('.docs-sidebar a[aria-current="page"]')).toHaveText('OneDrive settings sync');
  const result = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze();
  expect(result.violations).toEqual([]);
});

for (const width of [360, 390, 768, 1280]) {
  test(`navigation and documentation fit a ${width}px viewport`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.goto('');
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBeTruthy();
    if (width < 761) {
      await page.getByRole('button', { name: 'Open navigation' }).click();
      await expect(page.locator('#site-nav')).toBeVisible();
      await page.locator('#site-nav').getByRole('link', { name: 'Documentation' }).click();
    } else await page.goto('docs/');
    await page.getByRole('link', { name: /Folders & filename tokens/ }).click();
    await expect(page.getByRole('heading', { level: 1 })).toHaveText('Folders & filename tokens');
    if (width < 761) {
      await page.getByRole('button', { name: 'Browse docs', exact: true }).click();
      await expect(page.locator('#docs-sidebar')).toBeVisible();
    }
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBeTruthy();
  });
}

test('documentation remains readable without JavaScript', async ({ browser, baseURL }) => {
  const context = await browser.newContext({ javaScriptEnabled: false });
  const page = await context.newPage();
  await page.goto(`${baseURL}docs/onedrive/`);
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('OneDrive settings sync');
  await expect(page.getByText('This syncs settings, not captures or history.')).toBeVisible();
  await expect(page.locator('.docs-sidebar')).toBeVisible();
  await context.close();
});

test('a missing page shows an actionable 404', async ({ page }) => {
  const response = await page.goto('not-a-page/');
  expect(response.status()).toBe(404);
  await expect(page.getByRole('heading', { level: 1 })).toContainText('out of frame');
  await expect(page.getByRole('link', { name: 'Hotshot home', exact: true }).last()).toBeVisible();
});

test('every guide passes accessibility checks in both themes', async ({ page }) => {
  test.setTimeout(180000);
  for (const theme of ['light', 'dark']) {
    for (const document of documents) {
      await page.goto(`docs/${document.slug}/?scoutTheme=${theme}`);
      const result = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze();
      expect(result.violations, `${document.slug} in ${theme} theme`).toEqual([]);
    }
  }
});

test('search failure is visible and recoverable instead of returning fake results', async ({ page }) => {
  await page.route('**/search.json', route => route.fulfill({ status: 503, body: 'Unavailable' }));
  await page.goto('');
  await page.keyboard.press('Control+k');
  await page.getByRole('searchbox').fill('OneDrive');
  await expect(page.locator('.search-status')).toContainText('Search could not load');
  await page.unroute('**/search.json');
  await page.getByRole('searchbox').fill('OneDrive settings');
  await expect(page.locator('.search-results a').first()).toHaveAttribute('href', /docs\/onedrive\//);
});

test('the rebrand respects reduced motion and keeps its real content at 200% text size', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.setViewportSize({ width: 390, height: 900 });
  await page.goto('');
  expect(await page.locator('.capture-outline').evaluate(element => getComputedStyle(element).animationName)).toBe('none');
  await page.addStyleTag({ content: 'html { font-size: 200%; }' });
  await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Get Hotshot for Windows', exact: true })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBeTruthy();
});

test('real product media loads with captions and user-controlled playback', async ({ page }) => {
  await page.goto('');
  const video = page.locator('#in-action video');
  await expect(video).toHaveAttribute('controls', '');
  await expect(video).toHaveAttribute('controlslist', 'nodownload');
  await expect(video).toHaveAttribute('preload', 'none');
  await expect(video).not.toHaveAttribute('autoplay', /.*/);
  await expect(video.locator('source')).toHaveAttribute('src', /workflow\.mp4$/);
  await expect(page.locator('[data-video-play], .video-actions, a[href$="workflow.mp4"]')).toHaveCount(0);
  await expect(video.locator('track')).toHaveAttribute('src', /workflow\.vtt$/);
  await video.scrollIntoViewIfNeeded();
  await video.focus();
  await page.keyboard.press('Space');
  await expect.poll(() => video.evaluate(element => element.duration)).toBeGreaterThan(10);
  await expect.poll(() => video.evaluate(element => element.currentTime)).toBeGreaterThan(0);
  expect(await video.evaluate(element => element.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true }))))
    .toBe(false);
  await video.evaluate(element => element.pause());
  await expect(page.locator('.product-gallery img')).toHaveCount(2);
  for (const image of await page.locator('.product-gallery img').all()) {
    await image.scrollIntoViewIfNeeded();
    await expect.poll(() => image.evaluate(element => element.complete && element.naturalWidth > 0)).toBeTruthy();
  }
});

test('walkthrough has native playback controls even without JavaScript', async ({ browser, baseURL }) => {
  const context = await browser.newContext({ javaScriptEnabled: false });
  try {
    const page = await context.newPage();
    await page.goto(baseURL);
    await expect(page.locator('video')).toHaveAttribute('controls', '');
    await expect(page.locator('video source')).toHaveAttribute('src', /workflow\.mp4$/);
    await expect(page.locator('a[href$="workflow.mp4"], [data-video-play]')).toHaveCount(0);
  } finally { await context.close(); }
});

test('walkthrough byte ranges support metadata probes and seeking without a full download', async ({ request }) => {
  const probe = await request.get('media/workflow.mp4', { headers: { Range: 'bytes=0-1' } });
  expect(probe.status()).toBe(206);
  expect((await probe.body()).length).toBe(2);
  expect(probe.headers()['content-range']).toMatch(/^bytes 0-1\/\d+$/);
  const tail = await request.get('media/workflow.mp4', { headers: { Range: 'bytes=-16' } });
  expect(tail.status()).toBe(206);
  expect((await tail.body()).length).toBe(16);
  const invalid = await request.get('media/workflow.mp4', { headers: { Range: 'bytes=999999999-1000000000' } });
  expect(invalid.status()).toBe(416);
  expect((await invalid.body()).length).toBe(0);
});
