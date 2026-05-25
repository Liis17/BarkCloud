/* BarkCloud — shared shell & icons (Material 3 Expressive)
 *
 * ════════════════════════════════════════════════════════════════════════
 *  SERVER-SIDE TEMPLATE VARIABLES (used in this file)
 * ════════════════════════════════════════════════════════════════════════
 *  Server должен заменить {{...}} перед отдачей HTML клиенту.
 *  Синтаксис: Mustache / Handlebars / Jinja2-совместимый.
 *    {{ var }}     — экранируемое строковое значение
 *    {{{ var }}}   — RAW (для JSON в JS-литералы и т.п.)
 *
 *  app.version            — версия BarkCloud, напр. "v2.4.1"
 *  app.edition            — редакция, напр. "self-host"
 *
 *  user.initials          — инициалы для аватара, напр. "АК"
 *  user.display_name      — отображаемое имя, напр. "Антон К."
 *  user.role              — роль / логин, напр. "admin"
 *
 *  server.host            — хост инстанса, напр. "cloud.bark.io"
 *
 *  storage.used_label     — "312,4 ГБ"
 *  storage.total_label    — "500 ГБ"
 *  storage.percent        — число 0..100
 *
 *  sync.status            — "Синхронизировано" / "Идёт синхронизация" / ...
 *  sync.last_at           — "14:02"
 *
 *  nav.photos_count       — "4 218"
 *  nav.videos_count       — "186"
 *  nav.files_count        — "12,4k"
 *  nav.shared_count       — "34"
 *  nav.links_count        — "12"
 * ════════════════════════════════════════════════════════════════════════
 */

