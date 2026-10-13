export const profile = {
  initials: 'TU', firstName: 'Test', lastName: 'User', name: 'Test User',
  email: 'test@example.com', username: 'test', bio: '',
  avatarUrl: 'https://files.example/avatar-original', avatarPreviewUrl: 'https://files.example/avatar-preview',
};
export const setting = {
  serviceId: 0, section: 'JwtSettings', key: 'Issuer', value: 'bark',
  isSensitive: false, hasValue: true, isReadOnly: false, valueKind: 'text',
  restartTargets: ['identity'], editedAt: null, editedBy: 'seed', editedFrom: 'seed',
};
export const storageProfile = {
  profileId: 'universal-v1', role: 'universal', version: 1,
  serviceUrl: 'http://minio:9000', hasAccessKey: true, hasSecretKey: true,
  bucketName: 'cloud-universal', isR2: false, isActive: true, isLegacy: false,
  editedAt: null, editedBy: 'seed', editedFrom: 'seed',
};
type StorageGroupFixture = { id: string; name: string; quotaBytes: string; buckets: { serviceUrl: string; bucketName: string }[] };
type StorageBucketFixture = { serviceUrl: string; bucketName: string; roles: string[]; isActive: boolean; isLegacy: boolean };
export const serverSettings = {
  settings: [setting, { ...setting, key: 'Audience', value: 'users' }],
  reservedNames: ['admin'], storageProfiles: [storageProfile], storageRevisions: [],
  storageGroups: [] as StorageGroupFixture[], storageBuckets: [
    { serviceUrl: 'http://minio:9000', bucketName: 'cloud-universal', roles: ['universal'], isActive: true, isLegacy: false },
  ] as StorageBucketFixture[],
};
export const system = { version: '1.0', edition: 'self-host', emailEnabled: true, registrationEnabled: true };
export function jsonResponse(data: unknown, status = 200) {
  return new Response(JSON.stringify(data), { status, headers: { 'Content-Type': 'application/json' } });
}
