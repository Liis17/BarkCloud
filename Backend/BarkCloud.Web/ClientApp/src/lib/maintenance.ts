export type MaintenanceKind = 'update' | 'restart';

export function maintenanceWaitPath(
  kind: MaintenanceKind,
  operationId?: string | null,
  previousStartedAt?: string | null,
): string {
  const params = new URLSearchParams();
  if (operationId) params.set('operationId', operationId);
  if (previousStartedAt) params.set('previousStartedAt', previousStartedAt);

  const path = kind === 'update' ? '/updating' : '/restarting';
  const query = params.toString();
  return query ? `${path}?${query}` : path;
}
