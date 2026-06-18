import React from 'react';
import { Icon } from '../Icon';
import { EQUALIZER_BANDS, EQUALIZER_PRESETS, useAudioPlayer } from '../../hooks/useAudioPlayer';
import { formatDuration } from '../../lib/format';

export function MiniPlayer() {
  const player = useAudioPlayer();
  const [eqOpen, setEqOpen] = React.useState(false);
  const track = player.current;
  if (!track) return null;

  const cover = track.largeCoverUrl || track.coverUrl;
  return (
    <div className="mini-player">
      <div className="mp-cover">
        {cover ? <img src={cover} alt="" /> : <Icon.music size={26} />}
      </div>
      <div className="mp-meta">
        <div className="mp-title">{track.title || track.file.name}</div>
        <div className="mp-sub">{track.artist || track.album || 'Неизвестный исполнитель'}</div>
        <div className="mp-progress">
          <input
            type="range"
            min={0}
            max={Math.max(1, player.duration)}
            value={Math.min(player.currentTime, Math.max(1, player.duration))}
            onChange={(e) => player.seek(Number(e.currentTarget.value))}
            aria-label="Позиция трека"
          />
          <span>{formatDuration(player.currentTime)} / {formatDuration(player.duration || track.duration)}</span>
        </div>
      </div>
      <div className="mp-controls">
        <button className={'icon-btn' + (player.shuffle ? ' active' : '')} onClick={() => player.setShuffle(!player.shuffle)} title="Перемешать">
          <Icon.shuffle size={18} />
        </button>
        <button className="icon-btn" onClick={player.previous} title="Предыдущий">
          <Icon.skipBack size={18} />
        </button>
        <button className="icon-btn primary" onClick={player.toggle} title={player.isPlaying ? 'Пауза' : 'Играть'}>
          {player.isPlaying ? <Icon.pause size={18} /> : <Icon.play size={18} />}
        </button>
        <button className="icon-btn" onClick={player.next} title="Следующий">
          <Icon.skipForward size={18} />
        </button>
        <button className={'icon-btn mp-eq-toggle' + (player.equalizer.enabled ? ' active' : '')} onClick={() => setEqOpen((v) => !v)} title="Эквалайзер">
          <Icon.sliders size={18} />
        </button>
      </div>
      <div className="mp-volume">
        <button className="icon-btn" onClick={() => player.setMuted(!player.muted)} title={player.muted ? 'Включить звук' : 'Выключить звук'}>
          {player.muted ? '0' : Math.round(player.volume * 100)}
        </button>
        <input
          type="range"
          min={0}
          max={1}
          step={0.01}
          value={player.muted ? 0 : player.volume}
          onChange={(e) => {
            player.setMuted(false);
            player.setVolume(Number(e.currentTarget.value));
          }}
          aria-label="Громкость музыки"
        />
      </div>
      {eqOpen && (
        <div className="mp-eq-panel" role="dialog" aria-label="Эквалайзер">
          <div className="mp-eq-head">
            <div>
              <div className="mp-eq-title">Эквалайзер</div>
              <div className="mp-eq-sub">{player.equalizer.enabled ? 'Активен' : 'Отключён'}</div>
            </div>
            <label className="mp-eq-switch">
              <input
                type="checkbox"
                checked={player.equalizer.enabled}
                onChange={(e) => player.setEqualizerEnabled(e.currentTarget.checked)}
              />
              <span>Вкл</span>
            </label>
          </div>
          <div className="mp-eq-presets">
            {EQUALIZER_PRESETS.map((preset) => (
              <button
                key={preset.id}
                className={player.equalizer.preset === preset.id ? 'active' : ''}
                onClick={() => player.applyEqualizerPreset(preset.id)}
              >
                {preset.label}
              </button>
            ))}
          </div>
          <div className="mp-eq-sliders">
            {EQUALIZER_BANDS.map((band, i) => {
              const gain = player.equalizer.gains[i] ?? 0;
              return (
                <label key={band.frequency} className="mp-eq-band">
                  <span className="mp-eq-gain">{gain > 0 ? `+${gain}` : gain}</span>
                  <input
                    type="range"
                    min={-12}
                    max={12}
                    step={1}
                    value={gain}
                    onChange={(e) => player.setEqualizerGain(i, Number(e.currentTarget.value))}
                    aria-label={`${band.label} Гц`}
                  />
                  <span>{band.label}</span>
                </label>
              );
            })}
          </div>
          <div className="mp-eq-actions">
            <button className="btn text" onClick={player.resetEqualizer}>Сбросить</button>
          </div>
        </div>
      )}
    </div>
  );
}
