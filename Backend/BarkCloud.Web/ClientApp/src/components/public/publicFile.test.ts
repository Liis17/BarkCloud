import { describe, expect, it } from 'vitest';
import { countLabel, describePublicFile, fmtSize, isPublicMedia } from './publicFile';

describe('describePublicFile', () => {
  it('фото: чип «Изображение · EXT», тон tertiary', () => {
    expect(describePublicFile('Отпуск на Севере.heic', 'photo')).toEqual({ ext: 'HEIC', typeLabel: 'Изображение · HEIC', tone: 'tertiary' });
  });

  it('видео: тон primary', () => {
    expect(describePublicFile('Поход.mp4', 'video')).toEqual({ ext: 'MP4', typeLabel: 'Видео · MP4', tone: 'primary' });
  });

  it('аудио: «Аудиотрек», тон tertiary', () => {
    expect(describePublicFile('Тихий берег.mp3', 'audio')).toMatchObject({ typeLabel: 'Аудиотрек · MP3', tone: 'tertiary' });
  });

  it('PDF и офисные форматы — «Документ», тон secondary', () => {
    expect(describePublicFile('Смета.pdf', 'other')).toEqual({ ext: 'PDF', typeLabel: 'Документ · PDF', tone: 'secondary' });
    expect(describePublicFile('Отчёт.DOCX', 'other')).toMatchObject({ ext: 'DOCX', typeLabel: 'Документ · DOCX' });
  });

  it('неизвестное расширение — «Файл»', () => {
    expect(describePublicFile('архив.zip', 'other')).toMatchObject({ ext: 'ZIP', typeLabel: 'Файл · ZIP' });
  });

  it('без расширения и со слишком длинным расширением бейдж не показывается', () => {
    expect(describePublicFile('README', 'other')).toEqual({ ext: '', typeLabel: 'Файл', tone: 'secondary' });
    expect(describePublicFile('имя.слишкомдлинное', 'other')).toMatchObject({ ext: '', typeLabel: 'Файл' });
    expect(describePublicFile('.gitignore', 'other')).toMatchObject({ ext: '' });
  });
});

describe('fmtSize', () => {
  it('форматирует с русской запятой', () => {
    expect(fmtSize(3984588)).toBe('3,8 МБ');
    expect(fmtSize(884736)).toBe('864 КБ');
    expect(fmtSize(512)).toBe('512 Б');
  });

  it('нулевой размер — пустая строка', () => {
    expect(fmtSize(0)).toBe('');
  });
});

describe('isPublicMedia / countLabel', () => {
  it('медиа — только фото и видео', () => {
    expect(isPublicMedia('photo')).toBe(true);
    expect(isPublicMedia('video')).toBe(true);
    expect(isPublicMedia('audio')).toBe(false);
    expect(isPublicMedia('other')).toBe(false);
  });

  it('склоняет числительные', () => {
    expect(countLabel(1, 'папка', 'папки', 'папок')).toBe('1 папка');
    expect(countLabel(4, 'файл', 'файла', 'файлов')).toBe('4 файла');
    expect(countLabel(6, 'элемент', 'элемента', 'элементов')).toBe('6 элементов');
    expect(countLabel(11, 'трек', 'трека', 'треков')).toBe('11 треков');
  });
});
