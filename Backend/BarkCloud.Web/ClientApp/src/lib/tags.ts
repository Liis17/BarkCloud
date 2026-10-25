export interface UserTag {
  id: string;
  name: string;
  color: string;
}

export const tagKey = (name: string) => name.normalize('NFKC').trim().replace(/\s+/g, ' ').toLowerCase();
export const tagStyle = (color = '#6750A4') => ({ '--tag-color': color } as import('react').CSSProperties);