/* ─────────── ICONS (Material Symbols-style, 24dp default) ─────────── */
const Icon = {
  cloud: (p={}) => <svg width={p.size||24} height={p.size||24} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><path d="M17.5 19a4.5 4.5 0 0 0 0-9 .5.5 0 0 1-.5-.5 5 5 0 0 0-9.9-1 4 4 0 0 0-3.6 4 4 4 0 0 0 4 4.5h10z"/></svg>,
  photo: (p={}) => <svg className={p.className||"ico"} width={p.size||24} height={p.size||24} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><rect x="3" y="3" width="18" height="18" rx="3"/><circle cx="8.5" cy="8.5" r="1.6"/><path d="M21 15l-5-5L5 21"/></svg>,
  video: (p={}) => <svg className={p.className||"ico"} width={p.size||24} height={p.size||24} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><rect x="2" y="5" width="14" height="14" rx="2"/><path d="M16 9l6-3v12l-6-3"/></svg>,
  file: (p={}) => <svg className={p.className||"ico"} width={p.size||24} height={p.size||24} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><path d="M4 4h10l6 6v10a2 2 0 0 1-2 2H4z"/><path d="M14 4v6h6"/></svg>,
  folder: (p={}) => <svg className={p.className||"ico"} width={p.size||24} height={p.size||24} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/></svg>,
  share: (p={}) => <svg className={p.className||"ico"} width={p.size||24} height={p.size||24} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><circle cx="6" cy="12" r="3"/><circle cx="18" cy="6" r="3"/><circle cx="18" cy="18" r="3"/><line x1="8.6" y1="10.6" x2="15.4" y2="7.4"/><line x1="8.6" y1="13.4" x2="15.4" y2="16.6"/></svg>,
  settings: (p={}) => <svg className={p.className||"ico"} width={p.size||24} height={p.size||24} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09a1.65 1.65 0 0 0-1-1.51 1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09a1.65 1.65 0 0 0 1.51-1 1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z"/></svg>,
  trash: (p={}) => <svg className={p.className||"ico"} width={p.size||24} height={p.size||24} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><polyline points="3 6 5 6 21 6"/><path d="M19 6l-2 14a2 2 0 0 1-2 2H9a2 2 0 0 1-2-2L5 6"/><path d="M10 11v6M14 11v6"/></svg>,
  star: (p={}) => <svg className={p.className||"ico"} width={p.size||24} height={p.size||24} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><polygon points="12 2 15.09 8.26 22 9.27 17 14.14 18.18 21.02 12 17.77 5.82 21.02 7 14.14 2 9.27 8.91 8.26 12 2"/></svg>,
  link: (p={}) => <svg className={p.className||"ico"} width={p.size||24} height={p.size||24} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><path d="M10 13a5 5 0 0 0 7.07 0l3-3a5 5 0 1 0-7.07-7.07L11 5"/><path d="M14 11a5 5 0 0 0-7.07 0l-3 3a5 5 0 0 0 7.07 7.07L13 19"/></svg>,
  upload: (p={}) => <svg width={p.size||20} height={p.size||20} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><polyline points="17 8 12 3 7 8"/><line x1="12" y1="3" x2="12" y2="15"/></svg>,
  plus: (p={}) => <svg width={p.size||20} height={p.size||20} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><line x1="12" y1="5" x2="12" y2="19"/><line x1="5" y1="12" x2="19" y2="12"/></svg>,
  search: (p={}) => <svg width={p.size||20} height={p.size||20} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><circle cx="11" cy="11" r="8"/><line x1="21" y1="21" x2="16.65" y2="16.65"/></svg>,
  bell: (p={}) => <svg width={p.size||22} height={p.size||22} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><path d="M18 8A6 6 0 0 0 6 8c0 7-3 9-3 9h18s-3-2-3-9"/><path d="M13.73 21a2 2 0 0 1-3.46 0"/></svg>,
  grid: (p={}) => <svg width={p.size||20} height={p.size||20} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><rect x="3" y="3" width="7" height="7" rx="1"/><rect x="14" y="3" width="7" height="7" rx="1"/><rect x="14" y="14" width="7" height="7" rx="1"/><rect x="3" y="14" width="7" height="7" rx="1"/></svg>,
  list: (p={}) => <svg width={p.size||20} height={p.size||20} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><line x1="8" y1="6" x2="21" y2="6"/><line x1="8" y1="12" x2="21" y2="12"/><line x1="8" y1="18" x2="21" y2="18"/><line x1="3" y1="6" x2="3.01" y2="6"/><line x1="3" y1="12" x2="3.01" y2="12"/><line x1="3" y1="18" x2="3.01" y2="18"/></svg>,
  filter: (p={}) => <svg width={p.size||20} height={p.size||20} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><line x1="4" y1="6" x2="20" y2="6"/><line x1="7" y1="12" x2="17" y2="12"/><line x1="10" y1="18" x2="14" y2="18"/></svg>,
  chev: (p={}) => <svg width={p.size||18} height={p.size||18} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><polyline points="9 18 15 12 9 6"/></svg>,
  chevDown: (p={}) => <svg width={p.size||18} height={p.size||18} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><polyline points="6 9 12 15 18 9"/></svg>,
  play: (p={}) => <svg width={p.size||22} height={p.size||22} viewBox="0 0 24 24" fill="currentColor"><polygon points="6 4 20 12 6 20"/></svg>,
  download: (p={}) => <svg width={p.size||18} height={p.size||18} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><polyline points="7 10 12 15 17 10"/><line x1="12" y1="15" x2="12" y2="3"/></svg>,
  more: (p={}) => <svg width={p.size||20} height={p.size||20} viewBox="0 0 24 24" fill="currentColor"><circle cx="5" cy="12" r="1.8"/><circle cx="12" cy="12" r="1.8"/><circle cx="19" cy="12" r="1.8"/></svg>,
  arrow: (p={}) => <svg width={p.size||18} height={p.size||18} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><line x1="5" y1="12" x2="19" y2="12"/><polyline points="12 5 19 12 12 19"/></svg>,
  user: (p={}) => <svg className={p.className||"ico"} width={p.size||24} height={p.size||24} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><path d="M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2"/><circle cx="12" cy="7" r="4"/></svg>,
  lock: (p={}) => <svg className={p.className||"ico"} width={p.size||24} height={p.size||24} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><rect x="3" y="11" width="18" height="11" rx="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/></svg>,
  device: (p={}) => <svg className={p.className||"ico"} width={p.size||24} height={p.size||24} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><rect x="2" y="4" width="20" height="14" rx="2"/><line x1="8" y1="22" x2="16" y2="22"/><line x1="12" y1="18" x2="12" y2="22"/></svg>,
  palette: (p={}) => <svg className={p.className||"ico"} width={p.size||24} height={p.size||24} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><circle cx="13.5" cy="6.5" r=".8" fill="currentColor"/><circle cx="17.5" cy="10.5" r=".8" fill="currentColor"/><circle cx="8.5" cy="7.5" r=".8" fill="currentColor"/><circle cx="6.5" cy="12.5" r=".8" fill="currentColor"/><path d="M12 2a10 10 0 0 0 0 20 3 3 0 0 0 0-6 1.5 1.5 0 0 1-1.5-1.5 1.5 1.5 0 0 1 1.5-1.5H17a5 5 0 0 0 5-5c0-4.97-4.5-9-10-9z"/></svg>,
  server: (p={}) => <svg className={p.className||"ico"} width={p.size||24} height={p.size||24} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><rect x="2" y="3" width="20" height="8" rx="2"/><rect x="2" y="13" width="20" height="8" rx="2"/><line x1="6" y1="7" x2="6.01" y2="7"/><line x1="6" y1="17" x2="6.01" y2="17"/></svg>,
  key: (p={}) => <svg width={p.size||18} height={p.size||18} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M21 2l-2 2m-7.61 7.61a5.5 5.5 0 1 1-7.778 7.778 5.5 5.5 0 0 1 7.777-7.777zm0 0L15.5 7.5m0 0l3 3L22 7l-3-3m-3.5 3.5L19 4"/></svg>,
  globe: (p={}) => <svg className={p.className||"ico"} width={p.size||24} height={p.size||24} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><circle cx="12" cy="12" r="10"/><line x1="2" y1="12" x2="22" y2="12"/><path d="M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z"/></svg>,
  clock: (p={}) => <svg className={p.className||"ico"} width={p.size||18} height={p.size||18} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><circle cx="12" cy="12" r="10"/><polyline points="12 6 12 12 16 14"/></svg>,
  eye: (p={}) => <svg width={p.size||18} height={p.size||18} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z"/><circle cx="12" cy="12" r="3"/></svg>,
  pencil: (p={}) => <svg width={p.size||18} height={p.size||18} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><path d="M12 20h9"/><path d="M16.5 3.5a2.121 2.121 0 1 1 3 3L7 19l-4 1 1-4z"/></svg>,
  check: (p={}) => <svg width={p.size||18} height={p.size||18} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" strokeLinejoin="round"><polyline points="20 6 9 17 4 12"/></svg>,
  x: (p={}) => <svg width={p.size||18} height={p.size||18} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/></svg>,
  refresh: (p={}) => <svg width={p.size||18} height={p.size||18} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><polyline points="23 4 23 10 17 10"/><polyline points="1 20 1 14 7 14"/><path d="M3.51 9a9 9 0 0 1 14.85-3.36L23 10M1 14l4.64 4.36A9 9 0 0 0 20.49 15"/></svg>,
  power: (p={}) => <svg width={p.size||18} height={p.size||18} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M18.36 6.64a9 9 0 1 1-12.73 0"/><line x1="12" y1="2" x2="12" y2="12"/></svg>,
};

