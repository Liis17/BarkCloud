import type { Preview } from './types';

export function isSvgFileName(name: string): boolean {
  return /\.svgz?$/i.test(name);
}

export function selectPhotoViewSource(
  name: string,
  jpegViewUrl?: string | null,
  previews?: Preview[],
  originalUrl?: string | null,
): string | null {
  if (jpegViewUrl) return jpegViewUrl;
  if (!isSvgFileName(name)) return originalUrl || null;
  if (previews?.length) {
    const best = [...previews].sort((a, b) => b.w - a.w)[0];
    if (best?.url) return best.url;
  }
  return null;
}
