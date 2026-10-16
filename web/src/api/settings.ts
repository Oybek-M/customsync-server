import { api } from './client'
import type { Setting } from './types'

export async function getSettings(signal?: AbortSignal): Promise<Setting[]> {
  return api<Setting[]>('/api/v1/settings', { signal })
}

export async function updateSetting(
  key: string,
  value: string,
  signal?: AbortSignal
): Promise<void> {
  return api<void>(`/api/v1/settings/${encodeURIComponent(key)}`, {
    method: 'PUT',
    body: JSON.stringify({ value }),
    signal,
  })
}
