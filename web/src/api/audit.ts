import { api } from './client'
import type { AuditPage } from './types'

export async function getAudit(
  limit?: number,
  before?: number | null,
  signal?: AbortSignal
): Promise<AuditPage> {
  const params = new URLSearchParams()
  if (limit !== undefined && limit !== null) {
    params.set('limit', limit.toString())
  }
  if (before !== undefined && before !== null) {
    params.set('before', before.toString())
  }
  const query = params.toString() ? `?${params.toString()}` : ''
  return api<AuditPage>(`/api/v1/audit${query}`, { signal })
}
