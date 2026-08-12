export default function (view) {
  const pluginId = '2ea7f67d-8e4d-4c84-bd5a-a5bcd713bb23';
  const byId = (id) => view.querySelector('#' + id);
  const connector = (path, type, body) => {
    const request = { url: ApiClient.getUrl('MediaForgeRequests/' + path), type: type || 'GET', dataType: 'json' };
    if (body !== undefined) {
      request.headers = { 'Content-Type': 'application/json' };
      request.data = JSON.stringify(body);
    }
    return ApiClient.fetch(request);
  };
  function showKeyStatus(hasKey) {
    const input = byId('mfApiKey');
    input.value = '';
    input.placeholder = hasKey ? 'Sicher gespeichert – leer lassen, um beizubehalten' : 'Neuen API-Key eingeben';
    byId('mfClearApiKey').disabled = !hasKey;
  }
  async function load() {
    if (typeof Dashboard !== 'undefined') Dashboard.showLoadingMsg();
    try {
      const [config, secret] = await Promise.all([
        ApiClient.getPluginConfiguration(pluginId),
        connector('Admin/ApiKey')
      ]);
      byId('mfUrl').value = config.MediaForgeUrl || 'http://127.0.0.1:8080';
      showKeyStatus(secret.hasApiKey === true);
      byId('mfAutoApprove').checked = config.AutoApproveRequests === true;
      byId('mfAllUsers').checked = config.EnableAllUsers !== false;
      byId('mfMaintenance').checked = config.MaintenanceMode === true;
      byId('mfMaintenanceMessage').value = config.MaintenanceMessage || '';
      byId('mfMaxPending').value = config.MaxPendingRequestsPerUser || 10;
      byId('mfAdult').checked = config.AllowAdultSources === true;
      byId('mfAllowedSources').value = config.AllowedSources || '';
      byId('mfDefaultLanguage').value = config.DefaultLanguage || 'German Dub';
      byId('mfDefaultProvider').value = config.DefaultProvider || 'VOE';
      byId('mfMaxSources').value = config.MaxSearchSources || 8;
    } finally {
      if (typeof Dashboard !== 'undefined') Dashboard.hideLoadingMsg();
    }
  }
  async function save(event) {
    event.preventDefault();
    if (typeof Dashboard !== 'undefined') Dashboard.showLoadingMsg();
    const target = byId('mfTestResult');
    try {
      const config = await ApiClient.getPluginConfiguration(pluginId);
      delete config.MediaForgeApiKey;
      config.MediaForgeUrl = byId('mfUrl').value.trim();
      config.AutoApproveRequests = byId('mfAutoApprove').checked;
      config.EnableAllUsers = byId('mfAllUsers').checked;
      config.MaintenanceMode = byId('mfMaintenance').checked;
      config.MaintenanceMessage = byId('mfMaintenanceMessage').value.trim();
      config.MaxPendingRequestsPerUser = Math.max(1, Math.min(100, parseInt(byId('mfMaxPending').value, 10) || 10));
      config.AllowAdultSources = byId('mfAdult').checked;
      config.AllowedSources = byId('mfAllowedSources').value.trim();
      config.DefaultLanguage = byId('mfDefaultLanguage').value.trim() || 'German Dub';
      config.DefaultProvider = byId('mfDefaultProvider').value.trim() || 'VOE';
      config.MaxSearchSources = Math.max(1, Math.min(32, parseInt(byId('mfMaxSources').value, 10) || 8));
      const result = await ApiClient.updatePluginConfiguration(pluginId, config);
      const newApiKey = byId('mfApiKey').value.trim();
      if (newApiKey) await connector('Admin/ApiKey', 'POST', { apiKey: newApiKey });
      showKeyStatus(newApiKey ? true : !byId('mfClearApiKey').disabled);
      target.textContent = 'Einstellungen sicher gespeichert.';
      target.style.color = '#52b54b';
      if (typeof Dashboard !== 'undefined') Dashboard.processPluginConfigurationUpdateResult(result);
    } catch (_) {
      target.textContent = 'Speichern fehlgeschlagen. Bitte Eingaben und Serverprotokoll prüfen.';
      target.style.color = '#e35b64';
    } finally {
      if (typeof Dashboard !== 'undefined') Dashboard.hideLoadingMsg();
    }
  }
  async function clearApiKey() {
    if (!window.confirm('Den gespeicherten MediaForge API-Key wirklich löschen?')) return;
    const target = byId('mfTestResult');
    try {
      await connector('Admin/ApiKey', 'DELETE');
      showKeyStatus(false);
      target.textContent = 'API-Key gelöscht.';
      target.style.color = '#52b54b';
    } catch (_) {
      target.textContent = 'API-Key konnte nicht gelöscht werden.';
      target.style.color = '#e35b64';
    }
  }
  async function test() {
    const target = byId('mfTestResult');
    target.textContent = 'Verbindung wird getestet…';
    try {
      const result = await connector('Admin/Test', 'POST', {});
      target.textContent = result.ok ? 'Verbindung erfolgreich – Connector ' + (result.version || '') : 'Unerwartete Antwort von MediaForge.';
      target.style.color = '#52b54b';
    } catch (_) {
      target.textContent = 'Verbindung fehlgeschlagen. Bitte URL, API-Key, Scopes und installiertes MediaForge-Modul prüfen.';
      target.style.color = '#e35b64';
    }
  }
  view.querySelector('#MediaForgeRequestsConfigForm').addEventListener('submit', save);
  byId('mfTest').addEventListener('click', test);
  byId('mfClearApiKey').addEventListener('click', clearApiKey);
  view.addEventListener('viewshow', load);
  load();
}
