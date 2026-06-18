import React from 'react';
import { useParams } from 'react-router-dom';
import { Icon } from '../components/Icon';
import { PublicShareHeader, PublicShareShell, PublicStatus } from '../components/public/PublicShareShell';
import { useDocumentHead } from '../hooks/useDocumentHead';

interface ShareInfo {
  found: boolean;
  name: string;
  mediaKind: string;
  previewUrl: string;
  downloadUrl: string;
  fileSize: number;
  downloadPath: string;
}

function fmtSize(bytes: number): string {
  if (!bytes) return '';
  const u = ['Б', 'КБ', 'МБ', 'ГБ', 'ТБ'];
  let i = 0;
  let v = bytes;
  while (v >= 1024 && i < u.length - 1) {
    v /= 1024;
    i++;
  }
  return (i === 0 ? v.toFixed(0) : v.toFixed(v < 10 ? 1 : 0)).replace('.', ',') + ' ' + u[i];
}

function titleFromName(name: string): string {
  const dot = name.lastIndexOf('.');
  return dot > 0 ? name.slice(0, dot) : name;
}

function persistAudioVolumeRef(audio: HTMLAudioElement | null): void {
  if (!audio || (audio as unknown as { _volBound?: boolean })._volBound) return;
  (audio as unknown as { _volBound?: boolean })._volBound = true;

  const m = document.cookie.match(/(?:^|;\s*)bark_audio_vol=([^;]+)/);
  if (m) {
    try {
      const p = JSON.parse(decodeURIComponent(m[1]));
      const volume = Math.min(1, Math.max(0, Number(p.v)));
      audio.volume = Number.isNaN(volume) ? 0.8 : volume;
      audio.muted = !!p.m;
    } catch {
      audio.volume = 0.8;
    }
  }
  audio.addEventListener('volumechange', () => {
    const payload = encodeURIComponent(JSON.stringify({ v: audio.volume, m: audio.muted }));
    document.cookie = `bark_audio_vol=${payload}; path=/; max-age=31536000; samesite=lax`;
  });
}

export function PublicMusicTrackPage() {
  const { token } = useParams<{ token: string }>();
  const [state, setState] = React.useState<'loading' | 'notfound' | ShareInfo>('loading');
  const info = typeof state === 'object' ? state : null;
  const title = info ? titleFromName(info.name) : state === 'notfound' ? 'Трек недоступен' : 'Публичный трек';

  useDocumentHead(
    () => ({ title, iconUrl: info?.previewUrl || null }),
    [title, info?.previewUrl],
  );

  React.useEffect(() => {
    let alive = true;
    setState('loading');
    fetch(`/s/${token}/info`)
      .then((r) => (r.ok ? r.json() : Promise.reject(new Error('not found'))))
      .then((d: ShareInfo) => {
        if (!alive) return;
        setState(d.found && d.mediaKind === 'audio' ? d : 'notfound');
      })
      .catch(() => alive && setState('notfound'));
    return () => {
      alive = false;
    };
  }, [token]);

  if (state === 'loading') return <PublicStatus icon={Icon.music} title="Открываем трек" loading />;
  if (state === 'notfound') {
    return (
      <PublicStatus
        icon={Icon.music}
        title="Трек недоступен"
        text="Трек не найден или владелец отозвал доступ."
      />
    );
  }

  return (
    <PublicShareShell>
      <PublicShareHeader
        icon={Icon.music}
        coverUrl={state.previewUrl || undefined}
        label="Публичный трек BarkCloud"
        title={titleFromName(state.name)}
        meta={state.fileSize > 0 ? fmtSize(state.fileSize) : undefined}
      >
        <a className="btn primary" href={state.downloadPath || `/s/${token}`} style={{ display: 'inline-flex', alignItems: 'center', gap: 8 }}>
          <Icon.download size={18} /> Скачать
        </a>
      </PublicShareHeader>

      <div className="public-track-card">
        <div className="public-track-cover">
          {state.previewUrl ? <img src={state.previewUrl} alt="" /> : <Icon.music size={56} />}
        </div>
        <div className="public-track-body">
          <div className="public-track-title">{titleFromName(state.name)}</div>
          <div className="public-track-sub">Аудиотрек</div>
          <audio ref={persistAudioVolumeRef} src={state.downloadUrl} controls />
        </div>
      </div>
    </PublicShareShell>
  );
}
