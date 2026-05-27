import React from 'react';
import { Icon } from '../components/Icon';
import { usePageHeader } from '../hooks/usePageHeader';

// Раздел «Общие» — пока demo-fallback: в Files API нет RPC шаринга (см. backend-web TODO).
// Данные статичные, как и в исходной странице.

type Tone = 'p' | 'g' | 'v' | 'b' | 'r' | 'y' | 'n';

interface Tab {
  key: string;
  label: string;
  count: number;
  icon: string;
}
interface Share {
  kind: string;
  ext: string;
  name: string;
  path: string;
  ppl: [string, Tone][];
  count: string;
  shared: string;
  perm: 'editor' | 'viewer';
  size: string;
  expires?: string;
}
interface LinkItem {
  name: string;
  url: string;
  exp: string;
  clicks: number;
  kind: string;
  ext: string;
  pw: boolean;
  expired?: boolean;
}
interface Stat {
  k: string;
  v: string;
  d: React.ReactNode;
  ic: string;
  accent?: boolean;
}
interface Person {
  nm: string;
  mail: string;
  tone: Tone;
  in: string;
  online: boolean;
  count: string;
}

const TABS: Tab[] = [
  { key: 'with-me', label: 'Доступно мне', count: 34, icon: 'share' },
  { key: 'by-me', label: 'Я открыл доступ', count: 21, icon: 'user' },
  { key: 'links', label: 'Публичные ссылки', count: 12, icon: 'link' },
  { key: 'pending', label: 'Запросы', count: 3, icon: 'clock' },
];

const SHARES_WITH_ME: Share[] = [
  { kind: 'folder', ext: 'DIR', name: 'Команда дизайна 2026', path: 'lena.s@cloud.bark.io · /design-2026', ppl: [['АК', 'p'], ['ДО', 'v'], ['МВ', 'b'], ['+4', 'n']], count: '7 чел.', shared: '14 мая', perm: 'editor', size: '4,2 ГБ' },
  { kind: 'pdf', ext: 'PDF', name: 'Контракт_2026_BarkCloud_v3.pdf', path: 'lena.s@cloud.bark.io', ppl: [['ЛС', 'g'], ['АК', 'p'], ['МВ', 'b'], ['+1', 'n']], count: '4 чел.', shared: 'Вчера', perm: 'viewer', size: '2,4 МБ', expires: '31 мая' },
  { kind: 'code', ext: 'GIT', name: 'cloud-sync-engine (репозиторий)', path: 'dima.o@cloud.bark.io · /repos/', ppl: [['ДО', 'v'], ['АК', 'p']], count: '2 чел.', shared: '2 дня', perm: 'editor', size: '186 МБ' },
  { kind: 'doc', ext: 'XLS', name: 'Бюджет-проекта-2026.xlsx', path: 'lena.s@cloud.bark.io', ppl: [['ЛС', 'g'], ['АК', 'p'], ['МВ', 'b'], ['+2', 'n']], count: '5 чел.', shared: '10 мая', perm: 'editor', size: '1,1 МБ' },
  { kind: 'vid', ext: 'MP4', name: 'all-hands-Q2-recording.mp4', path: 'marina.v@cloud.bark.io', ppl: [['МВ', 'b'], ['АК', 'p'], ['+12', 'n']], count: '14 чел.', shared: '5 мая', perm: 'viewer', size: '2,8 ГБ' },
  { kind: 'folder', ext: 'DIR', name: 'Фото, корпоратив 2025', path: 'hr@cloud.bark.io', ppl: [['HR', 'r'], ['+24', 'n']], count: '24 чел.', shared: '3 мес', perm: 'viewer', size: '18,4 ГБ' },
];

const LINKS: LinkItem[] = [
  { name: 'Презентация Q2 (финал).pdf', url: 'cloud.bark.io/s/8x4F2-mPp9aQ', exp: '31 мая 2026', clicks: 42, kind: 'pdf', ext: 'PDF', pw: true },
  { name: 'photos-bali-2026.zip', url: 'cloud.bark.io/s/zT7uK1-vQ8wN', exp: '7 дней', clicks: 8, kind: 'zip', ext: 'ZIP', pw: false },
  { name: 'BarkCloud · Pitch Deck.pdf', url: 'cloud.bark.io/s/Ld3HmQ-w4xRr', exp: 'Бессрочно', clicks: 312, kind: 'pdf', ext: 'PDF', pw: true },
  { name: 'Отчёт_Q1.xlsx', url: 'cloud.bark.io/s/9j6Vp2-tNc1k', exp: 'Истёк · 12 мая', clicks: 14, kind: 'doc', ext: 'XLS', pw: false, expired: true },
];

