(function () {
  'use strict';
  const MENU_ID = 'mediaforge-requests-sidebar';
  const MODERN_MENU_ID = 'mediaforge-requests-modern-sidebar';
  const MODAL_ID = 'mediaforge-requests-modal';
  function api() { return typeof ApiClient !== 'undefined' ? ApiClient : window.ApiClient; }
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
  async function open() {
    const old = document.getElementById(MODAL_ID); if (old) old.remove();
    const overlay = document.createElement('div'); overlay.id = MODAL_ID; overlay.style.cssText = 'position:fixed;inset:0;z-index:999;background:#181818;overflow:auto;'; overlay.innerHTML = '<div style="position:sticky;top:0;z-index:5;display:flex;justify-content:flex-end;padding:.5rem;background:#111"><button type="button" aria-label="Schließen" style="border:0;background:transparent;color:#fff;font-size:2rem;cursor:pointer">×</button></div><div data-content><div style="padding:3rem;text-align:center">Laden…</div></div>';
    overlay.querySelector('button').onclick = () => overlay.remove(); document.body.appendChild(overlay);
    const client = api(); const content = overlay.querySelector('[data-content]');
    try {
      const html = await client.fetch({ url: client.getUrl('MediaForgeRequests/Page'), type: 'GET', dataType: 'text' }); const doc = new DOMParser().parseFromString(html, 'text/html'); const page = doc.querySelector('[data-role="page"]'); content.innerHTML = page ? page.innerHTML : html;
      const module = await import(client.getUrl('MediaForgeRequests/PageScript') + '?v=' + Date.now()); if (module.default) module.default(content, { sidebar: true });
    } catch (error) { content.textContent = 'MediaForge Requests konnte nicht geladen werden.'; }
  }
  function start() { const observer = new MutationObserver(inject); observer.observe(document.body, { childList: true, subtree: true }); inject(); }
  let attempts = 0; const timer = setInterval(function () { if (api() || attempts++ > 100) { clearInterval(timer); if (api()) document.readyState === 'loading' ? document.addEventListener('DOMContentLoaded', start) : start(); } }, 200);
})();
