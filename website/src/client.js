(() => {
  const base = document.body.dataset.base;
  const toast = document.querySelector('.toast');
  let toastTimer;
  const notify = message => {
    clearTimeout(toastTimer);
    toast.textContent = message;
    toast.hidden = false;
    toastTimer = setTimeout(() => { toast.hidden = true; }, 5000);
  };

  const themeButton = document.querySelector('[data-theme-toggle]');
  const updateThemeLabel = () => {
    const next = document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark';
    themeButton.setAttribute('aria-label', `Switch to ${next} theme`);
    themeButton.title = `Switch to ${next} theme`;
  };
  updateThemeLabel();
  themeButton.addEventListener('click', () => {
    const theme = document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark';
    document.documentElement.dataset.theme = theme;
    try { localStorage.setItem('hotshot-theme', theme); }
    catch { notify('Theme changed for this page. Your browser did not allow saving the preference.'); }
    updateThemeLabel();
  });

  const disclosures = [];
  const mobileLayout = window.matchMedia('(max-width: 760px)');
  for (const [buttonSelector, panelSelector] of [['[data-nav-toggle]', '#site-nav'], ['[data-docs-toggle]', '#docs-sidebar']]) {
    const button = document.querySelector(buttonSelector);
    const panel = document.querySelector(panelSelector);
    if (!button || !panel) continue;
    function setOpen(open, restoreFocus = false) {
      panel.classList.toggle('open', open);
      button.setAttribute('aria-expanded', String(open));
      if (button.matches('[data-nav-toggle]')) button.setAttribute('aria-label', open ? 'Close navigation' : 'Open navigation');
      if (restoreFocus && button.getClientRects().length) button.focus();
    }
    disclosures.push({ button, panel, close: restoreFocus => setOpen(false, restoreFocus) });
    button.addEventListener('click', () => setOpen(!panel.classList.contains('open')));
    panel.addEventListener('click', event => {
      if (event.target.closest('a')) {
        setOpen(false);
      }
    });
    document.addEventListener('keydown', event => {
      if (event.key === 'Escape' && panel.classList.contains('open')) {
        event.preventDefault();
        setOpen(false, true);
      }
    });
    document.addEventListener('pointerdown', event => {
      if (panel.classList.contains('open') && !panel.contains(event.target) && !button.contains(event.target))
        setOpen(false);
    });
    mobileLayout.addEventListener('change', () => setOpen(false, mobileLayout.matches && panel.contains(document.activeElement)));
  }
  const header = document.querySelector('.site-header');
  if ('ResizeObserver' in window) {
    new ResizeObserver(() => {
      document.documentElement.style.setProperty('--sticky-header-height', `${header.getBoundingClientRect().height}px`);
    }).observe(header);
  }

  const dialog = document.querySelector('.search-dialog');
  const query = document.querySelector('#docs-query');
  const status = document.querySelector('.search-status');
  const results = document.querySelector('.search-results');
  let records;
  let loading;
  let lastTrigger;
  function updateSearchViewport() {
    const viewport = window.visualViewport;
    dialog.style.setProperty('--search-viewport-height', `${viewport?.height ?? window.innerHeight}px`);
    dialog.style.setProperty('--search-viewport-top', `${viewport?.offsetTop ?? 0}px`);
  }
  window.visualViewport?.addEventListener('resize', updateSearchViewport);
  window.visualViewport?.addEventListener('scroll', updateSearchViewport);
  window.addEventListener('resize', updateSearchViewport);
  async function loadIndex() {
    if (records) return records;
    if (!loading) {
      loading = fetch(`${base}search.json`).then(async response => {
        if (!response.ok) throw new Error(`Search index returned HTTP ${response.status}`);
        const data = await response.json();
        if (!Array.isArray(data) || data.some(record => !['title', 'heading', 'text', 'url', 'group'].every(key => typeof record[key] === 'string')))
          throw new Error('Search index has an unsupported format');
        records = data;
        return data;
      }).finally(() => { loading = null; });
    }
    return loading;
  }
  async function showResults() {
    results.replaceChildren();
    const value = query.value.trim();
    if (!value) {
      status.textContent = 'Search guides, settings, tools, and troubleshooting.';
      return;
    }
    status.textContent = 'Searching documentation...';
    try {
      const index = await loadIndex();
      if (query.value.trim() !== value) return;
      const found = searchDocuments(index, value);
      status.textContent = found.length ? `${found.length} matching ${found.length === 1 ? 'section' : 'sections'}.` : 'No matching sections. Try a setting name or a shorter phrase.';
      for (const record of found) {
        const item = document.createElement('li');
        const link = document.createElement('a');
        link.className = 'search-result';
        link.href = record.url;
        const group = document.createElement('small');
        group.textContent = `${record.group} / ${record.title}`;
        const title = document.createElement('strong');
        title.textContent = record.heading;
        const excerpt = document.createElement('p');
        excerpt.textContent = record.excerpt;
        link.append(group, title, excerpt);
        item.append(link);
        results.append(item);
      }
    } catch (error) {
      if (query.value.trim() !== value) return;
      status.textContent = 'Search could not load. Check your connection and try typing again, or browse the guide list.';
      console.error('Hotshot documentation search failed:', error);
    }
  }
  function openSearch(trigger) {
    lastTrigger = trigger ?? document.activeElement;
    disclosures.forEach(disclosure => disclosure.close(false));
    updateSearchViewport();
    if (!dialog.open) dialog.showModal();
    document.documentElement.classList.add('search-open');
    query.focus();
    query.select();
    void showResults();
  }
  document.querySelectorAll('[data-search-open]').forEach(trigger => {
    trigger.addEventListener('click', event => { event.preventDefault(); openSearch(trigger); });
  });
  document.querySelector('[data-search-close]').addEventListener('click', () => dialog.close());
  dialog.addEventListener('close', () => {
    document.documentElement.classList.remove('search-open');
    lastTrigger?.focus();
  });
  dialog.addEventListener('click', event => {
    const bounds = dialog.getBoundingClientRect();
    if (event.target === dialog && (event.clientX < bounds.left || event.clientX > bounds.right ||
        event.clientY < bounds.top || event.clientY > bounds.bottom)) dialog.close();
  });
  query.addEventListener('input', () => { void showResults(); });
  dialog.addEventListener('keydown', event => {
    if (event.key === 'Escape') {
      event.preventDefault();
      dialog.close();
      return;
    }
    const links = [...results.querySelectorAll('a')];
    const current = links.indexOf(document.activeElement);
    if ((event.key === 'ArrowDown' || event.key === 'ArrowUp') && links.length) {
      event.preventDefault();
      const next = event.key === 'ArrowDown' ? (current + 1) % links.length :
        current <= 0 ? links.length - 1 : current - 1;
      links[next].focus();
    } else if (event.key === 'Enter' && document.activeElement === query && links.length) {
      event.preventDefault();
      links[0].click();
    }
  });
  document.addEventListener('keydown', event => {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
      event.preventDefault();
      openSearch();
    }
  });

  document.querySelectorAll('.prose pre').forEach(pre => {
    const code = pre.querySelector('code');
    if (!code) return;
    const label = document.createElement('span');
    label.className = 'code-label';
    label.textContent = [...code.classList].find(name => name.startsWith('language-'))?.slice(9) ?? 'text';
    const button = document.createElement('button');
    button.className = 'copy-code';
    button.textContent = 'Copy';
    button.setAttribute('aria-label', `Copy ${label.textContent} code`);
    button.addEventListener('click', async () => {
      try {
        await navigator.clipboard.writeText(code.textContent);
        button.textContent = 'Copied';
        setTimeout(() => { button.textContent = 'Copy'; }, 2000);
      } catch {
        notify('Clipboard access is unavailable. Select the code and copy it manually.');
      }
    });
    pre.prepend(label, button);
  });

  const tabs = [...document.querySelectorAll('[data-demo-tab]')];
  document.querySelectorAll('[data-video-play]').forEach(button => {
    const video = button.closest('.product-video')?.querySelector('video');
    if (!video) return;
    button.hidden = false;
    button.addEventListener('click', async () => {
      button.disabled = true;
      try {
        const source = video.querySelector('[data-video-src]');
        if (!source.hasAttribute('src')) {
          source.src = source.dataset.videoSrc;
          video.load();
        }
        await video.play();
        button.hidden = true;
      } catch (error) {
        console.error('Hotshot walkthrough playback failed:', error);
        notify('This browser could not play the walkthrough. Use the download link to watch it in a video player.');
      } finally { button.disabled = false; }
    });
  });
  function selectDemo(index) {
    tabs.forEach((tab, position) => {
      tab.setAttribute('aria-selected', String(index === position));
      tab.tabIndex = index === position ? 0 : -1;
      document.querySelector(`#demo-panel-${position}`).hidden = index !== position;
    });
  }
  tabs.forEach((tab, index) => {
    tab.addEventListener('click', () => selectDemo(index));
    tab.addEventListener('keydown', event => {
      const next = event.key === 'ArrowRight' ? (index + 1) % tabs.length :
        event.key === 'ArrowLeft' ? (index + tabs.length - 1) % tabs.length :
          event.key === 'Home' ? 0 : event.key === 'End' ? tabs.length - 1 : null;
      if (next !== null) { event.preventDefault(); selectDemo(next); tabs[next].focus(); }
    });
  });
  document.querySelector('#compare-slider')?.addEventListener('input', event => {
    document.querySelector('.comparison-images').style.setProperty('--compare-position', `${event.target.value}%`);
  });

  const tocLinks = [...document.querySelectorAll('.docs-toc a[href^="#"]')];
  if ('IntersectionObserver' in window && tocLinks.length) {
    const observer = new IntersectionObserver(entries => {
      for (const entry of entries) {
        if (!entry.isIntersecting) continue;
        tocLinks.forEach(link => {
          if (link.hash === `#${entry.target.id}`) link.setAttribute('aria-current', 'location');
          else link.removeAttribute('aria-current');
        });
      }
    }, { rootMargin: '-100px 0px -65% 0px' });
    document.querySelectorAll('.prose :is(h2, h3)').forEach(heading => observer.observe(heading));
  }
})();
