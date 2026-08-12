export default function (view, params) {
  if (view.dataset.mfInitialized === '1') return;
  view.dataset.mfInitialized = '1';
  const api = typeof ApiClient !== 'undefined' ? ApiClient : window.ApiClient;
  const q = (name) => view.querySelector('[data-mf="' + name + '"]');
  const state = { status: {}, detail: null, seasons: [], loaded: new Map(), source: '', tab: 'search' };
  let mineTimer = null;
  let mineLoading = false;

  function url(path, query) {
    let value = api.getUrl('MediaForgeRequests/' + path);
    if (query) value += '?' + new URLSearchParams(query).toString();
    return value;
  }
  async function call(path, options) {
    const opts = options || {};
    const request = { url: url(path, opts.query), type: opts.method || 'GET', dataType: 'json' };
    if (opts.body !== undefined) {
      request.headers = { 'Content-Type': 'application/json' };
      request.data = JSON.stringify(opts.body);
    }
    try { return await api.fetch(request); }
    catch (error) {
      let message = error && (error.message || error.statusText) || 'Anfrage fehlgeschlagen.';
      if (error && error.responseJSON && error.responseJSON.error) message = error.responseJSON.error;
      throw new Error(message);
    }
  }
  function notice(message, error) {
    q('notice').innerHTML = '';
    if (!message) return;
    const box = document.createElement('div'); box.className = 'mf-notice' + (error ? ' mf-error' : ''); box.textContent = message; q('notice').appendChild(box);
  }
  function switchTab(name) {
    state.tab = name;
    if (name !== 'mine' && mineTimer) { clearTimeout(mineTimer); mineTimer = null; }
    view.querySelectorAll('.mf-tab').forEach((b) => b.classList.toggle('active', b.dataset.tab === name));
    view.querySelectorAll('.mf-panel').forEach((p) => p.classList.toggle('active', p.dataset.panel === name));
    if (name === 'mine') loadMine();
    if (name === 'admin') loadAdmin();
  }
  view.querySelectorAll('.mf-tab').forEach((b) => b.addEventListener('click', () => switchTab(b.dataset.tab)));

  async function boot() {
    try {
      state.status = await call('Status');
      q('mode').textContent = state.status.mode === 'automatic' ? 'Direkter Download' : 'Freigabe durch Admin';
      if (!state.status.configured) notice('Das Plugin ist noch nicht mit MediaForge verbunden.', true);
      else if (state.status.maintenance) notice(state.status.maintenanceMessage || 'Anfragen sind derzeit deaktiviert.', true);
      const data = await call('Sources');
      (data.sources || []).forEach((item) => {
        const option = document.createElement('option'); option.value = item.id; option.textContent = item.label; q('source').appendChild(option);
      });
      await detectAdmin();
    } catch (error) { notice(error.message, true); }
  }
  async function detectAdmin() {
    try {
      const items = await call('Admin/Requests');
      const tab = view.querySelector('[data-tab="admin"]'); tab.style.display = '';
      renderRequests(q('admin'), items, true);
    } catch (_) { /* normal users receive 403 */ }
  }

  q('search-form').addEventListener('submit', async (event) => {
    event.preventDefault(); notice(''); q('results').innerHTML = '<div class="mf-empty">Suche läuft…</div>';
    try {
      const data = await call('Search', { query: { query: q('query').value.trim(), source: q('source').value } });
      renderResults(data.groups || []);
    } catch (error) { q('results').innerHTML = ''; notice(error.message, true); }
  });

  function renderResults(groups) {
    const host = q('results'); host.innerHTML = ''; let count = 0; let hasError = false;
    groups.forEach((group) => {
      const results = group.data && Array.isArray(group.data.results) ? group.data.results : [];
      results.forEach((item) => {
        const card = document.createElement('article'); card.className = 'mf-card'; card.tabIndex = 0;
        const body = document.createElement('div'); body.className = 'mf-cardbody';
        const title = document.createElement('div'); title.className = 'mf-cardtitle'; title.textContent = item.title || item.name || 'Unbekannter Titel';
        const source = document.createElement('div'); source.className = 'mf-source'; source.textContent = group.label + (item.year ? ' · ' + item.year : '');
        body.append(title, source); card.appendChild(body);
        const open = () => openDetail(item, group.source); card.addEventListener('click', open); card.addEventListener('keydown', (e) => { if (e.key === 'Enter') open(); }); host.appendChild(card); count++;
      });
      if (group.error) { hasError = true; const box = document.createElement('div'); box.className = 'mf-notice mf-error'; box.textContent = group.label + ': ' + group.error; host.appendChild(box); }
    });
    if (!count && !hasError) host.innerHTML = '<div class="mf-empty">Keine Treffer gefunden.</div>';
  }

  async function openDetail(item, source) {
    const rawUrl = item.url || item.link || item.series_url;
    if (!rawUrl) return notice('Der Treffer enthält keine MediaForge-URL.', true);
    state.source = source; state.loaded.clear(); state.seasons = []; q('overlay').style.display = 'flex'; q('detail-title').textContent = item.title || item.name || 'Laden…'; q('description').textContent = 'Details werden geladen…'; q('seasons').innerHTML = '';
    setOptions(q('language'), [state.status.defaultLanguage || 'German Dub'], state.status.defaultLanguage);
    setOptions(q('provider'), [state.status.defaultProvider || 'VOE'], state.status.defaultProvider);
    try {
      const [detail, seasons] = await Promise.all([call('Series', { query: { url: rawUrl } }), call('Seasons', { query: { url: rawUrl } })]);
      state.detail = Object.assign({}, item, detail, { url: detail.url || rawUrl }); state.seasons = seasons.seasons || [];
      q('detail-title').textContent = state.detail.title || item.title || item.name || 'Unbekannter Titel'; q('description').textContent = state.detail.description || 'Keine Beschreibung verfügbar.';
      renderSeasons(); if (state.seasons.length === 1 && state.seasons[0].is_single_movie) await loadSeason(0);
    } catch (error) { q('description').textContent = error.message; }
  }
  function renderSeasons() {
    const host = q('seasons'); host.innerHTML = '';
    state.seasons.forEach((season, index) => {
      const row = document.createElement('section'); row.className = 'mf-season'; row.dataset.index = index;
      const head = document.createElement('div'); head.className = 'mf-seasonhead';
      const label = document.createElement('strong'); label.textContent = season.is_single_movie ? 'Film' : (season.are_movies ? 'Filme/Specials' : 'Staffel ' + season.season_number) + ' (' + (season.episode_count || '?') + ')';
      const button = document.createElement('button'); button.type = 'button'; button.className = 'mf-btn secondary'; button.textContent = 'Episoden laden'; button.addEventListener('click', () => loadSeason(index)); head.append(label, button);
      const episodes = document.createElement('div'); episodes.className = 'mf-episodes'; episodes.dataset.episodes = index; row.append(head, episodes); host.appendChild(row);
    });
  }
  async function loadSeason(index) {
    if (state.loaded.has(index)) return;
    const host = q('seasons').querySelector('[data-episodes="' + index + '"]'); host.textContent = 'Laden…';
    try {
      const data = await call('Episodes', { query: { url: state.seasons[index].url } }); const episodes = data.episodes || []; state.loaded.set(index, episodes); host.innerHTML = '';
      episodes.forEach((ep) => { const label = document.createElement('label'); label.className = 'mf-episode'; const cb = document.createElement('input'); cb.type = 'checkbox'; cb.checked = !ep.downloaded; cb.value = ep.url; const text = document.createElement('span'); text.textContent = (state.seasons[index].is_single_movie ? '' : 'E' + ep.episode_number + ' · ') + (ep.title_de || ep.title_en || 'Episode') + (ep.downloaded ? ' ✓ vorhanden' : ''); label.append(cb, text); host.appendChild(label); });
      updateOptionsFromEpisodes(); const first = episodes.find((ep) => ep.url); if (first) updateProviders(first.url);
    } catch (error) { host.textContent = error.message; }
  }
  function updateOptionsFromEpisodes() {
    const langs = new Set(); state.loaded.forEach((eps) => eps.forEach((ep) => (ep.languages || []).forEach((lang) => langs.add(lang))));
    if (langs.size) setOptions(q('language'), Array.from(langs), q('language').value || state.status.defaultLanguage);
  }
  async function updateProviders(episodeUrl) {
    try {
      const data = await call('Providers', { query: { url: episodeUrl } }); const matrix = data.providers || {}; const languages = Object.keys(matrix);
      if (languages.length) { setOptions(q('language'), languages, q('language').value || state.status.defaultLanguage); syncProviders(matrix); q('language').onchange = () => syncProviders(matrix); }
    } catch (_) { /* configured defaults remain available */ }
  }
  function syncProviders(matrix) { const providers = matrix[q('language').value] || []; if (providers.length) setOptions(q('provider'), providers, state.status.defaultProvider); }
  function setOptions(select, values, preferred) { const clean = Array.from(new Set(values.filter(Boolean))); select.innerHTML = ''; clean.forEach((value) => { const option = document.createElement('option'); option.value = value; option.textContent = value; select.appendChild(option); }); if (clean.includes(preferred)) select.value = preferred; }
  q('load-all').addEventListener('click', async () => { q('load-all').disabled = true; try { for (let i = 0; i < state.seasons.length; i++) await loadSeason(i); } finally { q('load-all').disabled = false; } });
  q('select-all').addEventListener('click', () => q('seasons').querySelectorAll('input[type="checkbox"]').forEach((box) => { box.checked = true; }));
  q('request').addEventListener('click', async () => {
    const episodes = Array.from(q('seasons').querySelectorAll('input[type="checkbox"]:checked')).map((box) => box.value); if (!episodes.length) return notice('Bitte mindestens eine Episode oder den Film auswählen.', true);
    q('request').disabled = true;
    try {
      const payload = { title: q('detail-title').textContent, seriesUrl: state.detail.url, source: state.source, mediaType: state.detail.is_movie ? 'movie' : 'series', selectionLabel: episodes.length + (episodes.length === 1 ? ' Episode' : ' Episoden'), episodes, language: q('language').value, provider: q('provider').value, upscale: q('upscale').checked };
      const result = await call('Requests', { method: 'POST', body: payload }); closeDetail(); notice(result.status === 'queued' ? 'Download wurde direkt an MediaForge übergeben.' : 'Anfrage wurde an den Administrator gesendet.'); switchTab('mine');
    } catch (error) { notice(error.message, true); } finally { q('request').disabled = false; }
  });
  function closeDetail() { q('overlay').style.display = 'none'; }
  q('close').addEventListener('click', closeDetail); q('cancel').addEventListener('click', closeDetail); q('overlay').addEventListener('click', (e) => { if (e.target === q('overlay')) closeDetail(); });

  async function loadMine() {
    if (mineLoading) return;
    mineLoading = true;
    q('mine').innerHTML = '<div class="mf-empty">Laden…</div>';
    try {
      const items = await call('Requests/Mine');
      let progress = [];
      if (items.some((item) => item.status === 'queued' && item.mediaForgeQueueId)) {
        try { progress = (await call('Requests/Progress')).items || []; } catch (_) { /* request list remains available */ }
      }
      const byQueue = new Map(progress.map((item) => [item.queue_id, item]));
      renderRequests(q('mine'), items, false, byQueue);
      if (mineTimer) clearTimeout(mineTimer);
      const hasActiveDownload = progress.some((item) => item.status === 'queued' || item.status === 'running');
      if (view.isConnected && state.tab === 'mine' && hasActiveDownload) {
        mineTimer = setTimeout(loadMine, 5000);
      }
    } catch (error) { q('mine').textContent = error.message; }
    finally { mineLoading = false; }
  }
  async function loadAdmin() { q('admin').innerHTML = '<div class="mf-empty">Laden…</div>'; try { renderRequests(q('admin'), await call('Admin/Requests'), true); } catch (error) { q('admin').textContent = error.message; } }
  function renderRequests(host, items, admin, progressByQueue) {
    host.innerHTML = ''; if (!items || !items.length) { host.innerHTML = '<div class="mf-empty">Keine Anfragen vorhanden.</div>'; return; }
    items.forEach((item) => {
      const card = document.createElement('article'); card.className = 'mf-request'; const top = document.createElement('div'); top.className = 'mf-requesttop';
      const left = document.createElement('div'); const title = document.createElement('div'); title.className = 'mf-requesttitle'; title.textContent = item.title; const meta = document.createElement('div'); meta.className = 'mf-meta'; meta.textContent = (admin ? item.username + ' · ' : '') + (item.selectionLabel || ((item.episodes || []).length + ' Episoden')) + ' · ' + item.language + ' · ' + new Date(item.createdUtc).toLocaleString(); left.append(title, meta);
      const progress = progressByQueue && progressByQueue.get(item.mediaForgeQueueId); const pill = document.createElement('span'); pill.className = 'mf-pill ' + item.status; pill.textContent = progressLabel(progress) || statusLabel(item.status); top.append(left, pill); card.appendChild(top);
      if (progress) {
        const box = document.createElement('div'); box.className = 'mf-requestprogress';
        const track = document.createElement('div'); track.className = 'mf-requestprogresstrack';
        const fill = document.createElement('div'); fill.className = 'mf-requestprogressfill'; fill.style.width = Math.max(0, Math.min(100, Number(progress.percent) || 0)) + '%'; track.appendChild(fill);
        const detail = document.createElement('div'); detail.className = 'mf-meta'; detail.textContent = progressDetail(progress); box.append(track, detail); card.appendChild(box);
      }
      if (item.error) { const err = document.createElement('div'); err.className = 'mf-notice mf-error'; err.textContent = item.error; card.appendChild(err); }
      if (admin && (item.status === 'pending' || item.status === 'failed')) { const actions = document.createElement('div'); actions.className = 'mf-actions'; const approve = document.createElement('button'); approve.className = 'mf-btn'; approve.textContent = item.status === 'failed' ? 'Erneut versuchen' : 'Freigeben'; approve.onclick = () => decide(item.id, 'Approve'); const reject = document.createElement('button'); reject.className = 'mf-btn danger'; reject.textContent = 'Ablehnen'; reject.onclick = () => decide(item.id, 'Reject'); actions.append(approve, reject); card.appendChild(actions); }
      if (!admin && item.status === 'pending') { const actions = document.createElement('div'); actions.className = 'mf-actions'; const withdraw = document.createElement('button'); withdraw.className = 'mf-btn danger'; withdraw.textContent = 'Anfrage zurückziehen'; withdraw.onclick = () => withdrawRequest(item.id); actions.appendChild(withdraw); card.appendChild(actions); }
      host.appendChild(card);
    });
  }
  async function decide(id, action) { try { await call('Admin/Requests/' + id + '/' + action, { method: 'POST', body: action === 'Reject' ? { reason: 'Vom Administrator abgelehnt.' } : {} }); await loadAdmin(); } catch (error) { notice(error.message, true); } }
  async function withdrawRequest(id) { if (!window.confirm('Diese noch nicht freigegebene Anfrage zurückziehen?')) return; try { await call('Requests/' + id, { method: 'DELETE' }); await loadMine(); } catch (error) { notice(error.message, true); } }
  function progressLabel(progress) { if (!progress) return ''; return ({ queued: 'Wartet auf Download', running: 'Wird heruntergeladen', completed: 'Download fertig', partial: 'Teilweise fertig', failed: 'Download fehlgeschlagen', cancelled: 'In MediaForge abgebrochen' })[progress.status] || ''; }
  function progressDetail(progress) { const phase = ({ download: 'Download', ffmpeg: 'Verarbeitung', upscaling: 'Upscaling', move: 'Verschieben' })[progress.phase] || 'Download'; const episodes = progress.total_episodes > 1 ? ' · ' + progress.current_episode + '/' + progress.total_episodes + ' Episoden' : ''; return phase + ': ' + Math.round(Number(progress.percent) || 0) + '%' + episodes; }
  function statusLabel(status) { return ({ pending: 'Ausstehend', processing: 'Wird übergeben', queued: 'In MediaForge', completed: 'Download fertig', partial: 'Teilweise fertig', cancelled: 'Außerhalb von Jellyfin abgebrochen', rejected: 'Abgelehnt', withdrawn: 'Zurückgezogen', failed: 'Fehlgeschlagen' })[status] || status; }
  q('refresh-mine').addEventListener('click', loadMine); q('refresh-admin').addEventListener('click', loadAdmin);
  boot();
}
