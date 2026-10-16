import { api } from '../api/client'
import {
  getCursor,
  putRecordsBatch,
  setCursor,
  ensureDevice,
  type EncryptedRecord,
} from './schema'
import { useAuthStore } from '../stores/auth'
import { useVaultStore } from '../stores/vault'

export interface SyncProgress {
  fetched: number
  cursor: number
  hasMore: boolean
}

export interface SyncResult {
  fetched: number
  cursor: number
}

interface PullResponse {
  records: EncryptedRecord[]
  next_since: number
  has_more: boolean
}

let isSyncRunning = false

export function isSyncing(): boolean {
  return isSyncRunning
}

export async function syncArchive(
  signal?: AbortSignal,
  onProgress?: (p: SyncProgress) => void
): Promise<SyncResult> {
  if (isSyncRunning) {
    const cur = await getCursor()
    return { fetched: 0, cursor: cur }
  }

  isSyncRunning = true

  try {
    const authStore = useAuthStore()
    if (authStore.deviceId) {
      await ensureDevice(authStore.deviceId)
    }

    let fetched = 0

    while (true) {
      if (signal?.aborted) {
        break
      }

      const currentCursor = await getCursor()

      const res = await api<PullResponse>(
        `/api/v1/sync/pull?since=${currentCursor}&limit=500`,
        { signal }
      )

      if (res.records && res.records.length > 0) {
        // Bitta tranzaksiyada yoziladi va kursor yangilanadi
        await putRecordsBatch(res.records, res.next_since)
        fetched += res.records.length

        // Agar ombor ochiq bo'lsa yangi kelgan yozuvlar worker indeksiga ham yuboriladi
        try {
          const vaultStore = useVaultStore()
          if (!vaultStore.locked) {
            const worker = vaultStore.getWorker()
            await worker.indexAdd(res.records)
          }
        } catch {
          // Worker xatoligi sinxronizatsiyani to'xtatmaydi
        }
      } else if (res.next_since !== currentCursor) {
        await setCursor(res.next_since)
      }

      onProgress?.({
        fetched,
        cursor: res.next_since,
        hasMore: res.has_more,
      })

      if (signal?.aborted || !res.has_more) {
        break
      }
    }

    const finalCursor = await getCursor()
    return { fetched, cursor: finalCursor }
  } finally {
    isSyncRunning = false
  }
}
