import React from 'react';
import { useParams } from 'react-router-dom';
import { Icon } from '../components/Icon';
import { PublicChips, PublicShareShell, PublicStatus } from '../components/public/PublicShareShell';
import { describePublicFile, fmtSize } from '../components/public/publicFile';
import { useDocumentHead } from '../hooks/useDocumentHead';
import { AudioBar } from '../components/media/MediaPlayer';

interface ShareInfo {
  found: boolean;
  name: string;
  mediaKind: string;
  previewUrl: string;
  downloadUrl: string;
  fileSize: number;
  downloadPath: string;
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

  if (state === 'loading') return <PublicStatus title="Открываем трек" loading />;
  if (state === 'notfound') {
    return (
      <PublicStatus
        title="Трек недоступен"
        text="Трек не найден или владелец отозвал доступ."
      />
    );
  }

  const file = describePublicFile(state.name, state.mediaKind);
  return (
    <PublicShareShell narrow>
      <section className="public-track">
        <div className="public-track-cover public-ph">
          {state.previewUrl ? (
            <img src={state.previewUrl} alt="" />
          ) : (
            <span className="public-ph-tile">
              <Icon.music size={36} />
            </span>
          )}
        </div>
        <div className="public-track-body">
          <div>
            <div className="public-label">Публичный трек</div>
            <h1>{titleFromName(state.name)}</h1>
            <PublicChips chips={[file.typeLabel, state.fileSize > 0 && fmtSize(state.fileSize)]} />
          </div>
          <AudioBar onMediaRef={persistAudioVolumeRef} src={state.downloadUrl} />
          <div>
            <a className="public-pill is-tonal public-st" href={state.downloadPath || `/s/${token}`}>
              <Icon.download size={20} />
              Скачать
            </a>
          </div>
        </div>
      </section>
    </PublicShareShell>
  );
}
