import L from 'leaflet';

export interface OutletLocation {
  outletId: string;
  label: string;
  latitude: number;
  longitude: number;
}

/** Create map markers using each outlet's latitude and longitude. */
export function createOutletMarkers(outlets: OutletLocation[]): L.Marker[] {
  return outlets
    .filter((outlet) =>
      Number.isFinite(outlet.latitude) &&
      Number.isFinite(outlet.longitude) &&
      Math.abs(outlet.latitude) <= 90 &&
      Math.abs(outlet.longitude) <= 180
    )
    .map((outlet) =>
      L.marker([outlet.latitude, outlet.longitude]).bindPopup(outlet.label)
    );
}

export function fitMapToOutlets(map: L.Map, markers: L.Marker[]): void {
  if (markers.length === 0) return;
  const points = markers.map((marker) => marker.getLatLng());
  map.fitBounds(L.latLngBounds(points), { padding: [18, 18] });
}
