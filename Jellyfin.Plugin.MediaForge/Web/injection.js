(function () {
  'use strict';
  const RUNTIME_KEY = '__mediaForgeRequestsInjection';
  if (window[RUNTIME_KEY]) return;
  window[RUNTIME_KEY] = true;
  const MENU_ID = 'mediaforge-requests-sidebar';
  const MODERN_MENU_ID = 'mediaforge-requests-modern-sidebar';
  const MODAL_ID = 'mediaforge-requests-modal';
  const OFFICIAL_LINK_HASH = '#/mediaforge-requests';
  let openGeneration = 0;
  let activeDispose = null;
  let previousFocus = null;
  let appBarObserver = null;
  let openedLocation = '';
  function api() { return typeof ApiClient !== 'undefined' ? ApiClient : window.ApiClient; }
  function locationKey(location) {
    const value = location || window.location || {};
    return String(value.pathname || '') + String(value.search || '') + String(value.hash || '');
  }
  function inject() {
    if (!api()) return;
    const sidebar = document.querySelector('.mainDrawer-scrollContainer, .mainDrawer .scrollContainer');
    if (sidebar && !document.getElementById(MENU_ID)) {
      const entry = createEntry(MENU_ID, 'navMenuOption lnkMediaFolder', '<span class="material-icons navMenuOptionIcon playlist_add" aria-hidden="true"></span><span class="navMenuOptionText">Anfragen</span>');
      entry.setAttribute('is', 'emby-linkbutton'); entry.setAttribute('data-itemid', 'mediaforge-requests');
      const custom = sidebar.querySelector('.customMenuOptions');
      const libraries = sidebar.querySelector('.libraryMenuOptions');
      const admin = sidebar.querySelector('.adminMenuOptions');
      if (custom) custom.appendChild(entry);
      else if (libraries) sidebar.insertBefore(entry, libraries);
      else if (admin) sidebar.insertBefore(entry, admin);
      else sidebar.appendChild(entry);
    }
    if (typeof document.querySelectorAll === 'function' && !document.getElementById(MODERN_MENU_ID)) {
      const drawer = Array.from(document.querySelectorAll('.MuiDrawer-paper')).find((item) => !(item.closest && item.closest('.mainDrawer')));
      const list = drawer && drawer.querySelector('ul.MuiList-root');
      if (list) {
        const modern = createEntry(MODERN_MENU_ID, 'MuiButtonBase-root MuiListItemButton-root MuiListItemButton-gutters', '<span class="material-icons" aria-hidden="true" style="min-width:2.5rem">playlist_add</span><span>Anfragen</span>');
        modern.style.cssText = 'display:flex;align-items:center;box-sizing:border-box;width:100%;min-height:48px;padding:8px 16px;color:inherit;text-decoration:none;gap:.5rem;';
        const item = document.createElement('li'); item.className = 'MuiListItem-root MuiListItem-gutters MuiListItem-padding'; item.style.cssText = 'padding:0;'; item.appendChild(modern);
        list.appendChild(item);
      }
    }
  }
  function createEntry(id, className, html) {
    const entry = document.createElement('a'); entry.id = id; entry.href = '#'; entry.className = className; entry.innerHTML = html;
    entry.addEventListener('click', function (event) { event.preventDefault(); const modernDrawer = entry.closest && entry.closest('.MuiDrawer-paper'); if (!modernDrawer) { event.stopPropagation(); const backdrop = document.querySelector('.mainDrawer-backdrop'); if (backdrop) backdrop.click(); } open(); });
    return entry;
  }
  function getOverlay() { return document.getElementById(MODAL_ID); }
  function findAppBar() {
    const values = typeof document.querySelectorAll === 'function'
      ? Array.from(document.querySelectorAll('.MuiAppBar-root, .skinHeader'))
      : [];
    return values.find((item) => {
      const rect = item.getBoundingClientRect && item.getBoundingClientRect();
      return rect && rect.bottom > 0 && rect.top <= 1;
    }) || null;
  }
  function updateOverlayOffset() {
    const overlay = getOverlay();
    if (!overlay) return;
    const appBar = findAppBar();
    const rect = appBar && appBar.getBoundingClientRect ? appBar.getBoundingClientRect() : null;
    overlay.style.top = Math.max(0, Math.ceil(rect && rect.bottom || 0)) + 'px';
  }
  function observeAppBar() {
    if (appBarObserver) appBarObserver.disconnect();
    appBarObserver = null;
    const appBar = findAppBar();
    if (appBar && typeof ResizeObserver === 'function') {
      appBarObserver = new ResizeObserver(updateOverlayOffset);
      appBarObserver.observe(appBar);
    }
    updateOverlayOffset();
  }
  function closeRequests(restoreFocus) {
    openGeneration++;
    if (activeDispose) {
      const dispose = activeDispose;
      activeDispose = null;
      try { dispose(); } catch (error) { console.warn('[MediaForge Requests] Cleanup failed:', error); }
    } else {
      const content = getOverlay() && getOverlay().querySelector('[data-content]');
      if (content) content.dispatchEvent(new CustomEvent('viewhide'));
    }
    if (appBarObserver) appBarObserver.disconnect();
    appBarObserver = null;
    const overlay = getOverlay();
    if (overlay) overlay.remove();
    openedLocation = '';
    const focusTarget = previousFocus;
    previousFocus = null;
    if (restoreFocus !== false && focusTarget && focusTarget.isConnected && typeof focusTarget.focus === 'function') {
      focusTarget.focus();
    }
  }
  function handleDocumentClick(event) {
    const anchor = event.target && event.target.closest ? event.target.closest('a[href]') : null;
    if (!anchor) return;
    if (new URL(anchor.href, document.baseURI).hash === OFFICIAL_LINK_HASH) {
      event.preventDefault();
      open();
      return;
    }
    const overlay = getOverlay();
    if (overlay && !overlay.contains(anchor)) closeRequests(false);
  }
  function handleKeydown(event) {
    if (event.key !== 'Escape') return;
    const overlay = getOverlay();
    if (!overlay) return;
    const detail = overlay.querySelector('[data-mf="overlay"]');
    if (detail && detail.style.display !== 'none') {
      const detailClose = overlay.querySelector('[data-mf="close"]');
      if (detailClose) detailClose.click();
    } else {
      closeRequests(true);
    }
    event.preventDefault();
    event.stopPropagation();
  }
  function handleViewShow(event) {
    const overlay = getOverlay();
    if (overlay && locationKey() !== openedLocation && (!event.target || !overlay.contains(event.target))) closeRequests(false);
  }
  function handleHistoryUpdate() {
    if (getOverlay() && locationKey() !== openedLocation) closeRequests(false);
  }
  async function open() {
    const existing = getOverlay();
    if (existing) {
      const existingClose = existing.querySelector('[data-mediaforge-close]');
      if (existingClose && typeof existingClose.focus === 'function') existingClose.focus();
      return;
    }
    const trigger = document.activeElement;
    closeRequests(false);
    previousFocus = trigger;
    const generation = ++openGeneration;
    openedLocation = locationKey();
    const overlay = document.createElement('div');
    overlay.id = MODAL_ID;
    overlay.setAttribute('role', 'dialog');
    overlay.setAttribute('aria-label', 'MediaForge Anfragen');
    overlay.style.cssText = 'position:fixed!important;right:0!important;bottom:0!important;left:0!important;z-index:1400!important;background:#181818!important;overflow:auto!important;overscroll-behavior:contain;';
    overlay.innerHTML = '<div style="position:sticky;top:0;z-index:5;display:flex;justify-content:flex-end;padding:.5rem;background:#111;border-bottom:1px solid #333"><button type="button" data-mediaforge-close aria-label="MediaForge Anfragen schließen" style="min-width:2.75rem;min-height:2.75rem;border:0;background:transparent;color:#fff;font-size:2rem;line-height:1;cursor:pointer">×</button></div><div data-content><div style="padding:3rem;text-align:center">Laden…</div></div>';
    const closeButton = overlay.querySelector('[data-mediaforge-close]');
    closeButton.addEventListener('click', function () { closeRequests(true); });
    document.body.appendChild(overlay);
    observeAppBar();
    if (typeof closeButton.focus === 'function') closeButton.focus();
    const client = api(); const content = overlay.querySelector('[data-content]');
    try {
      const html = await client.fetch({ url: client.getUrl('MediaForgeRequests/Page'), type: 'GET', dataType: 'text' });
      if (generation !== openGeneration || !overlay.isConnected) return;
      const doc = new DOMParser().parseFromString(html, 'text/html'); const page = doc.querySelector('[data-role="page"]'); content.innerHTML = page ? page.innerHTML : html;
      const module = await import(client.getUrl('MediaForgeRequests/PageScript') + '?v=' + Date.now());
      if (generation !== openGeneration || !overlay.isConnected) return;
      if (module.default) {
        const dispose = module.default(content, { sidebar: true, close: function () { closeRequests(true); } });
        activeDispose = typeof dispose === 'function' ? dispose : null;
      }
    } catch (error) {
      if (generation === openGeneration && overlay.isConnected) content.textContent = 'MediaForge Requests konnte nicht geladen werden.';
    }
  }
  function start() {
    document.addEventListener('click', handleDocumentClick, true);
    document.addEventListener('keydown', handleKeydown, true);
    document.addEventListener('viewshow', handleViewShow, true);
    document.addEventListener('pagebeforeshow', handleViewShow, true);
    window.addEventListener('popstate', function () { closeRequests(false); });
    window.addEventListener('hashchange', function () { closeRequests(false); });
    window.addEventListener('resize', updateOverlayOffset);
    if (window.visualViewport) window.visualViewport.addEventListener('resize', updateOverlayOffset);
    if (typeof Events !== 'undefined' && Events && typeof Events.on === 'function') {
      Events.on(document, 'HISTORY_UPDATE', handleHistoryUpdate);
    }
    const observer = new MutationObserver(inject); observer.observe(document.body, { childList: true, subtree: true }); inject();
  }
  let attempts = 0; const timer = setInterval(function () {
    const client = api();
    if (client || attempts++ > 100) {
      clearInterval(timer);
      if (client) document.readyState === 'loading' ? document.addEventListener('DOMContentLoaded', start) : start();
      else window[RUNTIME_KEY] = false;
    }
  }, 200);
})();