const STATS: Stat[] = [
  { k: 'Доступно мне', v: '34', d: <>+<span className="pos">3</span> за неделю</>, ic: 'share', accent: true },
  { k: 'Я открыл', v: '21', d: '+1 за неделю', ic: 'user' },
  { k: 'Активных ссылок', v: '12', d: '2 истекают в 7 дн.', ic: 'link' },
  { k: 'Запросов в ожидании', v: '3', d: '1 новый сегодня', ic: 'bell' },
];

const PEOPLE: Person[] = [
  { nm: 'Лена С.', mail: 'lena.s', tone: 'g', in: 'ЛС', online: true, count: '18 файлов' },
  { nm: 'Дима О.', mail: 'dima.o', tone: 'v', in: 'ДО', online: true, count: '6 файлов' },
  { nm: 'Марина В.', mail: 'marina.v', tone: 'b', in: 'МВ', online: false, count: '4 файла' },
  { nm: 'HR · BarkCloud', mail: 'hr', tone: 'r', in: 'HR', online: false, count: '3 папки' },
  { nm: 'Паша М.', mail: 'pasha.m', tone: 'y', in: 'ПМ', online: true, count: '2 файла' },
  { nm: 'Аня Г.', mail: 'anya.g', tone: 'p', in: 'АГ', online: false, count: '1 файл' },
];

const BY_ME_COUNT = 21;
const WITH_ME_COUNT = 34;
const LINKS_META = '12 ссылок · 2 истекают в 7 дней';
const MAIL_DOMAIN = 'cloud.bark.io';

const TONES: Record<Tone, [string, string]> = {
  p: ['var(--md-primary-container)', 'var(--md-on-primary-container)'],
  g: ['#C8E6C9', '#1B5E20'],
  v: ['#D1C4E9', '#311B92'],
  b: ['#BBDEFB', '#0D47A1'],
  r: ['#FFCDD2', '#B71C1C'],
  y: ['#FFE0B2', '#6D4C00'],
  n: ['var(--md-surface-container-highest)', 'var(--md-on-surface-variant)'],
};

function ShareCard({ s }: { s: Share }) {
  return (
    <div className="share-card">
      <div className={'file-icon ' + s.kind}>{s.ext}</div>
      <div className="main-col">
        <div className="name">{s.name}</div>
        <div className="path">{s.path}</div>
      </div>
      <div className="ppl-col">
        <div className="avatars">
          {s.ppl.map((a, i) => {
            const [bg, fg] = TONES[a[1]];
            return (
              <div key={i} className="av" style={{ background: bg, color: fg }}>
                {a[0]}
              </div>
            );
          })}
        </div>
        <div className="ppl-text">
          <span className="b">{s.count}</span>
        </div>
      </div>
      <div className="meta-col">
        <div className="meta-line">
          <span className="k">Размер</span> {s.size}
        </div>
        <div className="meta-line">
          <span className="k">Открыто</span> {s.shared}
        </div>
        {s.expires && (
          <div className="meta-line" style={{ color: 'var(--md-warning)' }}>
            <span className="k">Истекает</span> {s.expires}
          </div>
        )}
      </div>
      <div className="act-col">
        <span className={'perm-badge ' + s.perm}>
          {s.perm === 'editor' ? <Icon.pencil size={12} /> : <Icon.eye size={12} />}
          {s.perm === 'editor' ? 'Редактор' : 'Просмотр'}
        </span>
      </div>
    </div>
  );
}

