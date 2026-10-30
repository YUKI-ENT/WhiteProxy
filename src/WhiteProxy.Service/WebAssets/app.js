'use strict';
(() => {
  const $ = id => document.getElementById(id);
  const clone = value => JSON.parse(JSON.stringify(value));
  const esc = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  const id = () => crypto.randomUUID().replaceAll('-', '');
  const dayNames = ['日','月','火','水','木','金','土'];
  const days = ['Sunday','Monday','Tuesday','Wednesday','Thursday','Friday','Saturday'];
  const localDate = value => { const d = new Date(value); return Number.isNaN(d.valueOf()) ? '—' : d.toLocaleString('ja-JP', {timeZone: zone(), month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit',second:'2-digit',hour12:false}); };
  const zone = () => (state?.timeZoneId || {'Tokyo Standard Time':'Asia/Tokyo','UTC':'UTC'}[state?.configuration?.proxy?.timeZoneId] || state?.configuration?.proxy?.timeZoneId || 'Asia/Tokyo');
  const activeGrants = (c, rule) => (c.temporaryGrants || []).filter(g => g.deviceId === rule.deviceId && g.serviceId === rule.serviceId && !g.revoked && Date.parse(g.expiresAtUtc) > Date.now());
  const key = rule => rule.deviceId + '/' + rule.serviceId;
  const lines = value => [...new Set(value.split(/\r?\n/).map(x => x.trim()).filter(Boolean))];
  let state = null, csrf = '', currentPage = 'dashboard', polling = false, busy = false, logs = [], editorSave = null, messageTimer;
  let rulesSignature = '', servicesSignature = '', logsLoaded = false;

  function message(text, error = false) {
    clearTimeout(messageTimer);
    $('message').textContent = text; $('message').classList.toggle('error', error); $('message').hidden = !text;
    if (text && !error) messageTimer = setTimeout(() => { $('message').hidden = true; }, 7000);
  }
  function requireLogin() {
    csrf = ''; state = null; $('login').hidden = false; $('application').hidden = true;
    if ($('editor').open) $('editor').close();
  }
  async function api(path, options = {}) {
    const headers = {'Accept':'application/json', ...options.headers};
    if (options.body !== undefined) { headers['Content-Type'] = 'application/json'; options.body = JSON.stringify(options.body); }
    if (options.method && options.method !== 'GET' && csrf) headers['X-CSRF-Token'] = csrf;
    let response;
    try { response = await fetch(path, {...options, headers, credentials:'same-origin', cache:'no-store'}); }
    catch { throw new Error('サーバーに接続できません。接続先とサービスの状態を確認してください。'); }
    const body = await response.json().catch(() => ({success:false,message:'サーバーからの応答を読み取れません。'}));
    if (response.status === 401 && path !== '/api/login') requireLogin();
    if (!response.ok || body.success === false) throw new Error(body.message || `要求に失敗しました（${response.status}）。`);
    return body;
  }
  function showApplication() { $('login').hidden = true; $('application').hidden = false; changePage(); }
  async function refresh(silent = false) {
    if (polling || !csrf || busy) return;
    polling = true;
    try {
      const next = await api('/api/status');
      if (!next.configuration || !next.status) throw new Error('サービスの状態を取得できません。');
      state = next; render();
    } catch (error) {
      $('connection').textContent = '接続を確認してください';
      if (!silent && csrf) message(error.message, true);
    } finally { polling = false; }
  }
  function rules(c = state.configuration) {
    const result = c.policies.map(clone), seen = new Set(result.map(key));
    for (const grant of c.temporaryGrants || []) if (!grant.revoked && Date.parse(grant.expiresAtUtc) > Date.now()) {
      const k = key(grant); if (!seen.has(k)) { seen.add(k); result.push({deviceId:grant.deviceId,serviceId:grant.serviceId,deny:false,alwaysAllow:false,schedules:[]}); }
    }
    return result;
  }
  function view(rule) { return (state.rules || []).find(v => v.key === key(rule) || (v.deviceId === rule.deviceId && v.serviceId === rule.serviceId)); }
  function schedule(rule) { return rule.alwaysAllow ? '全日・24時間' : (rule.schedules || []).filter(s => s.enabled).map(s => `${s.days.map(d => dayNames[typeof d === 'number' ? d : days.indexOf(d)]).join('・')} ${s.start.slice(0,5)}～${s.end.slice(0,5)}${s.end < s.start ? '（翌日）' : ''}`).join(' / ') || '一時許可のみ'; }
  function badge(text, type) { return `<span class="badge ${type}">${esc(text)}</span>`; }
  function action(name, text, value = '', style = '') { return `<button type="button" class="compact ${style}" data-action="${name}" data-value="${esc(value)}">${esc(text)}</button>`; }
  function render() {
    if (!state) return;
    const c = state.configuration, s = state.status, operational = s.healthy && s.running;
    $('connection').textContent = operational ? '● サービス稼働中' : '■ エラー・遮断中';
    $('timezone').textContent = '判定時刻帯：' + (c.proxy.timeZoneId || '—');
    $('updated').textContent = '最終更新 ' + localDate(s.timeUtc);
    const stats = [
      ['サービスの状態', operational ? '稼働中' : '遮断中', s.error || s.listen, true],
      ['接続セッション', Number(s.connections).toLocaleString(), '現在の接続数'],
      ['接続端末', Number(s.devices).toLocaleString(), '通信中の端末'],
      ['拒否した通信', Number(s.denied).toLocaleString(), 'サービス起動後の累計']
    ];
    const statsHtml = stats.map(([label,value,detail,status]) => `<article class="stat"><div class="stat-caption">${esc(label)}</div><div class="stat-value ${status ? 'status' + (operational ? '' : ' bad') : ''}">${esc(value)}</div><div class="stat-detail">${esc(detail)}</div></article>`).join('');
    if ($('stats').innerHTML !== statsHtml) $('stats').innerHTML = statsHtml;
    $('global-warning').hidden = !c.proxy.globalBlock;
    const data = rules().map(rule => ({rule,view:view(rule),device:c.devices.find(d => d.id === rule.deviceId),service:c.services.find(x => x.id === rule.serviceId)}));
    const signature = JSON.stringify([operational, data]);
    if (signature !== rulesSignature) {
      rulesSignature = signature;
      $('rules-body').innerHTML = data.length ? data.map(({rule,view:v,device,service}, i) => {
        const allowed = operational && v?.allowed === true, confirmed = operational && v;
        const k = key(rule);
        return `<tr class="${operational ? allowed ? 'allow-row' : 'deny-row' : ''}"><td>${badge(confirmed ? allowed ? '開放中' : '遮断中' : '状態未確認', confirmed ? allowed ? 'allow' : 'deny' : 'unknown')}</td><td class="target"><div class="rule-name">${esc(device?.name || rule.deviceId)}</div><div class="subline">${esc(device?.address || '')}</div></td><td>${esc(service?.name || rule.serviceId)}</td><td class="wrap">${esc(v?.schedule || schedule(rule))}</td><td class="wrap">${esc(!operational ? 'サービス停止・未接続・異常' : v?.reason || '状態を取得しています')}</td><td><div class="actions">${action('rule-edit','編集',k,'primary')}${action('rule-grant','一時許可',k,'grant')}${action('rule-toggle',rule.deny ? '遮断解除' : '一時遮断',k,'block')}${action('rule-delete','削除',k,'delete')}${action('rule-clone','Clone',k,'clone')}${i ? action('rule-up','↑',k) : ''}${i < data.length - 1 ? action('rule-down','↓',k) : ''}</div></td></tr>`;
      }).join('') : '<tr><td colspan="6" class="empty"><strong>許可ルールはまだありません</strong>「許可ルールを追加」から対象端末・サービス・許可時間を登録してください。</td></tr>';
    }
    const serviceSig = JSON.stringify(c.services);
    if (serviceSig !== servicesSignature) {
      const selected = selectedServices(); servicesSignature = serviceSig;
      $('services-body').innerHTML = c.services.length ? c.services.map(service => `<tr><td><input type="checkbox" class="service-select" value="${esc(service.id)}" aria-label="${esc(service.name)}を選択" ${selected.includes(service.id) ? 'checked' : ''}></td><td><strong>${esc(service.name)}</strong><div class="subline">${service.builtIn ? '初期候補' : 'ユーザー定義'} · ${esc(service.definitionVersion || '')}</div></td><td>${badge(service.enabled ? '有効' : '無効',service.enabled ? 'allow' : 'unknown')}</td><td class="hosts">${esc(service.hosts.slice(0,5).join('\n'))}${service.hosts.length > 5 ? `<div class="subline">ほか ${service.hosts.length - 5} 件（編集で確認）</div>` : ''}</td><td>${esc(service.allowedPorts.join(', '))}</td><td class="notes">${esc(service.notes || '')}${safeSource(service.source)}</td><td><div class="actions">${action('service-edit','編集',service.id,'primary')}${action('service-delete','削除',service.id,'delete')}</div></td></tr>`).join('') : '<tr><td colspan="7" class="empty"><strong>許可サービスを登録してください</strong>JSONサービスセットから追加することもできます。</td></tr>';
    }
  }
  function safeSource(source) {
    try { const url = new URL(source); return ['https:','http:'].includes(url.protocol) ? `<a href="${esc(url.href)}" target="_blank" rel="noopener noreferrer">公式出典 ↗</a>` : ''; } catch { return ''; }
  }
  function selectedServices() { return [...document.querySelectorAll('.service-select:checked')].map(x => x.value); }
  function changePage() {
    currentPage = ['dashboard','services','logs'].includes(location.hash.slice(1)) ? location.hash.slice(1) : 'dashboard';
    for (const page of ['dashboard','services','logs']) { $('page-' + page).hidden = page !== currentPage; document.querySelector(`[data-page="${page}"]`).classList.toggle('active',page === currentPage); }
    if (currentPage === 'logs' && !logsLoaded && csrf) loadLogs().catch(error => message(error.message,true));
  }
  async function command(request, candidate) {
    if (busy) throw new Error('別の操作を処理中です。少し待ってから再度お試しください。');
    busy = true;
    try {
      const response = await api('/api/command', {method:'POST',body:{expectedRevision:candidate?.revision ?? state.configuration.revision,...request}});
      if (response.configuration) state = {...state,...response};
      message(response.message || '保存しました。');
      return response;
    } finally { busy = false; await refresh(true); }
  }
  async function apply(candidate) { return command({operation:'apply',configuration:candidate,expectedRevision:candidate.revision},candidate); }
  function openEditor(title, html, save, saveText = '保存') {
    $('editor-title').textContent = title; $('editor-body').innerHTML = html; $('editor-error').textContent = '';
    $('editor-save').textContent = saveText; $('editor-save').disabled = false; $('editor-cancel').disabled = false; $('editor-close').disabled = false;
    editorSave = save; $('editor').showModal();
  }
  function field(name, label, value = '', {type='text',hint='',required=false,max=2000} = {}) {
    const input = type === 'textarea' ? `<textarea id="${name}" name="${name}" maxlength="${max}" ${required ? 'required' : ''}>${esc(value)}</textarea>` : `<input id="${name}" name="${name}" type="${type}" value="${esc(value)}" maxlength="${max}" ${required ? 'required' : ''}>`;
    return `<div class="field"><label for="${name}">${esc(label)}</label>${input}${hint ? `<div class="hint">${esc(hint)}</div>` : ''}</div>`;
  }
  function selectField(name, label, items, selected = '') { return `<div class="field"><label for="${name}">${esc(label)}</label><select id="${name}" name="${name}">${items.map(([value,text]) => `<option value="${esc(value)}" ${value === selected ? 'selected' : ''}>${esc(text)}</option>`).join('')}</select></div>`; }
  function checkbox(name,label,checked = false) { return `<div class="field"><label class="inline-check"><input id="${name}" name="${name}" type="checkbox" ${checked ? 'checked' : ''}>${esc(label)}</label></div>`; }
  function value(name) { return $(name).value.trim(); }
  function validAddress(address) {
    const parts = address.split('/'); if (parts.length > 2) return false;
    const octets = parts[0].split('.');
    return octets.length === 4 && octets.every(x => /^\d{1,3}$/.test(x) && Number(x) <= 255) && (parts.length === 1 || /^\d{1,2}$/.test(parts[1]) && Number(parts[1]) <= 32);
  }
  function ruleEditor(k = '') {
    const base = clone(state.configuration), existing = rules(base).find(p => key(p) === k), oldKey = existing ? key(existing) : '';
    const originalDevice = base.devices.find(d => d.id === existing?.deviceId);
    const scheduleDraft = clone(existing?.schedules || []);
    const html = selectField('rule-target','対象端末',[['','＋ IP / CIDRを直接指定して追加'],...base.devices.map(d => [d.id,d.name + ' / ' + d.address])],existing?.deviceId || '') +
      '<div class="field-grid">' + field('rule-name','端末の表示名',originalDevice?.name || '',{required:true,max:100}) + field('rule-address','IPv4 / CIDR',originalDevice?.address || '',{required:true,max:40,hint:'例：192.168.10.21 / 192.168.10.0/24'}) + '</div>' +
      selectField('rule-service','許可サービス',[['','サービスを選択してください'],...base.services.map(s => [s.id,s.name + (s.enabled ? '' : '（無効）')])],existing?.serviceId || '') +
      selectField('rule-mode','通常の許可時間',[['temporary','一時許可のみ（通常は遮断）'],['always','全日・24時間（常時許可）'],['schedule','曜日・時間帯を指定']],existing?.alwaysAllow ? 'always' : scheduleDraft.length ? 'schedule' : 'temporary') +
      '<section id="schedules-section"><div id="schedule-list"></div><button type="button" id="schedule-add">＋ 時間帯を追加</button></section><p class="hint">許可のない端末・サービス・時間帯は拒否します。時間外の行は読み飛ばし、上から順に判定します。時刻帯：' + esc(base.proxy.timeZoneId) + '</p>';
    openEditor(existing ? '許可ルールを編集' : '許可ルールを追加', html, async () => {
      const deviceId = value('rule-target') || id(), serviceId = value('rule-service'), name = value('rule-name'), address = value('rule-address'), mode = value('rule-mode');
      if (!serviceId) throw new Error('許可サービスを選択してください。');
      if (!name || !validAddress(address)) throw new Error('端末の表示名とIPv4 / CIDRを確認してください。');
      const candidate = clone(base), oldIndex = candidate.policies.findIndex(p => key(p) === oldKey);
      if (candidate.policies.some(p => p.deviceId === deviceId && p.serviceId === serviceId && key(p) !== oldKey)) throw new Error('同じ端末とサービスのルールが登録済みです。');
      if (existing && serviceId !== existing.serviceId && activeGrants(base,existing).length) throw new Error('旧サービスの一時許可・予約を終了してからサービスを変更してください。');
      if (mode === 'always' && !existing?.alwaysAllow && !confirm('全日・24時間、登録済みの許可先へ通信できるようになります。常時許可として保存しますか。')) return false;
      if (address.includes('/') && Number(address.split('/')[1]) < 32 && address !== originalDevice?.address && !confirm(address + ' の範囲に属する複数端末が対象になります。この範囲で保存しますか。')) return false;
      let device = candidate.devices.find(d => d.id === deviceId);
      if (!device) { device = {id:deviceId,name,address,group:'',enabled:true,paused:false}; candidate.devices.push(device); }
      else { device.name = name; device.address = address; }
      const schedules = mode === 'schedule' ? readSchedules() : [];
      if (mode === 'schedule' && !schedules.some(s => s.enabled)) throw new Error('有効な曜日・時間帯を追加してください。');
      const rule = {deviceId,serviceId,deny:existing?.deny || false,alwaysAllow:mode === 'always',schedules};
      if (oldIndex >= 0) candidate.policies[oldIndex] = rule; else candidate.policies.push(rule);
      await apply(candidate); return true;
    });
    $('rule-target').disabled = !!existing;
    $('rule-target').addEventListener('change',() => { const d = base.devices.find(d => d.id === value('rule-target')); $('rule-name').value = d?.name || ''; $('rule-address').value = d?.address || ''; });
    function drawSchedules() {
      $('schedule-list').innerHTML = scheduleDraft.map((s,index) => `<div class="schedule" data-schedule="${index}"><div class="days">${days.map((day,i) => `<label><input type="checkbox" data-day="${day}" ${(s.days || []).includes(day) || (s.days || []).includes(i) ? 'checked' : ''}>${dayNames[i]}</label>`).join('')}</div><div class="schedule-time"><input type="time" data-time="start" aria-label="開始時刻" value="${esc((s.start || '12:00').slice(0,5))}" required><span>～</span><input type="time" data-time="end" aria-label="終了時刻" value="${esc((s.end || '13:00').slice(0,5))}" required><label class="inline-check"><input type="checkbox" data-enabled ${s.enabled ? 'checked' : ''}>有効</label><button type="button" data-remove-schedule="${index}" class="compact delete">削除</button></div></div>`).join('');
      $('schedules-section').hidden = value('rule-mode') !== 'schedule';
      for (const button of $('schedule-list').querySelectorAll('[data-remove-schedule]')) button.addEventListener('click',() => { const updated = readSchedules(false); scheduleDraft.splice(0,scheduleDraft.length,...updated); scheduleDraft.splice(Number(button.dataset.removeSchedule),1); drawSchedules(); });
    }
    function readSchedules(validate = true) { return [...$('schedule-list').querySelectorAll('.schedule')].map(row => { const start = row.querySelector('[data-time=start]').value, end = row.querySelector('[data-time=end]').value; const selectedDays = [...row.querySelectorAll('[data-day]:checked')].map(x => x.dataset.day); const enabled = row.querySelector('[data-enabled]').checked; if (validate && (!start || !end || start === end || !selectedDays.length)) throw new Error('各時間帯の曜日・開始・終了を確認してください。終了が開始より早い場合は翌日まで許可します。'); return {enabled,days:selectedDays,start:start + ':00',end:end + ':00'}; }); }
    $('schedule-add').addEventListener('click',() => { const updated = readSchedules(false); scheduleDraft.splice(0,scheduleDraft.length,...updated,{enabled:true,days:days.slice(1,6),start:'12:00:00',end:'13:00:00'}); drawSchedules(); });
    $('rule-mode').addEventListener('change',() => { $('schedules-section').hidden = value('rule-mode') !== 'schedule'; });
    drawSchedules();
  }
  function serviceEditor(serviceId = '') {
    const base = clone(state.configuration), old = base.services.find(s => s.id === serviceId);
    openEditor(old ? '許可サービスを編集' : '許可サービスを追加',
      field('service-name','サービス名',old?.name || '',{required:true,max:100}) + field('service-hosts','ホスト（1行1件）',(old?.hosts || []).join('\n'),{type:'textarea',max:262144,hint:'example.com は完全一致。*.example.com はサブドメインのみ（親ホストを含みません）。URL・パスは指定できません。空欄はすべて拒否。'}) +
      field('service-ports','許可TCPポート',(old?.allowedPorts || [80,443]).join(','),{required:true,max:768,hint:'カンマ区切りで指定します。例：80,443'}) + checkbox('service-enabled','サービスを有効にする',old?.enabled ?? true) + field('service-source','公式出典URL',old?.source || '',{max:2000}) + field('service-notes','注意・確認内容',old?.notes || '',{type:'textarea',max:2000}), async () => {
        const name = value('service-name'), hosts = lines($('service-hosts').value), ports = [...new Set(value('service-ports').split(',').map(x => Number(x.trim())))];
        if (!name) throw new Error('サービス名を入力してください。');
        if (!ports.length || ports.some(p => !Number.isInteger(p) || p < 1 || p > 65535)) throw new Error('TCPポートを1～65535の範囲で指定してください。');
        if (hosts.some(h => h.includes('://') || /[/?#@\s]/.test(h))) throw new Error('ホスト名だけを1行1件で入力してください。URLやパスは指定できません。');
        if (ports.some(p => p !== 80 && p !== 443) && !confirm('80/443以外のトンネル接続も可能になります。追加ポートを許可しますか。')) return false;
        const next = {...(old || {id:id(),builtIn:false,definitionVersion:new Date().toISOString().slice(0,10)}),name,hosts,allowedPorts:ports,enabled:$('service-enabled').checked,source:value('service-source'),notes:$('service-notes').value.trim()};
        const candidate = clone(base), index = candidate.services.findIndex(s => s.id === serviceId);
        if (index < 0) candidate.services.push(next); else candidate.services[index] = next;
        await apply(candidate); return true;
      });
  }
  function localInput(date) { const d = new Date(date); return new Date(d.valueOf() - d.getTimezoneOffset() * 60000).toISOString().slice(0,16); }
  function grantEditor(k) {
    const base = clone(state.configuration), rule = rules(base).find(r => key(r) === k); if (!rule) return;
    const grants = activeGrants(base,rule), device = base.devices.find(d => d.id === rule.deviceId), service = base.services.find(s => s.id === rule.serviceId);
    const now = new Date(), latest = grants.reduce((date,g) => Date.parse(g.expiresAtUtc) > date.valueOf() ? new Date(g.expiresAtUtc) : date,new Date(now.valueOf() + base.proxy.maximumGrantHours * 3600000));
    openEditor('一時許可・予約', `<p><strong>${esc(device?.name)}</strong> / ${esc(service?.name)}</p><p class="hint">ブラウザーの現地時刻で指定します。サーバーの一時許可上限は ${esc(base.proxy.maximumGrantHours)} 時間です。既存期限の短縮には「終了」を使用してください。</p><div class="field-grid">${field('grant-start','開始',localInput(now),{type:'datetime-local',required:true})}${field('grant-end','終了',localInput(latest),{type:'datetime-local',required:true})}</div>${grants.length ? '<h3>現在の一時許可・予約</h3><ul class="grant-list">' + grants.map(g => `<li><span>${esc(localDate(g.startsAtUtc))} ～ ${esc(localDate(g.expiresAtUtc))}</span><button type="button" class="compact delete" data-revoke="${esc(g.id)}">終了</button></li>`).join('') + '</ul>' : ''}`, async () => {
      const start = new Date(value('grant-start')), end = new Date(value('grant-end'));
      if (!Number.isFinite(start.valueOf()) || !Number.isFinite(end.valueOf()) || end <= start) throw new Error('開始・終了時刻を確認してください。');
      const startsAtUtc = Math.abs(start.valueOf() - now.valueOf()) < 60000 ? null : start.toISOString();
      await command({operation:'grant',expectedRevision:base.revision,deviceIds:[rule.deviceId],serviceId:rule.serviceId,startsAtUtc,expiresAtUtc:end.toISOString()},base); return true;
    }, '一時許可を保存');
    for (const button of $('editor-body').querySelectorAll('[data-revoke]')) button.addEventListener('click',async () => { if (!confirm('この一時許可・予約を終了しますか。')) return; button.disabled = true; try { await command({operation:'revoke',grantId:button.dataset.revoke,expectedRevision:base.revision},base); $('editor').close(); } catch (error) { $('editor-error').textContent = error.message; button.disabled = false; } });
  }
  async function loadLogs() { const response = await api('/api/logs'); logs = response.logs || []; logsLoaded = true; renderLogs(); }
  function path(entry) { return entry.kind !== '通信' ? '—' : entry.isTunnel ? '取得不可（CONNECT）' : entry.requestPath || '未記録'; }
  function logDay(entry) { return new Intl.DateTimeFormat('sv-SE',{timeZone:zone(),year:'numeric',month:'2-digit',day:'2-digit'}).format(new Date(entry.timeUtc)); }
  function filteredLogs() { const query = $('log-search').value.toLowerCase(), kind = $('log-kind').value, from = $('log-from').value, to = $('log-to').value; return logs.filter(e => (!query || [e.peer,e.host,e.requestPath,e.reason,e.serviceId,e.actor].join(' ').toLowerCase().includes(query)) && (kind === 'all' || kind === 'audit' && e.kind === '監査' || kind === 'allow' && e.kind === '通信' && e.allowed || kind === 'deny' && e.kind === '通信' && !e.allowed) && (!from || logDay(e) >= from) && (!to || logDay(e) <= to)); }
  function renderLogs() {
    const filtered = filteredLogs(); $('log-count').textContent = filtered.length.toLocaleString() + ' / ' + logs.length.toLocaleString() + ' 件';
    $('logs-body').innerHTML = filtered.length ? filtered.map(e => `<tr class="${e.kind === '通信' ? e.allowed ? 'allow-row' : 'deny-row' : ''}"><td class="date">${esc(localDate(e.timeUtc))}</td><td>${esc(e.kind)}</td><td>${esc(e.peer)}</td><td>${esc(e.host)}</td><td>${esc(path(e))}</td><td class="numeric">${e.kind === '通信' ? esc(e.port) : '—'}</td><td class="result">${e.kind !== '通信' ? '—' : badge(e.allowed ? '許可' : '拒否',e.allowed ? 'allow' : 'deny')}</td><td>${esc(e.reason)}</td><td>${esc(state?.configuration.services.find(s => s.id === e.serviceId)?.name || e.serviceId || '')}</td><td class="numeric">${e.kind === '通信' ? Number(e.bytes || 0).toLocaleString() : '—'}</td><td class="numeric">${e.kind === '通信' ? Number(e.seconds || 0).toFixed(1) : '—'}</td><td>${esc(e.actor)}</td><td>${e.kind === '通信' && !e.allowed && e.host ? action('host-add','サービスへ追加',e.host) : ''}</td></tr>`).join('') : '<tr><td colspan="13" class="empty">表示するログがありません。条件を変えるか「ログを取得」を押してください。</td></tr>';
  }
  function download(name, content, type) { const url = URL.createObjectURL(new Blob([content],{type})); const anchor = document.createElement('a'); anchor.href = url; anchor.download = name; anchor.click(); setTimeout(() => URL.revokeObjectURL(url),1000); }
  function exportLogs() { const csv = value => { let text = String(value ?? ''); if (/^[\s]*[=+@-]/.test(text)) text = "'" + text; return '"' + text.replaceAll('"','""') + '"'; }; const rows = [['UTC','種別','端末IP','ホスト','パス','ポート','許可','理由','サービス','バイト','秒','操作者'],...filteredLogs().map(e => [e.timeUtc,e.kind,e.peer,e.host,path(e),e.port,e.allowed,e.reason,e.serviceId,e.bytes,e.seconds,e.actor])]; download('WhiteProxy-logs.csv','\ufeff' + rows.map(r => r.map(csv).join(',')).join('\r\n'),'text/csv;charset=utf-8'); }
  function exportServices() {
    const selected = selectedServices(), services = state.configuration.services.filter(s => selected.includes(s.id));
    if (!services.length) throw new Error('出力するサービスにチェックを入れてください。');
    if (services.some(s => !s.hosts.length)) throw new Error('ホストが未登録のサービスは出力できません。');
    const pack = {format:'WhiteProxy.ServicePack',version:1,name:services.length === 1 ? services[0].name : `許可サービス（${services.length}件）`,services:services.map(({name,hosts,allowedPorts,source,definitionVersion,notes}) => ({name,hosts,allowedPorts,source,definitionVersion,notes}))};
    download('WhiteProxy-services.json',JSON.stringify(pack,null,2),'application/json');
  }
  async function importServices(file) {
    if (file.size > 1024 * 1024) throw new Error('サービスセットは1MB以内にしてください。');
    let pack; try { pack = JSON.parse((await file.text()).replace(/^\ufeff/,'')); } catch { throw new Error('JSONを読み取れません。'); }
    if (pack.format !== 'WhiteProxy.ServicePack' || pack.version !== 1 || !Array.isArray(pack.services) || !pack.services.length || pack.services.length > 128) throw new Error('WhiteProxy.ServicePack バージョン1のサービスセットを指定してください。');
    const base = clone(state.configuration), added = [];
    for (const item of pack.services) {
      if (!item || typeof item.name !== 'string' || !item.name.trim() || !Array.isArray(item.hosts) || !item.hosts.length || !Array.isArray(item.allowedPorts) || !item.allowedPorts.length || item.hosts.some(h => typeof h !== 'string') || item.allowedPorts.some(p => !Number.isInteger(p) || p < 1 || p > 65535)) throw new Error('サービス名・ホスト・TCPポートを確認してください。');
      const hosts = [...new Set(item.hosts.map(h => h.trim().toLowerCase().replace(/\.$/,'')))], ports = [...new Set(item.allowedPorts)].sort((a,b) => a-b);
      if (base.services.some(s => s.name === item.name && JSON.stringify([...s.hosts].sort()) === JSON.stringify([...hosts].sort()) && JSON.stringify([...s.allowedPorts].sort((a,b) => a-b)) === JSON.stringify(ports))) continue;
      let name = item.name.trim(), index = 2; while (base.services.some(s => s.name === name)) name = item.name.slice(0,80) + `（インポート ${index++}）`;
      const service = {id:id(),name,hosts,allowedPorts:ports,source:item.source || '',definitionVersion:item.definitionVersion || '',notes:item.notes || '',enabled:true,builtIn:false};
      base.services.push(service); added.push(service);
    }
    if (!added.length) { message('すべて登録済みのため、追加はありません。'); return; }
    openEditor('サービスセットから追加', `<p>${esc(pack.name || file.name)}</p><div class="import-preview">${esc(added.map(s => `${s.name} — ホスト ${s.hosts.length} 件 / TCP ${s.allowedPorts.join(', ')}`).join('\n'))}</div><p class="hint">${added.length} 件を追加します。既存サービス・ルールは変更しません。許可ルールは自動作成しません。</p>`, async () => { if (added.some(s => s.allowedPorts.some(p => p !== 80 && p !== 443)) && !confirm('80/443以外のポートを含みます。追加しますか。')) return false; await apply(base); return true; },'追加して保存');
  }
  function hostEditor(host) {
    const base = clone(state.configuration);
    openEditor('拒否ログのホストをサービスへ追加', `<p><strong>${esc(host)}</strong></p>${selectField('host-service','追加するサービス',base.services.map(s => [s.id,s.name]))}<p class="hint">公式の接続先であることを確認してください。ホスト内の他のURLも許可対象になります。保存後に有効な許可ルールがある場合は通信可能になります。</p>`, async () => { const service = base.services.find(s => s.id === value('host-service')); if (!service) throw new Error('サービスを選択してください。'); if (!service.hosts.includes(host)) service.hosts.push(host); await apply(base); return true; });
  }
  async function dispatch(actionName, v) {
    if (!state && actionName !== 'refresh') return;
    const base = state && clone(state.configuration), rule = base && rules(base).find(r => key(r) === v);
    switch (actionName) {
      case 'refresh': await refresh(); break;
      case 'rule-add': ruleEditor(); break;
      case 'rule-edit': ruleEditor(v); break;
      case 'rule-grant': grantEditor(v); break;
      case 'service-add': serviceEditor(); break;
      case 'service-edit': serviceEditor(v); break;
      case 'host-add': hostEditor(v); break;
      case 'global-block': case 'global-unblock': if (confirm(actionName === 'global-block' ? 'すべての通信を遮断します。解除まで全ルールより優先されます。実行しますか。' : '全遮断を解除し、保存済みの許可ルールに戻しますか。')) await command({operation:'block',block:actionName === 'global-block'}); break;
      case 'rule-toggle': if (rule) { if (!confirm(rule.deny ? 'このルールの一時遮断を解除しますか。通常の許可時間に従って判定します。' : 'このルールを一時遮断しますか。解除までこの行で拒否します。')) break; let target = base.policies.find(p => key(p) === v); if (!target) { target = rule; base.policies.push(target); } target.deny = !target.deny; await apply(base); } break;
      case 'rule-delete': if (rule && confirm('このルールを削除しますか。対象の一時許可・予約が残っている場合は先に終了してください。')) { if (activeGrants(base,rule).length) throw new Error('一時許可・予約を終了してからルールを削除してください。'); base.policies = base.policies.filter(p => key(p) !== v); await apply(base); } break;
      case 'rule-clone': if (rule) { const source = base.devices.find(d => d.id === rule.deviceId); if (!source) break; base.policies = rules(base); const device = {...source,id:id(),name:source.name.slice(0,90) + ' のコピー'}; base.devices.push(device); const copy = {...clone(rule),deviceId:device.id}; const index = base.policies.findIndex(p => key(p) === v); base.policies.splice(index < 0 ? base.policies.length : index + 1,0,copy); await apply(base); } break;
      case 'rule-up': case 'rule-down': { base.policies = rules(base); const index = base.policies.findIndex(p => key(p) === v), destination = index + (actionName === 'rule-up' ? -1 : 1); if (index >= 0 && destination >= 0 && destination < base.policies.length) { [base.policies[index],base.policies[destination]] = [base.policies[destination],base.policies[index]]; await apply(base); } break; }
      case 'service-delete': { const service = base.services.find(s => s.id === v); if (service && confirm(`${service.name} を削除しますか。`)) { if (base.policies.some(p => p.serviceId === v) || base.temporaryGrants.some(g => g.serviceId === v)) throw new Error('ルールまたは一時許可の履歴で参照されているサービスは削除できません。無効化するか、参照のないサービスを選択してください。'); base.services = base.services.filter(s => s.id !== v); await apply(base); } break; }
      case 'service-import': $('service-file').click(); break;
      case 'service-export': exportServices(); break;
      case 'logs-refresh': await loadLogs(); message('ログを更新しました。'); break;
      case 'logs-export': exportLogs(); break;
    }
  }
  $('login-form').addEventListener('submit',async event => { event.preventDefault(); const button = event.submitter || $('login-form').querySelector('button'); button.disabled = true; $('login-error').textContent = ''; try { const result = await api('/api/login',{method:'POST',body:{password:$('password').value}}); csrf = result.csrfToken; $('password').value = ''; rulesSignature = servicesSignature = ''; logsLoaded = false; showApplication(); await refresh(); } catch (error) { $('login-error').textContent = error.message; } finally { button.disabled = false; } });
  $('logout').addEventListener('click',async () => { try { await api('/api/logout',{method:'POST',body:{}}); requireLogin(); } catch (error) { message(error.message,true); } });
  document.addEventListener('click',async event => { const button = event.target.closest('[data-action]'); if (!button || button.disabled || busy) return; button.disabled = true; try { await dispatch(button.dataset.action,button.dataset.value || ''); } catch (error) { message(error.message,true); } finally { button.disabled = false; } });
  $('editor-form').addEventListener('submit',async event => { event.preventDefault(); if (busy || !editorSave) return; const controls = [$('editor-save'),$('editor-cancel'),$('editor-close')]; controls.forEach(x => x.disabled = true); $('editor-error').textContent = ''; try { if (await editorSave() !== false) $('editor').close(); } catch (error) { $('editor-error').textContent = error.message; } finally { controls.forEach(x => x.disabled = false); } });
  for (const name of ['editor-close','editor-cancel']) $(name).addEventListener('click',() => { if (!busy) $('editor').close(); });
  $('editor').addEventListener('cancel',event => { if (busy) event.preventDefault(); });
  $('select-all-services').addEventListener('change',event => document.querySelectorAll('.service-select').forEach(x => { x.checked = event.target.checked; }));
  $('service-file').addEventListener('change',async event => { const file = event.target.files[0]; event.target.value = ''; if (file) try { await importServices(file); } catch (error) { message(error.message,true); } });
  for (const name of ['log-search','log-kind','log-from','log-to']) $(name).addEventListener('input',renderLogs);
  window.addEventListener('hashchange',changePage);
  setInterval(() => { if (!document.hidden && csrf) refresh(true); },2000);
  (async () => { try { const session = await api('/api/session'); if (!session.authenticated || !session.csrfToken) { requireLogin(); return; } csrf = session.csrfToken; showApplication(); await refresh(); } catch { requireLogin(); } })();
})();
