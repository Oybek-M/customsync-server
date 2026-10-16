import type {
  Device,
  DeviceHealth,
  Setting,
  AuditPage,
  StorageStats,
  PeerStat,
} from '../api/types'

export const mockDevices: Device[] = [
  {
    device_id: 'dev-own-web',
    name: 'Web Panel (My Browser)',
    platform: 'web',
    role: 'admin',
    enrolled_at: '2026-10-01T10:00:00Z',
    last_seen_at: '2026-10-16T08:00:00Z',
    last_cursor: 1500,
    revoked_at: null,
  },
  {
    device_id: 'dev-desktop-01',
    name: 'Desktop PC',
    platform: 'tdesktop',
    role: 'device',
    enrolled_at: '2026-10-02T11:00:00Z',
    last_seen_at: '2026-10-16T07:45:00Z',
    last_cursor: 1495,
    revoked_at: null,
  },
  {
    device_id: 'dev-phone-old',
    name: 'Old Android Phone',
    platform: 'android',
    role: 'device',
    enrolled_at: '2026-09-15T09:00:00Z',
    last_seen_at: '2026-09-20T12:00:00Z',
    last_cursor: 420,
    revoked_at: '2026-09-21T00:00:00Z',
  },
]

export const mockDeviceHealth: DeviceHealth[] = [
  {
    device_id: 'dev-desktop-01',
    name: 'Desktop PC',
    platform: 'tdesktop',
    revoked: false,
    reported_at: '2026-10-16T07:45:00Z',
    stale: false,
    rss_bytes: 157286400, // 150 MB
    memory_limit_bytes: 1073741824, // 1 GB
    cache_db_bytes: 52428800, // 50 MB
    media_store_bytes: 524288000, // 500 MB
    media_store_files: 120,
    tdlib_files_bytes: 104857600,
    tdlib_database_bytes: 41943040,
    free_disk_bytes: 21474836480, // 20 GB
  },
  {
    device_id: 'dev-phone-old',
    name: 'Old Android Phone',
    platform: 'android',
    revoked: true,
    reported_at: '2026-09-20T12:00:00Z',
    stale: true,
    rss_bytes: 83886080,
    memory_limit_bytes: null,
    cache_db_bytes: 10485760,
    media_store_bytes: 20971520,
    media_store_files: 15,
    tdlib_files_bytes: null,
    tdlib_database_bytes: null,
    free_disk_bytes: 5368709120,
  },
]

export const mockSettings: Setting[] = [
  {
    key: 'auth.enroll_code_minutes',
    value: '15',
    value_type: 'int',
    category: 'auth',
    description: 'Ulanish kodi amal qilish muddati (daqiqa)',
    updated_at: '2026-10-01T00:00:00Z',
  },
  {
    key: 'storage.quota_total_mb',
    value: '10240',
    value_type: 'int',
    category: 'storage',
    description: 'Serverdagi umumiy media kvotasi (MB)',
    updated_at: '2026-10-01T00:00:00Z',
  },
  {
    key: 'storage.quota_per_device_mb',
    value: '2048',
    value_type: 'int',
    category: 'storage',
    description: 'Har bir qurilma uchun media kvotasi (MB)',
    updated_at: '2026-10-01T00:00:00Z',
  },
  {
    key: 'sync.allow_registration',
    value: 'true',
    value_type: 'bool',
    category: 'sync',
    description: 'Yangi qurilmalarni ro\'yxatdan o\'tkazishga ruxsat',
    updated_at: '2026-10-01T00:00:00Z',
  },
  {
    key: 'sync.custom_rules',
    value: '{"auto_download": true, "max_mb": 50}',
    value_type: 'json',
    category: 'sync',
    description: 'Sinxronizatsiya maxsus qoidalari',
    updated_at: '2026-10-01T00:00:00Z',
  },
]

export const mockAuditPage1: AuditPage = {
  items: [
    {
      id: 105,
      at: '2026-10-16T08:00:00Z',
      action: 'device.enrolled',
      device_id: 'dev-desktop-01',
      actor_device_id: 'dev-own-web',
      detail: JSON.stringify({ name: 'Desktop PC', platform: 'tdesktop' }),
    },
    {
      id: 104,
      at: '2026-10-16T07:30:00Z',
      action: 'settings.changed',
      device_id: null,
      actor_device_id: 'dev-own-web',
      detail: '{"key":"auth.enroll_code_minutes","oldValue":"10","newValue":"15"}',
    },
    {
      id: 103,
      at: '2026-10-16T07:00:00Z',
      action: 'device.revoked',
      device_id: 'dev-phone-old',
      actor_device_id: 'dev-own-web',
      detail: 'Plain text unparsed detail or <script>alert("xss")</script>',
    },
  ],
  next_before: 103,
}

export const mockAuditPage2: AuditPage = {
  items: [
    {
      id: 102,
      at: '2026-10-15T18:00:00Z',
      action: 'device.enrolled',
      device_id: 'dev-phone-old',
      actor_device_id: null,
      detail: null,
    },
  ],
  next_before: null,
}

export const mockStorageStats: StorageStats = {
  record_count: 5420,
  record_bytes: 14500000, // ~13.8 MB
  media_count: 850,
  media_bytes: 450000000, // ~429.1 MB
}

export const mockPeerStats: PeerStat[] = [
  {
    peer_hash: 'a1b2c3d4e5f6789012345678abcdef01',
    record_count: 1250,
    total_bytes: 180000000,
    last_occurred_at: 1729065600, // seconds (< 1e12)
  },
  {
    peer_hash: 'f0e1d2c3b4a56789abcdef0123456789',
    record_count: 980,
    total_bytes: 120000000,
    last_occurred_at: 1729065600000, // ms (>= 1e12)
  },
]
