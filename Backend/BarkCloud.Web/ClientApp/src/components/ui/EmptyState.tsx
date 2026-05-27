import React from 'react';
import { Icon } from '../Icon';

interface EmptyStateProps {
  icon?: string;
  title: React.ReactNode;
  hint?: React.ReactNode;
  action?: React.ReactNode;
}

/** Состояние пустоты списка. */
export function EmptyState({ icon = 'cloud', title, hint, action }: EmptyStateProps) {
  const IconC = Icon[icon] || Icon.cloud;
  return (
    <div className="empty-state">
      <div className="es-icon">
        <IconC size={40} />
      </div>
      <div className="es-title">{title}</div>
      {hint && <div className="es-hint">{hint}</div>}
      {action}
    </div>
  );
}

export function Loading({ label = 'Загрузка…' }: { label?: string }) {
  return (
    <div className="loading">
      <span className="spinner" /> {label}
    </div>
  );
}
