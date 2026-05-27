import { useShell } from '../../hooks/useShell';

export function Footbar() {
  const sync = useShell()?.sync;
  return (
    <footer className="footbar">
      <div className="left">
        <span className="pulse">
          <span className="dot" />
          {sync?.status || 'Синхронизировано'}
        </span>
        <span>Последняя синхронизация · {sync?.lastAt}</span>
      </div>
      <div className="right">
        <a href="#">Документация</a>
        <a href="#">Статус</a>
        <a href="#">Горячие клавиши</a>
        <span>AES-256 · Zero-knowledge</span>
      </div>
    </footer>
  );
}
