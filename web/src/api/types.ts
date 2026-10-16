export interface Device {
  device_id: string
  name: string
  platform: string
  role: string
  enrolled_at: string
  last_seen_at: string | null
  last_cursor: number | null
  revoked_at: string | null
}

export interface CreateCodeRequest {
  role?: 'device' | 'admin' | string
}

export interface CreateCodeResponse {
  code: string
}

export interface DeviceHealth {
  device_id: string
  name: string
  platform: string
  revoked: boolean
  reported_at: string | null
  stale: boolean
  rss_bytes: number | null
  memory_limit_bytes: number | null
  cache_db_bytes: number | null
  media_store_bytes: number | null
  media_store_files: number | null
  tdlib_files_bytes: number | null
  tdlib_database_bytes: number | null
  free_disk_bytes: number | null
}

export interface Setting {
  key: string
  value: string
  value_type: 'int' | 'bool' | 'json' | string
  category: string
  description: string
  updated_at: string
}

export interface AuditEvent {
  id: number
  at: string
  action: string
  device_id: string | null
  actor_device_id: string | null
  detail: string | null
}

export interface AuditPage {
  items: AuditEvent[]
  next_before: number | null
}

export interface StorageStats {
  record_count: number
  record_bytes: number
  media_count: number
  media_bytes: number
}

export interface PeerStat {
  peer_hash: string
  record_count: number
  total_bytes: number
  last_occurred_at: number
}

export interface KeyWrapSummary {
  wrap_id: string
  wrap_type: string
  label: string
  created_at: string
  last_used_at: string | null
}

export interface KeyWrapDetail {
  wrap_id: string
  wrap_type: string
  label: string
  salt: string
  nonce: string
  wrapped_key: string
  iterations: number
}

export interface CreateWrapRequest {
  wrap_type: string
  label: string
  salt: string
  nonce: string
  wrapped_key: string
  iterations: number
}

export interface CreateWrapResponse {
  wrap_id: string
}

