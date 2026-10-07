import React from 'react';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { TextFileViewer, type TextPreviewFile } from './TextFileViewer';
import { Modal } from '../ui/Modal';
import { MAX_TEXT_FILE_BYTES } from '../../lib/textFiles';
import { formatJson } from '../../lib/formatJson';

vi.mock('./TextSource', () => ({ default: ({ text, wrap }: { text: string; wrap: boolean }) => <pre data-wrap={wrap} aria-label="Содержимое файла">{text}</pre> }));
vi.mock('./MarkdownPreview', () => ({ default: ({ text }: { text: string }) => <article>{text}</article> }));

const file: TextPreviewFile = { name: 'note.txt', contentUrl: '/api/files/text?id=f', onDownload: vi.fn() };
const response = (text: string) => ({ ok: true, arrayBuffer: async () => new TextEncoder().encode(text).buffer });
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

describe('TextFileViewer', () => {
  it('shows source, wrap toggle and original download', async () => {
    const download = vi.fn();
    vi.stubGlobal('fetch', vi.fn(async () => response('Hello <script>safe</script>')));
    render(<TextFileViewer file={{ ...file, onDownload: download }} onClose={() => {}} />);
    expect(await screen.findByLabelText('Содержимое файла')).toHaveProperty('textContent', 'Hello <script>safe</script>');
    fireEvent.click(screen.getByText('Перенос строк'));
    expect(screen.getByLabelText('Содержимое файла').dataset.wrap).toBe('true');
    fireEvent.click(screen.getByText('Скачать'));
    expect(download).toHaveBeenCalledOnce();
    expect(document.querySelector('script')).toBeNull();
  });
  it('opens Markdown in rendered mode and switches to source', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => response('# Title')));
    render(<TextFileViewer file={{ ...file, name: 'note.md' }} onClose={() => {}} />);
    expect(await screen.findByText('# Title')).toHaveProperty('tagName', 'ARTICLE');
    fireEvent.click(screen.getByText('Исходник'));
    expect(await screen.findByLabelText('Содержимое файла')).toHaveProperty('textContent', '# Title');
  });
  it('formats JSON in a worker and restores exact source', async () => {
    const terminate = vi.fn();
    class FormatWorker {
      onmessage?: (event: { data: unknown }) => void;
      terminate = terminate;
      postMessage(source: string) { this.onmessage?.({ data: formatJson(source) }); }
    }
    vi.stubGlobal('Worker', FormatWorker);
    vi.stubGlobal('fetch', vi.fn(async () => response('{"n":9007199254740993123}')));
    render(<TextFileViewer file={{ ...file, name: 'data.json' }} onClose={() => {}} />);
    await screen.findByLabelText('Содержимое файла');
    fireEvent.click(screen.getByText('Форматировать'));
    expect(screen.getByLabelText('Содержимое файла').textContent).toBe('{\n  "n": 9007199254740993123\n}');
    fireEvent.click(screen.getByText('Исходник'));
    expect(screen.getByLabelText('Содержимое файла').textContent).toBe('{"n":9007199254740993123}');
    expect(terminate).toHaveBeenCalled();
  });
  it('keeps source on JSON error and terminates a running worker on close', async () => {
    const instances: { onmessage?: (event: { data: unknown }) => void; terminate: ReturnType<typeof vi.fn> }[] = [];
    class FormatWorker {
      onmessage?: (event: { data: unknown }) => void;
      terminate = vi.fn();
      constructor() { instances.push(this); }
      postMessage() {}
    }
    vi.stubGlobal('Worker', FormatWorker);
    vi.stubGlobal('fetch', vi.fn(async () => response('{broken')));
    const view = render(<TextFileViewer file={{ ...file, name: 'data.json' }} onClose={() => {}} />);
    await screen.findByLabelText('Содержимое файла');
    fireEvent.click(screen.getByText('Форматировать'));
    act(() => instances[0].onmessage?.({ data: formatJson('{broken') }));
    await screen.findByRole('alert');
    expect(screen.getByLabelText('Содержимое файла').textContent).toBe('{broken');
    fireEvent.click(screen.getByText('Форматировать'));
    view.unmount();
    expect(instances[1].terminate).toHaveBeenCalledOnce();
  });
  it('shows empty files and rejects oversized files without requesting bytes', async () => {
    const fetch = vi.fn(async () => response(''));
    vi.stubGlobal('fetch', fetch);
    const view = render(<TextFileViewer file={file} onClose={() => {}} />);
    expect(await screen.findByText('Файл пуст')).toBeTruthy();
    view.rerender(<TextFileViewer file={{ ...file, size: MAX_TEXT_FILE_BYTES + 1 }} onClose={() => {}} />);
    expect(await screen.findByRole('alert')).toHaveProperty('textContent', expect.stringContaining('больше 10 МиБ'));
    expect(fetch).toHaveBeenCalledOnce();
  });
  it('shows server access errors and still offers download', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: false, json: async () => ({ error: 'Нет доступа к файлу' }) })));
    render(<TextFileViewer file={file} onClose={() => {}} />);
    expect(await screen.findByText('Нет доступа к файлу')).toBeTruthy();
    expect(screen.getByText('Скачать')).toBeTruthy();
  });
  it('aborts old loads and ignores stale content when switching files', async () => {
    let resolveOld!: (value: unknown) => void;
    const fetch = vi.fn((url: string, _options: RequestInit) => url === file.contentUrl ? new Promise((resolve) => { resolveOld = resolve; }) : Promise.resolve(response('New')));
    vi.stubGlobal('fetch', fetch);
    const view = render(<TextFileViewer file={file} onClose={() => {}} />);
    view.rerender(<TextFileViewer file={{ ...file, contentUrl: '/new', name: 'new.txt' }} onClose={() => {}} />);
    expect(fetch.mock.calls[0][1].signal?.aborted).toBe(true);
    expect(await screen.findByLabelText('Содержимое файла')).toHaveProperty('textContent', 'New');
    resolveOld(response('Old'));
    await waitFor(() => expect(screen.getByLabelText('Содержимое файла').textContent).toBe('New'));
    view.unmount();
    expect(fetch.mock.calls[1][1].signal?.aborted).toBe(true);
  });
  it('Escape closes only the upper dialog and restores focus to its opener', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => response('')));
    const parentClose = vi.fn();
    function Nested() {
      const [opened, setOpened] = React.useState(false);
      return <Modal title="Папка" onClose={parentClose}><button onClick={() => setOpened(true)}>Открыть текст</button>
        {opened && <TextFileViewer file={file} onClose={() => setOpened(false)} />}</Modal>;
    }
    render(<Nested />);
    const open = screen.getByText('Открыть текст');
    open.focus(); fireEvent.click(open);
    await screen.findByText('Файл пуст');
    fireEvent.keyDown(window, { key: 'Escape' });
    expect(parentClose).not.toHaveBeenCalled();
    expect(screen.getAllByRole('dialog')).toHaveLength(1);
    expect(document.activeElement).toBe(open);
    fireEvent.keyDown(window, { key: 'Escape' });
    expect(parentClose).toHaveBeenCalledOnce();
  });
});
