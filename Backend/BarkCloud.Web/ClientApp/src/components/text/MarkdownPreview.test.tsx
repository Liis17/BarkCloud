import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import MarkdownPreview, { markdownUrl } from './MarkdownPreview';
import { MermaidDiagram } from './MermaidDiagram';

const mermaid = vi.hoisted(() => ({ initialize: vi.fn(), render: vi.fn(async () => ({ svg: '<svg aria-label="diagram"></svg>' })) }));
vi.mock('mermaid', () => ({ default: mermaid }));
afterEach(() => { cleanup(); vi.clearAllMocks(); });

describe('MarkdownPreview', () => {
  it('renders GFM, highlighted code, math and Mermaid', async () => {
    render(<MarkdownPreview text={'# Title\n\n| A | B |\n| - | - |\n| 1 | 2 |\n\n- [x] Done\n\n```js\nconst n = 1;\n```\n\n$x^2$\n\n```mermaid\ngraph TD; A-->B;\n```'} />);
    expect(screen.getByRole('heading', { name: 'Title' })).toBeTruthy();
    expect(screen.getByRole('table')).toBeTruthy();
    expect((screen.getByRole('checkbox') as HTMLInputElement).disabled).toBe(true);
    expect(document.querySelector('.hljs-keyword')?.textContent).toBe('const');
    expect(document.querySelector('.katex')).toBeTruthy();
    await waitFor(() => expect(document.querySelector('svg[aria-label="diagram"]')).toBeTruthy());
    expect(mermaid.initialize).toHaveBeenCalledWith(expect.objectContaining({ securityLevel: 'strict', htmlLabels: false }));
  });
  it('does not execute HTML, unsafe links or resolve relative images', () => {
    render(<MarkdownPreview text={'<script>alert(1)</script>\n\n<img src=x onerror=alert(1)>\n\n[unsafe](javascript:alert(1))\n\n![relative](./pic.png)\n\n![external](https://example.com/pic.png)\n\n$\\href{javascript:alert(1)}{bad}$'} />);
    expect(document.querySelector('script, [onerror]')).toBeNull();
    expect(screen.getByText('unsafe').getAttribute('href')).not.toMatch(/javascript/);
    expect(screen.getByText('relative')).toBeTruthy();
    expect(screen.getByAltText('external').getAttribute('src')).toBe('https://example.com/pic.png');
    expect(document.querySelector('a[href^="javascript:"]')).toBeNull();
    expect(markdownUrl('/api/private', 'href')).toBe('');
  });
  it('unknown code languages and invalid formulas preserve their source', () => {
    render(<MarkdownPreview text={'```unknownlang\nunknown source\n```\n\n$\\frac{1}{$'} />);
    expect(screen.getByText('unknown source')).toBeTruthy();
    expect(document.querySelector('.katex-error')?.textContent).toContain('\\frac{1}{');
  });
  it('rejects Mermaid configuration from the file and shows the original block', async () => {
    render(<MermaidDiagram source={'%%{init: {"securityLevel":"loose"}}%%\ngraph TD; A-->B;'} />);
    expect(await screen.findByRole('alert')).toBeTruthy();
    expect(screen.getByText(/%%\{init:/)).toBeTruthy();
    expect(mermaid.render).not.toHaveBeenCalled();
  });
  it('isolates a failed diagram from the rest of the document', async () => {
    mermaid.render.mockRejectedValueOnce(new Error('Bad diagram'));
    render(<MarkdownPreview text={'# Still readable\n\n```mermaid\nbroken diagram\n```'} />);
    expect(screen.getByRole('heading')).toHaveProperty('textContent', 'Still readable');
    expect(await screen.findByRole('alert')).toHaveProperty('textContent', expect.stringContaining('Bad diagram'));
    expect(screen.getByText('broken diagram')).toBeTruthy();
  });
  it('preserves the source of an oversized diagram', async () => {
    const source = 'graph TD; A-->B;\n' + ' '.repeat(50000);
    render(<MermaidDiagram source={source} />);
    expect(await screen.findByRole('alert')).toHaveProperty('textContent', expect.stringContaining('50 000'));
    expect(document.querySelector('pre code')?.textContent).toBe(source);
    expect(mermaid.render).not.toHaveBeenCalled();
  });
});
