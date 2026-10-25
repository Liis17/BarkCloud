import { describe, expect, it } from 'vitest';
import { isSvgFileName, selectPhotoViewSource } from './imagePreviewSource';

describe('selectPhotoViewSource', () => {
  it('uses the original URL for ordinary images when there is no derived view', () => {
    expect(selectPhotoViewSource('photo.jpg', null, [
      { w: 1024, target: 1024, url: '/thumbnail.jpg' },
    ], '/original')).toBe('/original');
  });

  it('uses a raster preview instead of the SVG original', () => {
    expect(selectPhotoViewSource('graphic.svg', null, [
      { w: 128, target: 128, url: '/small.png' },
      { w: 1024, target: 1024, url: '/large.png' },
    ], '/original.svg')).toBe('/large.png');
  });

  it('returns no display or copy source for an SVG without a safe preview', () => {
    expect(selectPhotoViewSource('graphic.svg', null, [], '/original.svg')).toBeNull();
  });

  it('prefers JpegView for SVG and also recognizes SVGZ as an active original format', () => {
    expect(selectPhotoViewSource('graphic.svg', '/view.jpg', [], '/original.svg')).toBe('/view.jpg');
    expect(isSvgFileName('graphic.SVGZ')).toBe(true);
  });
});
