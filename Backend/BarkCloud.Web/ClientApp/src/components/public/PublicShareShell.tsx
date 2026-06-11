import React from 'react';
import { Icon, type IconFn } from '../Icon';

export interface PublicShareShellProps {
  children: React.ReactNode;
  centered?: boolean;
}

export function PublicShareShell({ children, centered }: PublicShareShellProps) {
  return (
    <div className={'public-shell' + (centered ? ' is-centered' : '')}>
      <header className="public-topbar">
        <div className="public-brand">
          <span className="public-brand-mark">
            <Icon.cloud size={20} />
          </span>
          <span>BarkCloud</span>
        </div>
        <a className="public-toplink" href="/login">
          Открыть облако
        </a>
      </header>
      <main className="public-main">{children}</main>
    </div>
  );
}

export interface PublicShareHeaderProps {
  icon: IconFn;
  label: string;
  title: string;
  subtitle?: string;
  meta?: string;
  children?: React.ReactNode;
}

export function PublicShareHeader({ icon: HeaderIcon, label, title, subtitle, meta, children }: PublicShareHeaderProps) {
  return (
    <section className="public-hero">
      <div className="public-hero-icon">
        <HeaderIcon size={28} />
      </div>
      <div className="public-hero-copy">
        <div className="public-label">{label}</div>
        <h1>{title}</h1>
        {subtitle && <p>{subtitle}</p>}
        {meta && <div className="public-meta">{meta}</div>}
      </div>
      {children && <div className="public-hero-actions">{children}</div>}
    </section>
  );
}

export interface PublicStatusProps {
  icon: IconFn;
  title: string;
  text?: string;
  loading?: boolean;
}

export function PublicStatus({ icon: StatusIcon, title, text, loading }: PublicStatusProps) {
  return (
    <PublicShareShell centered>
      <div className="public-status">
        {loading ? <span className="spinner" /> : <StatusIcon size={44} />}
        <h2>{title}</h2>
        {text && <p>{text}</p>}
      </div>
    </PublicShareShell>
  );
}