/* ─────────── SIDEBAR ─────────── */
const NAV_PRIMARY = [
  {key:'photos', href:'/photos', label:'Фото', icon:'photo', count:'{{ nav.photos_count }}'},
  {key:'videos', href:'/videos', label:'Видео', icon:'video', count:'{{ nav.videos_count }}'},
  {key:'files', href:'/files', label:'Файлы', icon:'folder', count:'{{ nav.files_count }}'},
];
const NAV_SHARE = [
  {key:'shared', href:'/shared', label:'Общие', icon:'share', count:'{{ nav.shared_count }}'},
  {key:'links', href:'/shared', label:'Ссылки', icon:'link', count:'{{ nav.links_count }}'},
  {key:'starred', href:'/files', label:'Избранное', icon:'star'},
];
const NAV_OTHER = [
  {key:'trash', href:'/trash', label:'Корзина', icon:'trash'},
  {key:'settings', href:'/settings', label:'Настройки', icon:'settings'},
];

function Sidebar({ page }) {
  function renderItem(item) {
    const active = item.key === page;
    const IconC = Icon[item.icon];
    return (
      <a key={item.key} className={"sb-item" + (active ? " active" : "")} href={item.href}>
        <span className="ico"><IconC size={22}/></span>
        <span>{item.label}</span>
        {item.count && <span className="count">{item.count}</span>}
      </a>
    );
  }
  return (
    <aside className="sidebar">
      <div className="sb-brand">
        <div className="mark"><Icon.cloud size={22}/></div>
        <div>
          <div className="name">BarkCloud</div>
          <div className="v">{"{{ app.version }}"} · {"{{ app.edition }}"}</div>
        </div>
      </div>

      <nav className="sb-nav">
        <div>
          <div className="sb-section-label">Библиотека</div>
          <div className="sb-items">{NAV_PRIMARY.map(renderItem)}</div>
        </div>
        <div>
          <div className="sb-section-label">Совместное</div>
          <div className="sb-items">{NAV_SHARE.map(renderItem)}</div>
        </div>
        <div>
          <div className="sb-section-label">Прочее</div>
          <div className="sb-items">{NAV_OTHER.map(renderItem)}</div>
        </div>
      </nav>

      <div className="sb-storage">
        <div className="sb-storage-head">
          <span>Хранилище</span>
          <span className="used">{"{{ storage.used_label }}"} / {"{{ storage.total_label }}"}</span>
        </div>
        <div className="bar"><div className="bar-fill" style={{width:'{{ storage.percent }}%'}}/></div>
        <div className="sb-storage-foot">
          <span>{"{{ storage.percent }}"}% использовано</span>
          <a href="/settings">Расширить</a>
        </div>
      </div>

      <a className="sb-user" href="/settings">
        <div className="avatar">{"{{ user.initials }}"}</div>
        <div className="who">
          <div className="uname">{"{{ user.display_name }}"}</div>
          <div className="uhost">{"{{ user.role }}"} · {"{{ server.host }}"}</div>
        </div>
        <span className="chev"><Icon.chev size={18}/></span>
      </a>
    </aside>
  );
}

