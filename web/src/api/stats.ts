import { api } from './client'
import type { StorageStats, PeerStat } from './types'

export async function getStorageStats(signal?: AbortSignal): Promise<StorageStats> {
  return api<StorageStats>('/api/v1/stats/storage', { signal })
}

export async function getPeerStats(
  sort: 'bytes' | 'count' | 'recent' = 'bytes',
  limit = 10,
  signal?: AbortSignal
): Promise<PeerStat[]> {
  const params = new URLSearchParams()
  params.set('sort', sort)
  params.set('limit', limit.toString())
  return api<PeerStat[]>(`/api/v1/stats/peers?${params.toString()}`, { signal })
}
