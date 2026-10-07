import { applyEdits, format, visit } from 'jsonc-parser';

export interface JsonFormatResult { text?: string; error?: string }

export function formatJson(source: string): JsonFormatResult {
  let error: string | undefined;
  visit(source, { onError: (_code, _offset, _length, line, column) => {
    error ??= `Некорректный JSON: строка ${line + 1}, столбец ${column + 1}`;
  } }, { disallowComments: true, allowTrailingComma: false, allowEmptyContent: false });
  if (error) return { error };
  return { text: applyEdits(source, format(source, undefined, { tabSize: 2, insertSpaces: true, eol: '\n' })) };
}
