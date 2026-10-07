import { describe, expect, it } from 'vitest';
import { formatJson } from './formatJson';

describe('JSON formatting', () => {
  it('preserves number spelling, duplicate keys and escapes', () => {
    const source = '{"id":9007199254740993123,"id":1e+30,"text":"\\u0041\\n\\\""}';
    const result = formatJson(source);
    expect(result).toEqual({ text: '{\n  "id": 9007199254740993123,\n  "id": 1e+30,\n  "text": "\\u0041\\n\\\""\n}' });
    expect(source).toContain('9007199254740993123');
  });
  it.each(['{"x":}', '{"x":1,}', '// comment\n{}', '', '{}{}'])('rejects invalid strict JSON: %s', (source) => {
    expect(formatJson(source).error).toMatch(/строка \d+, столбец \d+/);
    expect(formatJson(source).text).toBeUndefined();
  });
  it.each(['null', 'true', '123', '"hello"', '[]'])('supports JSON scalar/empty values: %s', (source) => {
    expect(formatJson(source).text).toBe(source);
  });
});
