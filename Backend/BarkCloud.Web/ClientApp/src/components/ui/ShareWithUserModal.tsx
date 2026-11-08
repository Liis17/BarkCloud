import React from 'react';
import { Modal } from './Modal';
import { SearchField } from './TextField';
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

interface PlaylistGrant {
  grantId: string;
  user: SearchUser;
}

const hint: React.CSSProperties = { color: 'var(--md-on-surface-variant)', fontSize: 13, margin: '10px 2px 0' };

/**
 * Модалка «Поделиться с пользователем»: поиск получателей и выдача гранта доступа.
 * Если передан folderId — выдаётся грант на папку (рекурсивно), playlistId — на плейлист, иначе на файл.
 * Для плейлиста дополнительно показывается, у кого уже есть доступ, с возможностью отозвать его.
 */
export function ShareWithUserModal({
  fileId,
  folderId,
  playlistId,
  fileName,
  onClose,
  toast,
}: {
  fileId?: string;
  folderId?: string;
  playlistId?: string;
  fileName: string;
  onClose: () => void;
  toast: ToastPush;
}) {
  const [q, setQ] = React.useState('');
  const [users, setUsers] = React.useState<SearchUser[]>([]);
  const [loading, setLoading] = React.useState(false);
  const [shared, setShared] = React.useState<Set<number>>(new Set());
  const [grants, setGrants] = React.useState<PlaylistGrant[] | null>(null);
  const [revoking, setRevoking] = React.useState<string | null>(null);

  const loadGrants = React.useCallback(() => {
    if (!playlistId) return;
    apiGet<{ items: PlaylistGrant[] }>('/api/music/shared/outgoing?playlistId=' + encodeURIComponent(playlistId))
      .then((d) => setGrants(d.items || []))
      .catch((e) => {
        setGrants([]);
        toast((e as Error).message, 'err');
      });
  }, [playlistId, toast]);

  React.useEffect(loadGrants, [loadGrants]);

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
      if (folderId) await apiPost('/api/shared/grant-folder', { directoryId: folderId, recipientUserId: u.id });
      else if (playlistId) await apiPost('/api/music/shared/grant', { playlistId, recipientUserId: u.id });
      else await apiPost('/api/shared/grant', { fileId, recipientUserId: u.id });
      setShared((prev) => new Set(prev).add(u.id));
      toast(`Доступ выдан: ${u.username || u.firstName || 'пользователь'}`);
      loadGrants();
    } catch (e) {
      toast((e as Error).message, 'err');
    }
  }

  async function revoke(g: PlaylistGrant) {
    setRevoking(g.grantId);
    try {
      await apiPost('/api/music/shared/revoke-grant', { grantId: g.grantId });
      setGrants((prev) => prev && prev.filter((x) => x.grantId !== g.grantId));
      setShared((prev) => {
        const next = new Set(prev);
        next.delete(g.user.id);
        return next;
      });
      toast(`Доступ отозван: ${g.user.username || g.user.firstName || 'пользователь'}`);
    } catch (e) {
      toast((e as Error).message, 'err');
    } finally {
      setRevoking(null);
    }
  }

  function displayName(u: SearchUser) {
    return [u.firstName, u.lastName].filter(Boolean).join(' ') || u.username || `Пользователь #${u.id}`;
  }

  function userInfo(u: SearchUser) {
    return (
      <>
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
      </>
    );
  }

  const grantedIds = new Set(grants?.map((g) => g.user.id));

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
      <SearchField
        label="Поиск по имени или юзернейму"
        autoFocus
        placeholder="Поиск по имени или юзернейму…"
        value={q}
        onChange={(e) => setQ(e.target.value)}
        leading={<Icon.search size={18} />}
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
              {userInfo(u)}
              {shared.has(u.id) || grantedIds.has(u.id) ? (
                <span style={{ display: 'inline-flex', alignItems: 'center', gap: 4, color: 'var(--md-primary)' }}>
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
      {playlistId && (
        <>
          <div style={{ fontWeight: 500, margin: '18px 2px 0' }}>Доступ есть у</div>
          {grants === null ? (
            <p style={hint}>Загрузка…</p>
          ) : grants.length === 0 ? (
            <p style={hint}>Плейлист пока никому не выдан.</p>
          ) : (
            <ul style={{ listStyle: 'none', padding: 0, margin: '6px 0 0' }}>
              {grants.map((g) => (
                <li key={g.grantId} style={{ display: 'flex', alignItems: 'center', gap: 10, padding: '8px 0' }}>
                  {userInfo(g.user)}
                  <button className="btn text" disabled={revoking === g.grantId} onClick={() => revoke(g)}>
                    Отозвать
                  </button>
                </li>
              ))}
            </ul>
          )}
        </>
      )}
    </Modal>
  );
}
