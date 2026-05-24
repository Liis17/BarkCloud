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
  {key:'trash', href:'/files', label:'Корзина', icon:'trash'},
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

/* Export to global scope */
Object.assign(window, {
  Icon, AppShell, Sidebar, Topbar, Footbar
});
