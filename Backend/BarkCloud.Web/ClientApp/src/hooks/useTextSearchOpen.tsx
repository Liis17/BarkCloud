import { useNavigate } from 'react-router-dom';
import { useTextFileViewer } from './useTextFileViewer';
import { openSearchHit, type SearchHit } from '../lib/search';
import { textContentUrl } from '../lib/textFiles';
import { apiPost, downloadFile } from '../lib/api';

export function useTextSearchOpen() {
  const navigate = useNavigate();
  const { openTextFile, textViewer } = useTextFileViewer();
  function openSearchResult(hit: SearchHit) {
    openSearchHit(hit, navigate, (file) => openTextFile({ name: file.title, size: file.size,
      contentUrl: textContentUrl({ kind: file.kind === 'sharedFile' ? 'shared' : 'owned', fileId: file.fileId }),
      onDownload: async () => {
        if (file.kind !== 'sharedFile') return downloadFile(file.fileId);
        const result = await apiPost<{ downloadUrl: string }>('/api/shared/download', { fileId: file.fileId });
        if (!result.downloadUrl) throw new Error('Ссылка недоступна');
        window.location.href = result.downloadUrl;
      },
    }));
  }
  return { openSearchResult, textViewer };
}
