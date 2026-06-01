import React from 'react';
import { Modal } from './Modal';
import { Icon } from '../Icon';
import { apiGet, apiPost } from '../../lib/api';
import type { ToastPush } from '../../hooks/useToast';

interface SearchUser {
  id: number;
  username: string;
  firstName: string;
  lastName: string;
  avatar: string;
}

const inputStyle: React.CSSProperties = {
  width: '100%',
  padding: '10px 12px',
  borderRadius: 8,
  border: '1px solid var(--md-outline, #444)',
  background: 'var(--md-surface-container-high, #25252e)',
  color: 'inherit',
  fontSize: 14,
};
const hint: React.CSSProperties = { color: 'var(--md-on-surface-variant)', fontSize: 13, margin: '10px 2px 0' };

/** Модалка «Поделиться с пользователем»: поиск получателей и выдача гранта доступа. */
export function ShareWithUserModal({
  fileId,
  fileName,
  onClose,
  toast,
}: {
  fileId: string;
  fileName: string;
  onClose: () => void;
  toast: ToastPush;
}) {
  const [q, setQ] = React.useState('');
  const [users, setUsers] = React.useState<SearchUser[]>([]);
  const [loading, setLoading] = React.useState(false);
  const [shared, setShared] = React.useState<Set<number>>(new Set());

  React.useEffect(() => {
    const query = q.trim();
    if (query.length < 2) {
      setUsers([]);
      return;
    }
    let alive = true;
    setLoading(true);
    const t = setTimeout(() => {
      apiGet<{ users: SearchUser[] }>('/api/shared/users/search?q=' + encodeURIComponent(query))
        .then((d) => alive && setUsers(d.users || []))
        .catch(() => alive && setUsers([]))
        .finally(() => alive && setLoading(false));
    }, 300);
    return () => {
      alive = false;
      clearTimeout(t);
    };
  }, [q]);

  async function share(u: SearchUser) {
    try {
      await apiPost('/api/shared/grant', { fileId, recipientUserId: u.id });
      setShared((prev) => new Set(prev).add(u.id));
      toast(`Доступ выдан: ${u.username || u.firstName || 'пользователь'}`);
    } catch (e) {
      toast((e as Error).message, 'err');
    }
  }

  function displayName(u: SearchUser) {
    return [u.firstName, u.lastName].filter(Boolean).join(' ') || u.username;
  }

  return (
    <Modal
      title={`Поделиться «${fileName}»`}
      onClose={onClose}
      actions={
        <button className="btn primary" onClick={onClose}>
          Готово
        </button>
      }
    >
      <input
        style={inputStyle}
        autoFocus
        placeholder="Поиск по имени или юзернейму…"
        value={q}
        onChange={(e) => setQ(e.target.value)}
      />
      {q.trim().length < 2 ? (
        <p style={hint}>Введите минимум 2 символа.</p>
      ) : loading ? (
        <p style={hint}>Поиск…</p>
      ) : users.length === 0 ? (
        <p style={hint}>Никого не найдено.</p>
      ) : (
        <ul style={{ listStyle: 'none', padding: 0, margin: '10px 0 0' }}>
          {users.map((u) => (
            <li key={u.id} style={{ display: 'flex', alignItems: 'center', gap: 10, padding: '8px 0' }}>
              <div
                style={{
                  width: 34,
                  height: 34,
                  borderRadius: '50%',
                  overflow: 'hidden',
                  background: 'var(--md-surface-container-high)',
                  display: 'flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                  flexShrink: 0,
                }}
              >
                {u.avatar ? (
                  <img src={u.avatar} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover' }} />
                ) : (
                  <Icon.user size={18} />
                )}
              </div>
              <div style={{ flex: 1, minWidth: 0 }}>
                <div style={{ fontWeight: 500 }}>{displayName(u)}</div>
                {u.username && <div style={{ fontSize: 12, color: 'var(--md-on-surface-variant)' }}>@{u.username}</div>}
              </div>
              {shared.has(u.id) ? (
                <span style={{ display: 'inline-flex', alignItems: 'center', gap: 4, color: 'var(--md-primary, #7aa2ff)' }}>
                  <Icon.check size={16} /> Выдан
                </span>
              ) : (
                <button className="btn text" onClick={() => share(u)}>
                  Поделиться
                </button>
              )}
            </li>
          ))}
        </ul>
      )}
    </Modal>
  );
}
