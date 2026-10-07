import React from 'react';
import { EditorState, StateEffect } from '@codemirror/state';
import { EditorView, lineNumbers } from '@codemirror/view';
import { defaultHighlightStyle, syntaxHighlighting } from '@codemirror/language';
import { languages } from '@codemirror/language-data';

export default function TextSource({ text, name, wrap }: { text: string; name: string; wrap: boolean }) {
  const parent = React.useRef<HTMLDivElement>(null);
  React.useEffect(() => {
    let alive = true;
    const view = new EditorView({ parent: parent.current!, state: EditorState.create({ doc: text, extensions: [
      EditorState.readOnly.of(true), EditorView.editable.of(false), lineNumbers(), syntaxHighlighting(defaultHighlightStyle),
      EditorView.contentAttributes.of({ 'aria-label': 'Содержимое файла', tabindex: '0' }),
      ...(wrap ? [EditorView.lineWrapping] : []),
    ] }) });
    const language = languages.find((item) => item.filename?.test(name) || item.extensions.includes(name.slice(name.lastIndexOf('.') + 1).toLowerCase()));
    language?.load().then((support) => { if (alive) view.dispatch({ effects: StateEffect.appendConfig.of(support) }); }).catch(() => {});
    return () => { alive = false; view.destroy(); };
  }, [text, name, wrap]);
  return <div className="text-file-source" ref={parent} />;
}
