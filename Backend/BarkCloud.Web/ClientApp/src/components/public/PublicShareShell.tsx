import React from 'react';
import { Icon, type IconFn } from '../Icon';
import type { PublicTone } from './publicFile';

export interface PublicShareShellProps {
  children: React.ReactNode;
  centered?: boolean;
  /** Узкая колонка (аудио, плейлист) вместо 1200px. */
  narrow?: boolean;
  /** Скрыть «Открыть облако» в шапке (страница «Ссылка недоступна»). */
  hideTopLink?: boolean;
}

export function PublicShareShell({ children, centered, narrow, hideTopLink }: PublicShareShellProps) {
  return (
    <div className={'public-shell' + (centered ? ' is-centered' : '')}>
      <header className="public-topbar">
        <div className="public-brand">
          <span className="public-brand-mark">
            <Icon.cloud size={22} />
          </span>
          <span>BarkCloud</span>
        </div>
        {!hideTopLink && (
          <a className="public-toplink public-st" href="/login">
            Открыть облако
            <Icon.external size={18} />
          </a>
        )}
      </header>
      <main className={'public-main' + (narrow ? ' is-narrow' : '')}>{children}</main>
    </div>
  );
}

export function PublicChips({ chips }: { chips: (string | undefined | false)[] }) {
  const items = chips.filter((c): c is string => !!c);
  if (items.length === 0) return null;
  return (
    <div className="public-chips">
      {items.map((c) => (
        <span key={c} className="public-chip">
          {c}
        </span>
      ))}
    </div>
  );
}

export interface PublicShareHeaderProps {
  /** file — плитка-иконка + действия справа; folder — карточка; collection — большая обложка (альбом, плейлист). */
  variant: 'file' | 'folder' | 'collection';
  icon: IconFn;
  tone?: PublicTone;
  label: string;
  /** Обложка коллекции (если задана — вместо иконки). */
  coverUrl?: string;
  title: string;
  subtitle?: string;
  chips?: (string | undefined | false)[];
  /** Действия справа (только variant="file"). */
  children?: React.ReactNode;
}

export function PublicShareHeader({
  variant,
  icon: HeaderIcon,
  tone = 'primary',
  label,
  coverUrl,
  title,
  subtitle,
  chips,
  children,
}: PublicShareHeaderProps) {
  const media =
    variant === 'collection' ? (
      <div className="public-cover public-ph">
        {coverUrl ? (
          <img src={coverUrl} alt="" />
        ) : (
          <span className="public-cover-icon">
            <HeaderIcon size={30} />
          </span>
        )}
      </div>
    ) : (
      <div className={'public-hero-tile tone-' + (variant === 'folder' ? 'primary' : tone)}>
        <HeaderIcon size={variant === 'folder' ? 40 : 32} />
      </div>
    );
  return (
    <section className={'public-hero is-' + variant}>
      <div className="public-hero-main">
        {media}
        <div className="public-hero-copy">
          <div className="public-label">{label}</div>
          <h1>{title}</h1>
          {subtitle && <p>{subtitle}</p>}
          {chips && <PublicChips chips={chips} />}
        </div>
      </div>
      {children && <div className="public-hero-actions">{children}</div>}
    </section>
  );
}

export interface PublicStatusProps {
  title: string;
  text?: string;
  loading?: boolean;
}

export function PublicStatus({ title, text, loading }: PublicStatusProps) {
  if (loading) {
    return (
      <PublicShareShell centered>
        <div className="public-loading" role="status">
          <div className="public-loading-tile">
            <svg className="public-spin" viewBox="0 0 48 48" aria-hidden="true">
              <circle cx="24" cy="24" r="19" className="public-spin-track" />
              <circle cx="24" cy="24" r="19" strokeDasharray="40 200" className="public-spin-arc" />
            </svg>
          </div>
          <div>
            <h1>{title}</h1>
            <p>Публичная ссылка BarkCloud</p>
          </div>
        </div>
      </PublicShareShell>
    );
  }
  return (
    <PublicShareShell centered hideTopLink>
      <section className="public-status">
        <div className="public-status-tile">
          <Icon.linkOff size={40} />
        </div>
        <div>
          <h1>{title}</h1>
          {text && <p>{text}</p>}
        </div>
        <a className="public-pill public-st" href="/login">
          Открыть облако
        </a>
      </section>
    </PublicShareShell>
  );
}
