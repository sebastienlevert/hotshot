import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

const viewports = [
  { width: 320, height: 568 },
  { width: 390, height: 844 },
  { width: 844, height: 390 },
];

async function expectPageFits(page) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(
    await page.evaluate(() => window.innerWidth));
}

async function expectTouchTargets(locator) {
  for (const element of await locator.all()) {
    if (!await element.isVisible()) continue;
    const bounds = await element.boundingBox();
    expect(bounds.width).toBeGreaterThanOrEqual(44);
    expect(bounds.height).toBeGreaterThanOrEqual(44);
  }
}

for (const width of [320, 390, 1280]) {
  for (const theme of ['light', 'dark']) {
    test(`workflow search overlay covers sample text at ${width}px in ${theme} theme`, async ({ page }, testInfo) => {
      await page.setViewportSize({ width, height: 900 });
      await page.goto(`?scoutTheme=${theme}`);
      await page.evaluate(() => document.fonts.ready);
      await page.getByRole('tab', { name: 'Find', exact: true }).click();
      const panel = page.locator('#demo-panel-2');
      await panel.scrollIntoViewIfNeeded();
      await expect(panel.locator('.demo-result strong')).toHaveText(['homepage-review.png', 'settings-update.png']);
      const coverage = await panel.evaluate(element => {
        const overlay = element.querySelector('.demo-search');
        const bounds = overlay.getBoundingClientRect();
        const points = [...element.querySelectorAll('.sample-art > p, .sample-caption')].flatMap(sample => {
          const sampleBounds = sample.getBoundingClientRect();
          const left = Math.max(sampleBounds.left, bounds.left);
          const top = Math.max(sampleBounds.top, bounds.top);
          const right = Math.min(sampleBounds.right, bounds.right);
          const bottom = Math.min(sampleBounds.bottom, bounds.bottom);
          if (right - left < 4 || bottom - top < 4) return [];
          return [{ x: (left + right) / 2, y: (top + bottom) / 2 }];
        });
        return {
          tested: points.length,
          uncovered: points.filter(point => !overlay.contains(document.elementFromPoint(point.x, point.y))),
        };
      });
      await page.locator('.workflow-demo').screenshot({ path: testInfo.outputPath(`workflow-find-${width}-${theme}.png`) });
      expect(coverage.tested).toBeGreaterThan(0);
      expect(coverage.uncovered, 'Sample text must stay behind the opaque Find preview').toEqual([]);
      await expectPageFits(page);
    });

    test(`workflow comparison reveals distinct designs at ${width}px in ${theme} theme`, async ({ page }, testInfo) => {
      await page.setViewportSize({ width, height: 900 });
      await page.goto(`?scoutTheme=${theme}`);
      await page.evaluate(() => document.fonts.ready);
      await page.getByRole('tab', { name: 'Compare', exact: true }).click();
      const comparison = page.locator('.comparison-images');
      const before = comparison.locator('> .sample-window');
      const after = comparison.locator('.sample-updated');
      await expect(before.locator('.sample-art > p')).toHaveText('Good ideas.Clearly captured.');
      await expect(after.locator('.sample-art > p')).toHaveText('Good ideas.Big impact.');
      expect(await before.locator('.sample-art').evaluate(element => getComputedStyle(element).backgroundColor))
        .not.toBe(await after.locator('.sample-art').evaluate(element => getComputedStyle(element).backgroundColor));
      expect(await comparison.locator('.demo-compare').evaluate(element => getComputedStyle(element).opacity)).toBe('1');
      const slider = page.getByRole('slider', { name: 'Comparison swipe position' });
      for (const value of [0, 50, 100]) {
        await slider.evaluate((element, position) => {
          element.value = String(position);
          element.dispatchEvent(new Event('input', { bubbles: true }));
        }, value);
        await comparison.scrollIntoViewIfNeeded();
        const geometry = await comparison.evaluate(element => {
          const bounds = element.getBoundingClientRect();
          const divider = element.querySelector('.compare-divider').getBoundingClientRect();
          const beforeBounds = element.querySelector(':scope > .sample-window').getBoundingClientRect();
          const afterBounds = element.querySelector('.sample-updated').getBoundingClientRect();
          return { position: (divider.left + divider.width / 2 - bounds.left) / bounds.width * 100,
            beforeWidth: beforeBounds.width, afterWidth: afterBounds.width,
            beforeHeight: beforeBounds.height, afterHeight: afterBounds.height,
            clip: getComputedStyle(element.querySelector('.demo-compare')).clipPath };
        });
        expect(geometry.position).toBeCloseTo(value, 1);
        expect(geometry.beforeWidth).toBeCloseTo(geometry.afterWidth, 1);
        expect(geometry.beforeHeight).toBeCloseTo(geometry.afterHeight, 1);
        expect(geometry.clip).toContain(`${value}%`);
        await page.locator('.workflow-demo').screenshot({ path: testInfo.outputPath(`workflow-compare-${width}-${theme}-${value}.png`) });
      }
      const accessibility = await new AxeBuilder({ page }).include('#demo-panel-3').withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze();
      expect(accessibility.violations).toEqual([]);
      await expectPageFits(page);
    });
  }
}