function LinkCard({ l }: { l: LinkItem }) {
  return (
    <div className="link-card" style={l.expired ? { opacity: 0.55 } : {}}>
      <div className="link-icon">
        <Icon.link size={22} />
      </div>
      <div>
        <div style={{ fontSize: 15, fontWeight: 500, color: 'var(--md-on-surface)', marginBottom: 8 }}>{l.name}</div>
        <div className="link-url">
          <Icon.link size={12} />
          <span>
            <span className="accent">
              {l.url.split('/')[0]}/{l.url.split('/')[1]}/
            </span>
            {l.url.split('/')[2]}
          </span>
        </div>
      </div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
        <div className="meta-line">
          <span className="k">Кликов</span> <span style={{ color: 'var(--md-on-surface)', fontWeight: 500 }}>{l.clicks}</span>
        </div>
        <div className="meta-line">
          <span className="k">Истекает</span> <span style={{ color: l.expired ? 'var(--md-error)' : 'var(--md-on-surface)' }}>{l.exp}</span>
        </div>
        {l.pw && (
          <div className="meta-line" style={{ color: 'var(--md-success)' }}>
            <Icon.lock size={12} /> С паролем
          </div>
        )}
      </div>
      <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 4 }}>
        <button className="icon-btn" title="Скопировать">
          <Icon.link size={18} />
        </button>
        <button className="icon-btn" title="Ещё">
          <Icon.more size={18} />
        </button>
      </div>
    </div>
  );
}

export function SharedPage() {
  const [tab, setTab] = React.useState('with-me');

  usePageHeader(
    () => ({
      title: 'Общий доступ',
      kicker: (
        <>
          <span>Совместное</span>
          <span className="sep">/</span>
          <span className="cur">Общие</span>
        </>
      ),
      actions: (
        <>
          <button className="btn outlined">
            <Icon.link size={16} /> Новая ссылка
          </button>
          <button className="btn primary">
            <Icon.plus size={16} /> Поделиться
          </button>
        </>
      ),
    }),
    [],
  );

  return (
    <>
      <div className="stat-tiles">
        {STATS.map((s, i) => {
          const Ic = Icon[s.ic];
          return (
            <div key={i} className={'stat-tile' + (s.accent ? ' accent' : '')}>
              <div className="k">
                <Ic size={16} /> {s.k}
              </div>
              <div className="v">{s.v}</div>
              <div className="d">{s.d}</div>
            </div>
          );
        })}
      </div>

      <div className="sh-tabs">
        {TABS.map((t) => {
          const Ic = Icon[t.icon];
          return (
            <button key={t.key} className={'sh-tab' + (tab === t.key ? ' on' : '')} onClick={() => setTab(t.key)}>
              <Ic size={18} />
              {t.label}
              <span className="count">{t.count}</span>
            </button>
          );
        })}
      </div>

      <div className="right-pane">
        <div>
          {tab === 'links' ? (
            <>
              <div className="section-head">
                <h2>Активные публичные ссылки</h2>
                <div className="meta">{LINKS_META}</div>
              </div>
              {LINKS.map((l, i) => (
                <LinkCard key={i} l={l} />
              ))}
            </>
          ) : (
            <>
              <div className="section-head">
                <h2>{tab === 'by-me' ? 'Файлы, которые я открыл' : tab === 'pending' ? 'Запросы доступа' : 'Файлы, доступные мне'}</h2>
                <div className="meta">
                  {SHARES_WITH_ME.length} из {tab === 'by-me' ? BY_ME_COUNT : WITH_ME_COUNT} · сорт. по дате
                </div>
              </div>
              {SHARES_WITH_ME.map((s, i) => (
                <ShareCard key={i} s={s} />
              ))}
            </>
          )}
        </div>

        <div className="ppl-panel">
          <h3>Часто работаете вместе</h3>
          {PEOPLE.map((p, i) => {
            const [bg, fg] = TONES[p.tone];
            return (
              <div key={i} className="ppl-row">
                <div className="av" style={{ background: bg, color: fg }}>
                  {p.in}
                  <span className={'online' + (p.online ? '' : ' off')} />
                </div>
                <div>
                  <div className="nm">{p.nm}</div>
                  <div className="meta">
                    {p.mail}@{MAIL_DOMAIN}
                  </div>
                </div>
                <div className="count">{p.count}</div>
              </div>
            );
          })}
          <button className="btn outlined" style={{ width: '100%', marginTop: 16, justifyContent: 'center' }}>
            <Icon.plus size={16} /> Пригласить
          </button>
        </div>
      </div>
    </>
  );
}
