import Markdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import remarkMath from 'remark-math';
import rehypeKatex from 'rehype-katex';
import rehypeHighlight from 'rehype-highlight';
import { MermaidDiagram } from './MermaidDiagram';
import 'katex/dist/katex.min.css';

/** Относительные облачные пути пока не резолвятся; разрешены внешние URL и якоря. */
export function markdownUrl(url: string, key: string): string {
  if (key === 'href' && (url.startsWith('#') || /^mailto:/i.test(url))) return url;
  return /^https?:\/\//i.test(url) ? url : '';
}

export default function MarkdownPreview({ text }: { text: string }) {
  return <div className="text-file-markdown"><Markdown
    skipHtml urlTransform={markdownUrl}
    remarkPlugins={[remarkGfm, remarkMath]}
    rehypePlugins={[[rehypeKatex, { trust: false, throwOnError: false }], [rehypeHighlight, { detect: false, plainText: ['mermaid'] }]]}
    components={{
      a: ({ node: _node, ...props }) => <a {...props} target={props.href?.startsWith('#') ? undefined : '_blank'} rel="noopener noreferrer" />,
      img: ({ node: _node, ...props }) => props.src ? <img {...props} loading="lazy" referrerPolicy="no-referrer" /> : <span>{props.alt}</span>,
      pre: ({ node, children, ...props }) => {
        const code = node?.children[0];
        if (code?.type === 'element' && code.tagName === 'code' && Array.isArray(code.properties.className) && code.properties.className.includes('language-mermaid')) {
          const source = code.children.map((child) => child.type === 'text' ? child.value : '').join('');
          return <MermaidDiagram source={source} />;
        }
        return <pre {...props}>{children}</pre>;
      },
    }}
  >{text}</Markdown></div>;
}