for (const viewport of viewports) {
    test.describe(`phone ${viewport.width}x${viewport.height}`, () => {
      test.use({ viewport, isMobile: true, hasTouch: true, deviceScaleFactor: 2 });

      for (const theme of ['light', 'dark']) {
        test(`search stays usable and scrolls results within the ${theme} viewport`, async ({ page }) => {
          for (const route of ['', 'docs/', 'docs/development/']) {
            await page.goto(`${route}?scoutTheme=${theme}`);
            await page.evaluate(() => document.fonts.ready);
            await expectPageFits(page);
            await expectTouchTargets(page.locator('.site-header button, .site-header a, .demo-tabs button, .copy-code, .docs-toolbar a, .docs-toolbar button'));
            const trigger = page.locator('[data-search-open]').first();
            expect(await trigger.evaluate(element => getComputedStyle(element).webkitTapHighlightColor))
              .toMatch(/^(?:transparent|rgba\(0,\s*0,\s*0,\s*0\))$/);
            await trigger.tap();
            await page.getByRole('searchbox').fill('capture');
            await expect(page.locator('.search-results a')).toHaveCount(10);
            await expectTouchTargets(page.locator('[data-search-close]'));
            const bounds = await page.evaluate(() => {
              const dialog = document.querySelector('.search-dialog').getBoundingClientRect();
              const results = document.querySelector('.search-results').getBoundingClientRect();
              return { top: dialog.top, bottom: dialog.bottom, resultsTop: results.top, resultsBottom: results.bottom,
                resultsHeight: results.height, viewportHeight: innerHeight };
            });
            expect(bounds.top).toBeGreaterThanOrEqual(0);
            expect(bounds.bottom).toBeLessThanOrEqual(bounds.viewportHeight);
            expect(bounds.resultsTop).toBeGreaterThanOrEqual(bounds.top);
            expect(bounds.resultsBottom).toBeLessThanOrEqual(bounds.bottom);
            expect(bounds.resultsHeight).toBeGreaterThan(44);
            await page.locator('.search-results a').last().scrollIntoViewIfNeeded();
            await expect(page.locator('.search-results a').last()).toBeInViewport();
            await page.locator('[data-search-close]').tap();
            await expect(page.getByRole('dialog')).not.toBeVisible();
            await expect(page.locator('html')).not.toHaveClass(/search-open/);
            await expect(trigger).toBeFocused();
          }
          const result = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze();
          expect(result.violations).toEqual([]);
        });
      }

      test('walkthrough waits for playback instead of downloading on mobile page load', async ({ page }) => {
        const videoRequests = [];
        page.on('request', request => {
          if (request.url().endsWith('/workflow.mp4')) videoRequests.push(request.url());
        });
        await page.goto('');
        await page.evaluate(() => document.fonts.ready);
        await page.locator('#in-action video').scrollIntoViewIfNeeded();
        await page.waitForTimeout(150);
        await expect(page.locator('video')).toHaveAttribute('preload', 'none');
        expect(videoRequests).toEqual([]);
      });
    });
}

  test.describe('responsive interactions', () => {
    test.use({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });

    test('menus close on Escape, outside taps and orientation changes', async ({ page }) => {
      await page.goto('docs/development/');
      const nav = page.locator('[data-nav-toggle]');
      await nav.tap();
      await expect(nav).toHaveAttribute('aria-label', 'Close navigation');
      await page.locator('#site-nav a').first().focus();
      await page.keyboard.press('Escape');
      await expect(nav).toHaveAttribute('aria-expanded', 'false');
      await expect(nav).toBeFocused();
      await nav.tap();
      await page.locator('.docs-main h1').tap();
      await expect(nav).toHaveAttribute('aria-expanded', 'false');
      const browse = page.locator('[data-docs-toggle]');
      await browse.tap();
      await expect(page.locator('#docs-sidebar')).toBeVisible();
      await page.locator('#docs-sidebar a').first().focus();
      await page.keyboard.press('Escape');
      await expect(browse).toBeFocused();
      await expect(browse).toHaveAttribute('aria-expanded', 'false');
      await browse.tap();
      await page.setViewportSize({ width: 844, height: 390 });
      await expect(browse).toHaveAttribute('aria-expanded', 'false');
      await page.setViewportSize({ width: 390, height: 844 });
      await expect(page.locator('#docs-sidebar')).not.toBeVisible();
      await expectPageFits(page);
    });

    test('large text and an open search adapt to a shorter visual viewport', async ({ page }) => {
      await page.goto('docs/file-naming/');
      await page.addStyleTag({ content: 'html { font-size: 200%; }' });
      await expectPageFits(page);
      await page.locator('[data-search-open]').first().tap();
      await page.getByRole('searchbox').fill('OneDrive');
      await expect(page.locator('.search-results a').first()).toBeVisible();
      await page.addStyleTag({ content: 'html { font-size: 100%; }' });
      await page.setViewportSize({ width: 390, height: 360 });
      await expect.poll(() => page.locator('.search-dialog').evaluate(element => element.getBoundingClientRect().bottom))
        .toBeLessThanOrEqual(360);
      await page.locator('.search-results a').last().scrollIntoViewIfNeeded();
      await expect(page.locator('.search-results a').last()).toBeInViewport();
      await page.locator('[data-search-close]').tap();
    });
  });