/* ─────────── TOPBAR ─────────── */
function Topbar({ kicker, title, actions, search = true }) {
  return (
    <header className="topbar">
      <div className="tb-title">
        {kicker && <div className="tb-kicker">{kicker}</div>}
        <div className="tb-h1">{title}</div>
      </div>
      {search && (
        <div className="tb-search">
          <span className="si"><Icon.search size={20}/></span>
          <input type="text" placeholder="Найти в облаке: файлы, люди, теги…"/>
          <span className="kbd">⌘ K</span>
        </div>
      )}
      <div className="tb-actions">
        {actions}
        <button className="icon-btn" title="Уведомления"><Icon.bell size={22}/><span className="dot-badge"/></button>
      </div>
    </header>
  );
}

/* ─────────── FOOTER ─────────── */
function Footbar({ status = '{{ sync.status }}' }) {
  return (
    <footer className="footbar">
      <div className="left">
        <span className="pulse"><span className="dot"/>{status}</span>
        <span>Последняя синхронизация · {"{{ sync.last_at }}"}</span>
      </div>
      <div className="right">
        <a href="#">Документация</a>
        <a href="#">Статус</a>
        <a href="#">Горячие клавиши</a>
        <span>AES-256 · Zero-knowledge</span>
      </div>
    </footer>
  );
}

/* ─────────── APP SHELL ─────────── */
function AppShell({ page, kicker, title, actions, children, footerStatus, search = true }) {
  return (
    <div className="app">
      <Sidebar page={page}/>

      <div className="main">
        <Topbar kicker={kicker} title={title} actions={actions} search={search}/>
        <div className="content">{children}</div>
        <Footbar status={footerStatus}/>
      </div>
    </div>
  );
}

