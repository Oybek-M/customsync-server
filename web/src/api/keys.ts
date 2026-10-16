import { api } from './client'
import type { KeyWrapSummary, KeyWrapDetail, CreateWrapRequest, CreateWrapResponse } from './types'

export async function getKeyWraps(): Promise<KeyWrapSummary[]> {
  return api<KeyWrapSummary[]>('/api/v1/keys/wraps')
}

export async function getKeyWrap(wrapId: string): Promise<KeyWrapDetail> {
  return api<KeyWrapDetail>(`/api/v1/keys/wraps/${encodeURIComponent(wrapId)}`)
}

export async function createKeyWrap(payload: CreateWrapRequest): Promise<CreateWrapResponse> {
  return api<CreateWrapResponse>('/api/v1/keys/wraps', {
    method: 'POST',
    body: JSON.stringify(payload),
  })
}

export async function deleteKeyWrap(wrapId: string): Promise<void> {
  return api<void>(`/api/v1/keys/wraps/${encodeURIComponent(wrapId)}`, {
    method: 'DELETE',
  })
}
