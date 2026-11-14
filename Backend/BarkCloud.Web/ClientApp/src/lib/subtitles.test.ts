import { describe, expect, it } from 'vitest';
import { decodeSubtitleBytes, toWebVtt } from './subtitles';

describe('toWebVtt', () => {
  it('converts SRT timings and line endings', () => {
    const srt = '﻿1\r\n0:00:01,500 --> 00:00:03,000\r\nПривет, 00:00:05,000\r\n';
    expect(toWebVtt(srt)).toBe('WEBVTT\n\n1\n00:00:01.500 --> 00:00:03.000\nПривет, 00:00:05,000\n');
  });

  it('keeps WebVTT as is', () => {
    const vtt = 'WEBVTT\n\n00:01.000 --> 00:02.000\nHi\n';
    expect(toWebVtt(vtt)).toBe(vtt);
  });
});

describe('decodeSubtitleBytes', () => {
  it('reads UTF-8 and falls back to Windows-1251', () => {
    expect(decodeSubtitleBytes(new TextEncoder().encode('Привет').buffer as ArrayBuffer)).toBe('Привет');
    expect(decodeSubtitleBytes(new Uint8Array([0xcf, 0xf0, 0xe8, 0xe2, 0xe5, 0xf2]).buffer)).toBe('Привет');
  });
});
