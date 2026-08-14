export default function (view, params) {
  if (view.dataset.mfInitialized === '1') return;
  view.dataset.mfInitialized = '1';
  const api = typeof ApiClient !== 'undefined' ? ApiClient : window.ApiClient;
  const q = (name) => view.querySelector('[data-mf="' + name + '"]');
  const state = { status: {}, sources: [], detail: null, source: '', tab: 'search' };
  let mineTimer = null;
  let mineLoading = false;
  let searchGeneration = 0;
  let detailGeneration = 0;

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
    catch (error) { throw new Error(await readErrorMessage(error)); }
  }
  async function readErrorMessage(error) {
    const response = error && error.response ? error.response : error;
    const responseJson = error && error.responseJSON || response && response.responseJSON;
    if (responseJson && typeof responseJson.error === 'string' && responseJson.error.trim()) return responseJson.error;
    if (response && typeof response.clone === 'function') {
      try {
        const payload = await response.clone().json();
        if (payload && typeof payload.error === 'string' && payload.error.trim()) return payload.error;
      } catch (_) { /* the response was empty or not JSON */ }
    }
    return error && (error.message || error.statusText) || response && response.statusText || 'Anfrage fehlgeschlagen.';
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
      state.sources = Array.isArray(data.sources) ? data.sources : [];
      state.sources.forEach((item) => {
        const option = document.createElement('option'); option.value = item.id; option.textContent = item.label; q('source').appendChild(option);
      });
      await loadDiscover();
      await detectAdmin();
    } catch (error) { notice(error.message, true); }
  }
  async function loadDiscover(retry) {
    const host = q('discover');
    try {
      const data = await call('Discover');
      const definitions = [['new', 'Neu'], ['popular', 'Beliebt'], ['movies', 'Filme']];
      const total = definitions.reduce((count, row) => count + ((data.rows && data.rows[row[0]]) || []).length, 0);
      if (!total && !retry) {
        setTimeout(() => loadDiscover(true), 2500);
        return;
      }
      host.innerHTML = '';
      definitions.forEach((definition) => {
        const items = (data.rows && data.rows[definition[0]]) || [];
        if (!items.length) return;
        const section = document.createElement('section'); section.className = 'mf-discoverrow';
        const heading = document.createElement('h3'); heading.className = 'mf-discoverhead'; heading.textContent = definition[1];
        const grid = document.createElement('div'); grid.className = 'mf-discovergrid';
        items.forEach((item) => grid.appendChild(createMediaCard(item, item.source, item.source_label)));
        section.append(heading, grid); host.appendChild(section);
      });
      if (!host.children.length) host.innerHTML = '<div class="mf-empty">Zurzeit sind keine Empfehlungen verfügbar.</div>';
    } catch (error) {
      host.innerHTML = '';
      const box = document.createElement('div'); box.className = 'mf-notice mf-error'; box.textContent = 'Startansicht: ' + error.message; host.appendChild(box);
    }
  }
  async function detectAdmin() {
    try {
      const items = await call('Admin/Requests');
      const tab = view.querySelector('[data-tab="admin"]'); tab.style.display = '';
      renderRequests(q('admin'), items, true);
    } catch (_) { /* normal users receive 403 */ }
  }

  function syncSearchMode() {
    const searching = q('query').value.trim().length > 0;
    q('discover').hidden = searching;
    q('results').hidden = !searching;
    if (!searching) q('results').innerHTML = '';
  }
  q('query').addEventListener('input', () => { searchGeneration++; q('results').innerHTML = ''; syncSearchMode(); });
  q('search-form').addEventListener('submit', async (event) => {
    event.preventDefault();
    const generation = ++searchGeneration;
    const keyword = q('query').value.trim();
    const selectedSource = q('source').value;
    const maximum = Math.max(1, Math.min(32, Number(state.status.maxSearchSources) || 8));
    const sources = selectedSource === 'all'
      ? state.sources.slice(0, maximum)
      : state.sources.filter((item) => item.id === selectedSource);
    syncSearchMode(); notice('');
    const host = q('results'); host.innerHTML = '';
    const pending = document.createElement('div'); pending.className = 'mf-empty'; pending.dataset.mfSearchPending = '1'; pending.textContent = 'Weitere Quellen werden durchsucht…'; host.appendChild(pending);
    if (!sources.length) { pending.remove(); notice('Keine freigegebene Quelle verfügbar.', true); return; }
    let resultCount = 0; let errorCount = 0;
    await Promise.all(sources.map(async (item) => {
      let groups;
      try {
        const data = await call('Search', { query: { query: keyword, source: item.id } });
        groups = data.groups || [];
      } catch (error) {
        groups = [{ source: item.id, label: item.label, error: error.message }];
      }
      if (generation !== searchGeneration) return;
      const rendered = appendResults(host, groups, pending);
      resultCount += rendered.count; errorCount += rendered.errors;
    }));
    if (generation !== searchGeneration) return;
    pending.remove();
    if (!resultCount && !errorCount) host.innerHTML = '<div class="mf-empty">Keine Treffer gefunden.</div>';
  });

  function appendResults(host, groups, before) {
    let count = 0; let errors = 0;
    groups.forEach((group) => {
      const results = group.data && Array.isArray(group.data.results) ? group.data.results : [];
      results.forEach((item) => {
        host.insertBefore(createMediaCard(item, group.source, group.label), before); count++;
      });
      if (group.error) { errors++; const box = document.createElement('div'); box.className = 'mf-notice mf-error'; box.textContent = group.label + ': ' + group.error; host.insertBefore(box, before); }
    });
    return { count, errors };
  }

  function createMediaCard(item, sourceId, sourceLabel) {
    const card = document.createElement('article'); card.className = 'mf-card'; card.tabIndex = 0;
    const rawUrl = item.url || item.link || item.series_url;
    if (item.poster_url) {
      const cover = document.createElement('img'); cover.className = 'mf-cover'; cover.loading = 'lazy'; cover.alt = '';
      card.appendChild(cover); loadCover(cover, item.poster_url);
    } else if (rawUrl) {
      const cover = document.createElement('img'); cover.className = 'mf-cover'; cover.loading = 'lazy'; cover.alt = '';
      card.appendChild(cover);
      call('Series', { query: { url: rawUrl } })
        .then((detail) => detail.poster_url ? loadCover(cover, detail.poster_url) : cover.remove())
        .catch(() => cover.remove());
    }
    const body = document.createElement('div'); body.className = 'mf-cardbody';
    const title = document.createElement('div'); title.className = 'mf-cardtitle'; title.textContent = item.title || item.name || 'Unbekannter Titel';
    const source = document.createElement('div'); source.className = 'mf-source'; source.textContent = (sourceLabel || sourceId || '') + (item.year ? ' · ' + item.year : '');
    body.append(title, source); card.appendChild(body);
    const open = () => openDetail(item, sourceId); card.addEventListener('click', open); card.addEventListener('keydown', (event) => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); open(); } });
    return card;
  }

  async function loadCover(image, posterUrl) {
    let objectUrl = '';
    try {
      const response = await api.fetch({ url: url('Image', { url: posterUrl }), type: 'GET' });
      if (!response || typeof response.blob !== 'function') throw new Error('Ungültige Bildantwort');
      const blob = await response.blob();
      if (!blob.type.startsWith('image/')) throw new Error('Ungültiger Bildtyp');
      objectUrl = URL.createObjectURL(blob); image.src = objectUrl;
      image.addEventListener('load', () => URL.revokeObjectURL(objectUrl), { once: true });
      image.addEventListener('error', () => { URL.revokeObjectURL(objectUrl); image.remove(); }, { once: true });
    } catch (_) {
      if (objectUrl) URL.revokeObjectURL(objectUrl);
      image.remove();
    }
  }

  async function openDetail(item, source) {
    const rawUrl = item.url || item.link || item.series_url;
    if (!rawUrl) return notice('Der Treffer enthält keine MediaForge-URL.', true);
    const generation = ++detailGeneration;
    state.source = source; state.detail = null; q('overlay').style.display = 'flex'; q('detail-title').textContent = item.title || item.name || 'Laden…'; q('description').textContent = 'Vorhandene Staffeln und Episoden werden geprüft…'; q('plan').innerHTML = '<div class="mf-empty">MediaForge prüft den Bestand…</div>'; q('request').disabled = true;
    setOptions(q('language'), [state.status.defaultLanguage || 'German Dub'], state.status.defaultLanguage);
    setOptions(q('provider'), [state.status.defaultProvider || 'VOE'], state.status.defaultProvider);
    try {
      const payload = { title: item.title || item.name || 'Unbekannter Titel', seriesUrl: rawUrl, source, mediaType: item.media_type === 'movie' ? 'movie' : 'series' };
      const plan = await call('Requests/Plan', { method: 'POST', body: payload });
      if (generation !== detailGeneration) return;
      state.detail = Object.assign(payload, { title: plan.title || payload.title, plan });
      q('detail-title').textContent = state.detail.title;
      q('description').textContent = plan.description || 'Keine Beschreibung verfügbar.';
      const languages = Array.isArray(plan.languages) && plan.languages.length ? plan.languages : [state.status.defaultLanguage || 'German Dub'];
      setOptions(q('language'), languages, state.status.defaultLanguage);
      const syncProviders = () => {
        const available = plan.providers && Array.isArray(plan.providers[q('language').value]) ? plan.providers[q('language').value] : [];
        setOptions(q('provider'), available.length ? available : [state.status.defaultProvider || 'VOE'], state.status.defaultProvider);
      };
      q('language').onchange = syncProviders; syncProviders();
      q('plan').innerHTML = '';
      const summary = document.createElement('div'); summary.className = 'mf-plan ' + (plan.missing_count ? '' : 'complete');
      if (plan.missing_count) {
        summary.textContent = plan.is_movie
          ? 'Der Film fehlt und kann angefragt werden.'
          : plan.missing_count + ' von ' + plan.total_count + ' Episoden fehlen. Es werden ausschließlich diese fehlenden Episoden angefragt.';
        q('request').disabled = false;
      } else {
        summary.textContent = plan.is_movie
          ? 'Der Film ist bereits vorhanden und wird nicht erneut eingereiht.'
          : 'Alle ' + plan.total_count + ' Episoden sind bereits vorhanden. Es wird nichts eingereiht.';
      }
      q('plan').appendChild(summary);
    } catch (error) { if (generation === detailGeneration) { q('description').textContent = error.message; q('plan').innerHTML = ''; } }
  }
  function setOptions(select, values, preferred) { const clean = Array.from(new Set(values.filter(Boolean))); select.innerHTML = ''; clean.forEach((value) => { const option = document.createElement('option'); option.value = value; option.textContent = value; select.appendChild(option); }); if (clean.includes(preferred)) select.value = preferred; }
  q('request').addEventListener('click', async () => {
    if (!state.detail || !state.detail.plan || !state.detail.plan.missing_count) return;
    const detail = state.detail;
    const generation = detailGeneration;
    q('request').disabled = true;
    try {
      const payload = { title: detail.title, seriesUrl: detail.seriesUrl, source: detail.source, mediaType: detail.plan.is_movie ? 'movie' : 'series', language: q('language').value, provider: q('provider').value, upscale: q('upscale').checked };
      const result = await call('Requests/Automatic', { method: 'POST', body: payload });
      const message = result.status === 'queued' ? 'Nur die fehlenden Inhalte wurden direkt an MediaForge übergeben.' : 'Die Anfrage für die fehlenden Inhalte wurde an den Administrator gesendet.';
      if (generation === detailGeneration) { closeDetail(); notice(message); switchTab('mine'); } else { notice(message); }
    } catch (error) { notice(error.message, true); } finally { if (generation === detailGeneration) q('request').disabled = false; }
  });
  function closeDetail() { detailGeneration++; state.detail = null; q('overlay').style.display = 'none'; }
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
      const hasActiveDownload = items.some((item) => item.status === 'queued')
        || progress.some((item) => item.status === 'queued' || item.status === 'running');
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
  function progressDetail(progress) { const phase = ({ download: 'Download', ffmpeg: 'Verarbeitung' })[progress.phase] || 'Download'; const episodes = progress.total_episodes > 1 ? ' · ' + progress.current_episode + '/' + progress.total_episodes + ' Episoden' : ''; return phase + ': ' + Math.round(Number(progress.percent) || 0) + '%' + episodes; }
  function statusLabel(status) { return ({ pending: 'Ausstehend', processing: 'Wird übergeben', queued: 'In MediaForge', completed: 'Download fertig', available: 'Bereits in Jellyfin vorhanden', partial: 'Teilweise fertig', cancelled: 'Außerhalb von Jellyfin abgebrochen', rejected: 'Abgelehnt', withdrawn: 'Zurückgezogen', failed: 'Fehlgeschlagen' })[status] || status; }
  q('refresh-mine').addEventListener('click', loadMine); q('refresh-admin').addEventListener('click', loadAdmin);
  boot();
}
