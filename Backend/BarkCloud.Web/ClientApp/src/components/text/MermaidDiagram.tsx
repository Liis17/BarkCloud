import React from 'react';

let diagramId = 0;
// Mermaid использует общую конфигурацию: сериализуем рендер разных блоков.
let renderQueue: Promise<void> = Promise.resolve();

export function MermaidDiagram({ source }: { source: string }) {
  const parent = React.useRef<HTMLDivElement>(null);
  const [error, setError] = React.useState('');
  const [loading, setLoading] = React.useState(true);
  React.useEffect(() => {
    let alive = true;
    const render = () => {
      setLoading(true); setError('');
      renderQueue = renderQueue.catch(() => {}).then(async () => {
        if (!alive) return;
        try {
          // Файловые директивы и frontmatter не могут менять настройки рендера.
          if (/%%\s*\{|^\s*---/m.test(source)) throw new Error('Настройки Mermaid из файла не поддерживаются');
          if (source.length > 50000) throw new Error('Схема превышает лимит 50 000 символов');
          const { default: mermaid } = await import('mermaid');
          if (!alive) return;
          mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', htmlLabels: false,
            maxTextSize: 50000, maxEdges: 500, theme: document.documentElement.dataset.theme === 'dark' ? 'dark' : 'default',
            secure: ['secure', 'securityLevel', 'startOnLoad', 'maxTextSize', 'maxEdges', 'htmlLabels', 'themeCSS'], suppressErrorRendering: true });
          const { svg } = await mermaid.render(`text-file-diagram-${++diagramId}`, source);
          if (alive && parent.current) { parent.current.innerHTML = svg; setLoading(false); }
        } catch (failure) {
          if (alive) { setError((failure as Error).message || 'Не удалось построить схему'); setLoading(false); }
        }
      });
    };
    render();
    const observer = new MutationObserver(render);
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
    return () => { alive = false; observer.disconnect(); };
  }, [source]);
  return <div className="text-file-diagram">
    {loading && <span role="status">Строим схему…</span>}
    {error && <><div className="text-file-error" role="alert">Не удалось построить схему: {error}</div><pre><code>{source}</code></pre></>}
    <div ref={parent} hidden={!!error || loading} />
  </div>;
}