/* ════════════════════════════════════════════════════════════════════════
 *  DATA LAYER — same-origin /api (проксирует в Files-сервис с токеном из cookie)
 * ════════════════════════════════════════════════════════════════════════ */

async function api(path, opts = {}) {
  const res = await fetch(path, {
    credentials: 'same-origin',
    headers: { 'Content-Type': 'application/json', ...(opts.headers || {}) },
    ...opts,
  });
  if (res.status === 401) { window.location.href = '/login'; throw new Error('unauthorized'); }

  const text = await res.text();
  let data = null;
  if (text) { try { data = JSON.parse(text); } catch (e) { /* не-JSON ответ */ } }

  if (!res.ok) {
    const err = new Error((data && data.error) || ('Ошибка ' + res.status));
    if (data && data.code) err.code = data.code;
    throw err;
  }
  return data;
}
const apiGet = (path) => api(path);
const apiPost = (path, body) => api(path, { method: 'POST', body: JSON.stringify(body || {}) });

/* Открыть системный диалог выбора файлов */
function pickFiles({ accept, multiple = true } = {}) {
  return new Promise((resolve) => {
    const input = document.createElement('input');
    input.type = 'file';
    input.multiple = multiple;
    if (accept) input.accept = accept;
    input.style.display = 'none';
    document.body.appendChild(input);
    input.onchange = () => {
      const files = Array.from(input.files || []);
      document.body.removeChild(input);
      resolve(files);
    };
    input.click();
  });
}

/* Загрузка одного файла с прогрессом (XHR — fetch не отдаёт upload-progress) */
function uploadFile(file, onProgress) {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open('POST', '/api/files/upload');
    xhr.withCredentials = true;
    xhr.upload.onprogress = (e) => { if (e.lengthComputable && onProgress) onProgress(e.loaded / e.total); };
    xhr.onload = () => {
      if (xhr.status >= 200 && xhr.status < 300) {
        try { resolve(JSON.parse(xhr.responseText)); }
        catch (e) { reject(new Error('Некорректный ответ загрузки')); }
      } else if (xhr.status === 401) {
        window.location.href = '/login';
        reject(new Error('unauthorized'));
      } else {
        let msg = 'Ошибка ' + xhr.status;
        try { const d = JSON.parse(xhr.responseText); if (d.error) msg = d.error; } catch (e) {}
        reject(new Error(msg));
      }
    };
    xhr.onerror = () => reject(new Error('Сетевая ошибка загрузки'));
    const fd = new FormData();
    fd.append('file', file, file.name);
    xhr.send(fd);
  });
}

/* ════════════════════════════════════════════════════════════════════════
 *  SHARED UI — превью, лайтбокс, модалка, тосты
 * ════════════════════════════════════════════════════════════════════════ */

/* Превью медиа. Браузер сам выбирает ширину под размер блока (srcset + sizes + DPR). */
function MediaThumb({ media, sizes = '200px', className = 'thumb', alt = '' }) {
  const previews = (media && media.previews) || [];
  if (!previews.length) {
    return <div className={className} style={{ '--tint-a': '#C8A78C', '--tint-b': '#6F4A3A' }} />;
  }
  const srcSet = previews.map(p => `${p.url} ${p.w}w`).join(', ');
  const fallback = previews[previews.length - 1].url; // самое широкое
  return (
    <img className={className} src={fallback} srcSet={srcSet} sizes={sizes}
      alt={alt || (media && media.name) || ''} loading="lazy" style={{ objectFit: 'cover' }} />
  );
}

