// Даты, склонения, группировка — перенос из Pages/shared.jsx.

export const GRID_SIZES = '(max-width: 700px) 33vw, (max-width: 1280px) 20vw, 180px';

export function plural(n: number, one: string, few: string, many: string): string {
  const m10 = n % 10;
  const m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return one;
  if (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) return few;
  return many;
}

const ruDate = new Intl.DateTimeFormat('ru-RU', { day: 'numeric', month: 'long', year: 'numeric' });

export function dateLabel(d: Date | null): string {
  if (!d) return 'Без даты';
  const today = new Date();
  today.setHours(0, 0, 0, 0);
  const day = new Date(d);
  day.setHours(0, 0, 0, 0);
  const diff = Math.round((today.getTime() - day.getTime()) / 86400000);
  if (diff === 0) return 'Сегодня';
  if (diff === 1) return 'Вчера';
  return ruDate.format(d);
}

export interface DateGroup<T> {
  key: string;
  label: string;
  items: T[];
}

export function groupByDate<T extends { createdAt?: string | null }>(items: T[]): DateGroup<T>[] {
  const groups: DateGroup<T>[] = [];
  const byKey = new Map<string, DateGroup<T>>();
  for (const m of items) {
    const d = m.createdAt ? new Date(m.createdAt) : null;
    const key = d ? d.toDateString() : 'unknown';
    let g = byKey.get(key);
    if (!g) {
      g = { key, label: dateLabel(d), items: [] };
      byKey.set(key, g);
      groups.push(g);
    }
    g.items.push(m);
  }
  return groups;
}

const ruDateTime = new Intl.DateTimeFormat('ru-RU', {
  day: 'numeric',
  month: 'long',
  year: 'numeric',
  hour: '2-digit',
  minute: '2-digit',
});

export function fmtFull(d: string | null | undefined): string {
  if (!d) return '—';
  const dt = new Date(d);
  return isNaN(dt.getTime()) ? '—' : ruDateTime.format(dt);
}

export function kindRu(k: string | undefined): string {
  return ({ photo: 'Фото', video: 'Видео', document: 'Документ', audio: 'Аудио' } as Record<string, string>)[k || ''] || 'Файл';
}

export function formatDuration(seconds: number | null | undefined): string {
  const total = Math.max(0, Math.floor(seconds || 0));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  if (h > 0) return `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
  return `${m}:${String(s).padStart(2, '0')}`;
}
