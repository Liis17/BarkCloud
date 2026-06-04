import React from 'react';
import { MapContainer, TileLayer, CircleMarker, Tooltip, Popup, useMap, useMapEvents } from 'react-leaflet';
import 'leaflet/dist/leaflet.css';

import { Lightbox } from '../components/media/Lightbox';
import { EmptyState, Loading } from '../components/ui/EmptyState';
import { useToast } from '../hooks/useToast';
import { usePageHeader } from '../hooks/usePageHeader';
import { apiGet } from '../lib/api';
import { plural } from '../lib/format';
import type { CardFile, MapPoint } from '../lib/types';

interface MapResponse {
  points: MapPoint[];
  nextCursorAt: string | null;
  nextCursorId: string | null;
}

const MAX_POINTS = 5000; // потолок выборки точек на клиент (cursor-страницы по 500)

/** Минимальная карточка для Lightbox по точке карты (Lightbox грузит оригинал по id). */
function pointToCard(p: MapPoint): CardFile {
  return {
    id: p.id,
    name: '',
    ext: '',
    kind: p.kind,
    iconKind: p.kind === 'video' ? 'vid' : 'img',
    size: 0,
    sizeLabel: '',
    width: 0,
    height: 0,
    previews: p.previewUrl ? [{ w: 512, target: 512, url: p.previewUrl }] : [],
    createdAt: p.createdAt,
    uploadedAt: null,
  };
}

/** Подгоняет вьюпорт под все точки один раз после загрузки. */
function FitBounds({ points }: { points: MapPoint[] }) {
  const map = useMap();
  React.useEffect(() => {
    if (!points.length) return;
    const lats = points.map((p) => p.lat);
    const lngs = points.map((p) => p.lng);
    map.fitBounds(
      [
        [Math.min(...lats), Math.min(...lngs)],
        [Math.max(...lats), Math.max(...lngs)],
      ],
      { padding: [40, 40], maxZoom: 14 },
    );
  }, [points, map]);
  return null;
}

interface Cell {
  lat: number;
  lng: number;
  items: MapPoint[];
}

/** Клиентская grid-кластеризация: точки в текущих границах группируются по ячейке,
 *  размер которой зависит от зума. Кластеры — оранжевые кружки со счётчиком (клик — приблизить),
 *  одиночные точки — синие маркеры с превью в попапе. */
function Clusters({ points, onPick }: { points: MapPoint[]; onPick: (p: MapPoint) => void }) {
  const map = useMap();
  const [, setTick] = React.useState(0);
  const bump = () => setTick((t) => t + 1);
  useMapEvents({ moveend: bump, zoomend: bump });

  const zoom = map.getZoom();
  const bounds = map.getBounds();
  const cell = 360 / Math.pow(2, zoom) / 6;

  const cells = new Map<string, Cell>();
  for (const p of points) {
    if (!bounds.contains([p.lat, p.lng])) continue;
    const key = Math.round(p.lat / cell) + '_' + Math.round(p.lng / cell);
    let c = cells.get(key);
    if (!c) {
      c = { lat: 0, lng: 0, items: [] };
      cells.set(key, c);
    }
    c.items.push(p);
    c.lat += p.lat;
    c.lng += p.lng;
  }

  return (
    <>
      {[...cells.values()].map((c, i) => {
        const lat = c.lat / c.items.length;
        const lng = c.lng / c.items.length;

        if (c.items.length === 1) {
          const p = c.items[0];
          return (
            <CircleMarker
              key={i}
              center={[lat, lng]}
              radius={7}
              pathOptions={{ color: '#fff', weight: 2, fillColor: '#4F9DDE', fillOpacity: 1 }}
            >
              <Popup>
                <button className="map-popup" onClick={() => onPick(p)}>
                  {p.previewUrl ? <img src={p.previewUrl} alt="" /> : <span>Открыть</span>}
                </button>
              </Popup>
            </CircleMarker>
          );
        }

        const r = Math.min(26, 12 + Math.log2(c.items.length) * 4);
        return (
          <CircleMarker
            key={i}
            center={[lat, lng]}
            radius={r}
            pathOptions={{ color: '#fff', weight: 2, fillColor: '#E0883B', fillOpacity: 0.85 }}
            eventHandlers={{ click: () => map.flyTo([lat, lng], Math.min(zoom + 2, 17)) }}
          >
            <Tooltip permanent direction="center" className="cluster-label">
              {c.items.length}
            </Tooltip>
          </CircleMarker>
        );
      })}
    </>
  );
}

export function MapPage() {
  const [points, setPoints] = React.useState<MapPoint[] | null>(null);
  const [picked, setPicked] = React.useState<MapPoint | null>(null);
  const [toastNode, toast] = useToast();

  React.useEffect(() => {
    let alive = true;
    (async () => {
      const acc: MapPoint[] = [];
      let cursor: { at: string; id: string } | null = null;
      try {
        for (let guard = 0; guard < MAX_POINTS / 500 + 1; guard++) {
          let q = '/api/cloud/map?limit=500';
          if (cursor) q += '&cursorAt=' + encodeURIComponent(cursor.at) + '&cursorId=' + encodeURIComponent(cursor.id);
          const d = await apiGet<MapResponse>(q);
          acc.push(...(d.points || []));
          if (d.nextCursorAt && acc.length < MAX_POINTS) cursor = { at: d.nextCursorAt, id: d.nextCursorId! };
          else break;
        }
        if (alive) setPoints(acc);
      } catch (e) {
        if (alive) {
          toast((e as Error).message, 'err');
          setPoints([]);
        }
      }
    })();
    return () => {
      alive = false;
    };
  }, [toast]);

  usePageHeader(
    () => ({
      title: 'Карта',
      kicker: (
        <>
          <span>Библиотека</span>
          <span className="sep">/</span>
          <span className="cur">Карта</span>
        </>
      ),
    }),
    [],
  );

  return (
    <>
      {toastNode}
      {points === null ? (
        <Loading />
      ) : points.length === 0 ? (
        <EmptyState
          icon="globe"
          title="Нет фотографий с геометками"
          hint="Снимки с GPS-координатами появятся на карте автоматически."
        />
      ) : (
        <div className="map-wrap">
          <div className="map-hint">
            {points.length}
            {points.length >= MAX_POINTS ? '+' : ''} {plural(points.length, 'снимок', 'снимка', 'снимков')} на карте
          </div>
          <MapContainer center={[20, 0]} zoom={2} scrollWheelZoom className="map-canvas">
            <TileLayer
              attribution='&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>'
              url="https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png"
            />
            <FitBounds points={points} />
            <Clusters points={points} onPick={setPicked} />
          </MapContainer>
        </div>
      )}
      {picked && <Lightbox media={pointToCard(picked)} onClose={() => setPicked(null)} />}
    </>
  );
}