/* Полноэкранный просмотр ОРИГИНАЛА (временная ссылка через /api/files/download) */
function Lightbox({ media, onClose }) {
  const [url, setUrl] = React.useState(null);
  const [err, setErr] = React.useState(null);
  const fileId = media && media.id;

  React.useEffect(() => {
    let alive = true;
    setUrl(null); setErr(null);
    if (!fileId) return;
    apiGet('/api/files/download?ids=' + encodeURIComponent(fileId))
      .then(d => { if (alive) setUrl((d.urls && d.urls[fileId]) || null); })
      .catch(e => { if (alive) setErr(e.message); });
    return () => { alive = false; };
  }, [fileId]);

  React.useEffect(() => {
    const onKey = (e) => { if (e.key === 'Escape') onClose && onClose(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  if (!media) return null;
  const isVideo = media.kind === 'video';
  return (
    <div className="lightbox" onClick={onClose}>
      <button className="lb-close icon-btn" onClick={onClose} title="Закрыть"><Icon.x size={24} /></button>
      <div className="lb-stage" onClick={e => e.stopPropagation()}>
        {err && <div className="lb-msg">Не удалось загрузить оригинал: {err}</div>}
        {!err && !url && <div className="lb-msg"><span className="spinner" /> Загрузка оригинала…</div>}
        {url && isVideo && <video src={url} controls autoPlay />}
        {url && !isVideo && <img src={url} alt={media.name || ''} />}
      </div>
      {url && <a className="lb-download btn" href={url} download={media.name}><Icon.download size={16} /> Скачать</a>}
    </div>
  );
}

/* Модальное окно (Esc / клик по фону — закрыть) */
function Modal({ title, children, onClose, actions, wide }) {
  React.useEffect(() => {
    const onKey = (e) => { if (e.key === 'Escape') onClose && onClose(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);
  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className={"modal" + (wide ? " wide" : "")} onClick={e => e.stopPropagation()}>
        <div className="modal-head">
          <h3>{title}</h3>
          <button className="icon-btn" onClick={onClose} title="Закрыть"><Icon.x size={20} /></button>
        </div>
        <div className="modal-body">{children}</div>
        {actions && <div className="modal-actions">{actions}</div>}
      </div>
    </div>
  );
}

/* Тосты. Возвращает [node, push(msg, kind)] */
function useToast() {
  const [toasts, setToasts] = React.useState([]);
  const push = React.useCallback((msg, kind = 'ok') => {
    const id = Math.random().toString(36).slice(2);
    setToasts(t => [...t, { id, msg, kind }]);
    setTimeout(() => setToasts(t => t.filter(x => x.id !== id)), 4200);
  }, []);
  const node = (
    <div className="toast-stack">
      {toasts.map(t => <div key={t.id} className={"toast " + t.kind}>{t.msg}</div>)}
    </div>
  );
  return [node, push];
}

/* Состояние пустоты / загрузки списка */
function EmptyState({ icon = 'cloud', title, hint, action }) {
  const IconC = Icon[icon] || Icon.cloud;
  return (
    <div className="empty-state">
      <div className="es-icon"><IconC size={40} /></div>
      <div className="es-title">{title}</div>
      {hint && <div className="es-hint">{hint}</div>}
      {action}
    </div>
  );
}
function Loading({ label = 'Загрузка…' }) {
  return <div className="loading"><span className="spinner" /> {label}</div>;
}

/* ════════════════════════════════════════════════════════════════════════
 *  ОБЩЕЕ: даты, склонения, альбомы (используются на Фото и Видео)
 * ════════════════════════════════════════════════════════════════════════ */

const GRID_SIZES = '(max-width: 700px) 33vw, (max-width: 1280px) 20vw, 180px';

function plural(n, one, few, many) {
  const m10 = n % 10, m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return one;
  if (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) return few;
  return many;
}

const ruDate = new Intl.DateTimeFormat('ru-RU', { day: 'numeric', month: 'long', year: 'numeric' });
function dateLabel(d) {
  if (!d) return 'Без даты';
  const today = new Date(); today.setHours(0, 0, 0, 0);
  const day = new Date(d); day.setHours(0, 0, 0, 0);
  const diff = Math.round((today - day) / 86400000);
  if (diff === 0) return 'Сегодня';
  if (diff === 1) return 'Вчера';
  return ruDate.format(d);
}
function groupByDate(items) {
  const groups = [];
  const byKey = new Map();
  for (const m of items) {
    const d = m.createdAt ? new Date(m.createdAt) : null;
    const key = d ? d.toDateString() : 'unknown';
    if (!byKey.has(key)) {
      const g = { key, label: dateLabel(d), items: [] };
      byKey.set(key, g);
      groups.push(g);
    }
    byKey.get(key).items.push(m);
  }
  return groups;
}

function AlbumCard({ album, onOpen }) {
  return (
    <div className="album-card" onClick={() => onOpen(album)}>
      {album.coverUrl
        ? <img className="thumb" src={album.coverUrl} alt="" loading="lazy" style={{ objectFit: 'cover' }} />
        : <div className="thumb" style={{ '--tint-a': '#B4A3D6', '--tint-b': '#5B4889' }} />}
      <div className="overlay">
        <div className="badge">Альбом</div>
        <div className="a-name">{album.name}</div>
        <div className="a-meta">{album.count} {plural(album.count, 'элемент', 'элемента', 'элементов')}{album.description ? ' · ' + album.description : ''}</div>
      </div>
    </div>
  );
}

/* Создание / редактирование альбома */
function AlbumFormModal({ album, onClose, onSaved, toast }) {
  const [name, setName] = React.useState(album ? album.name : '');
  const [description, setDescription] = React.useState(album ? album.description : '');
  const [busy, setBusy] = React.useState(false);

  async function save() {
    if (!name.trim()) { toast('Введите название', 'err'); return; }
    setBusy(true);
    try {
      const saved = album
        ? await apiPost('/api/albums/update', { album: album.id, name, description })
        : await apiPost('/api/albums', { name, description });
      onSaved(saved);
    } catch (e) { toast(e.message, 'err'); }
    finally { setBusy(false); }
  }

  return (
    <Modal title={album ? 'Редактировать альбом' : 'Новый альбом'} onClose={onClose}
      actions={<>
        <button className="btn text" onClick={onClose}>Отмена</button>
        <button className="btn primary" onClick={save} disabled={busy}>{busy ? '…' : 'Сохранить'}</button>
      </>}>
      <label className="field-label">Название</label>
      <input type="text" value={name} onChange={e => setName(e.target.value)} autoFocus placeholder="Например: Отпуск 2026" />
      <label className="field-label">Описание</label>
      <textarea value={description} onChange={e => setDescription(e.target.value)} placeholder="Необязательно" />
    </Modal>
  );
}

/* Выбор медиа для добавления в альбом */
function PickMediaModal({ candidates, exclude, onClose, onAdd, toast, title = 'Добавить в альбом' }) {
  const [sel, setSel] = React.useState(() => new Set());
  const [busy, setBusy] = React.useState(false);
  const available = candidates.filter(p => !exclude.has(p.id));

  function toggle(id) {
    setSel(prev => { const n = new Set(prev); n.has(id) ? n.delete(id) : n.add(id); return n; });
  }
  async function add() {
    if (!sel.size) { onClose(); return; }
    setBusy(true);
    try { await onAdd([...sel]); }
    catch (e) { toast(e.message, 'err'); }
    finally { setBusy(false); }
  }

  return (
    <Modal wide title={title} onClose={onClose}
      actions={<>
        <button className="btn text" onClick={onClose}>Отмена</button>
        <button className="btn primary" onClick={add} disabled={busy}>Добавить{sel.size ? ` (${sel.size})` : ''}</button>
      </>}>
      {available.length === 0
        ? <div style={{ color: 'var(--md-on-surface-variant)', padding: '12px 0' }}>Нет элементов для добавления.</div>
        : <div className="pick-grid">
          {available.map(p => (
            <div key={p.id} className={"pick-cell" + (sel.has(p.id) ? ' on' : '')} onClick={() => toggle(p.id)}>
              <MediaThumb media={p} sizes="120px" />
              {sel.has(p.id) && <div className="pick-check"><Icon.check size={14} /></div>}
            </div>
          ))}
        </div>}
    </Modal>
  );
}

/* Просмотр альбома: сетка элементов, обложка, добавить/убрать, переименовать, удалить */
function AlbumDetail({ album, candidates, gridSizes = GRID_SIZES, onBack, onChanged, toast }) {
  const [items, setItems] = React.useState(null);
  const [lightbox, setLightbox] = React.useState(null);
  const [editing, setEditing] = React.useState(false);
  const [picking, setPicking] = React.useState(false);

  const load = React.useCallback(() => {
    setItems(null);
    apiGet('/api/albums/items?album=' + encodeURIComponent(album.id))
      .then(d => setItems(d.items || []))
      .catch(e => { toast(e.message, 'err'); setItems([]); });
  }, [album.id]);
  React.useEffect(load, [load]);

  const excludeIds = React.useMemo(() => new Set((items || []).map(i => i.id)), [items]);

  async function addItems(fileIds) {
    await apiPost('/api/albums/items/add', { album: album.id, fileIds });
    setPicking(false); load(); onChanged(); toast('Добавлено в альбом');
  }
  async function removeItem(id) {
    try { await apiPost('/api/albums/items/remove', { album: album.id, fileIds: [id] }); load(); onChanged(); }
    catch (e) { toast(e.message, 'err'); }
  }
  async function setCover(id) {
    try { await apiPost('/api/albums/update', { album: album.id, coverFileId: id }); onChanged(); toast('Обложка обновлена'); }
    catch (e) { toast(e.message, 'err'); }
  }
  async function removeAlbum() {
    if (!window.confirm('Удалить альбом? Файлы останутся в облаке.')) return;
    try { await apiPost('/api/albums/delete', { album: album.id }); onChanged(); onBack(); }
    catch (e) { toast(e.message, 'err'); }
  }

  return (
    <div>
      <div className="date-head" style={{ marginBottom: 18 }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
          <button className="icon-btn" onClick={onBack} title="Назад"><Icon.arrow size={20} style={{ transform: 'rotate(180deg)' }} /></button>
          <div>
            <h3>{album.name}</h3>
            {album.description && <div style={{ fontSize: 13, color: 'var(--md-on-surface-variant)' }}>{album.description}</div>}
          </div>
        </div>
        <div className="right" style={{ gap: 8 }}>
          <button className="btn outlined" onClick={() => setPicking(true)}><Icon.plus size={16} /> Добавить</button>
          <button className="btn outlined" onClick={() => setEditing(true)}><Icon.pencil size={16} /> Изменить</button>
          <button className="btn text" onClick={removeAlbum}><Icon.trash size={16} /> Удалить</button>
        </div>
      </div>

      {items === null ? <Loading /> :
        items.length === 0
          ? <EmptyState icon="photo" title="Альбом пуст" hint="Добавьте фото или видео из вашей галереи."
            action={<button className="btn primary" onClick={() => setPicking(true)}><Icon.plus size={16} /> Добавить</button>} />
          : <div className="photo-grid">
            {items.map(m => (
              <div key={m.id} className="photo" onClick={() => setLightbox(m)}>
                <MediaThumb media={m} sizes={gridSizes} />
                {m.kind === 'video' && <div className="vbadge"><Icon.play size={10} /> видео</div>}
                <div className="item-tools">
                  <button title="Сделать обложкой" onClick={(e) => { e.stopPropagation(); setCover(m.id); }}><Icon.star size={15} /></button>
                  <button title="Убрать из альбома" onClick={(e) => { e.stopPropagation(); removeItem(m.id); }}><Icon.x size={15} /></button>
                </div>
              </div>
            ))}
          </div>}

      {editing && <AlbumFormModal album={album} onClose={() => setEditing(false)}
        onSaved={() => { setEditing(false); onChanged(); toast('Сохранено'); }} toast={toast} />}
      {picking && <PickMediaModal candidates={candidates} exclude={excludeIds}
        onClose={() => setPicking(false)} onAdd={addItems} toast={toast} />}
      {lightbox && <Lightbox media={lightbox} onClose={() => setLightbox(null)} />}
    </div>
  );
}

/* Export to global scope */
Object.assign(window, {
  Icon, AppShell, Sidebar, Topbar, Footbar,
  api, apiGet, apiPost, pickFiles, uploadFile,
  MediaThumb, Lightbox, Modal, useToast, EmptyState, Loading,
  GRID_SIZES, plural, dateLabel, groupByDate,
  AlbumCard, AlbumFormModal, PickMediaModal, AlbumDetail
});
