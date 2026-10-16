import type { KeyWrapData } from './primitives'
import type { DecryptedIndexedRecord } from './worker'

export type { DecryptedIndexedRecord }

export interface OpenRecordInput {
  record_id: string
  nonce: string
  payload: string
}

export interface OpenRecordResult {
  record_id: string
  json?: any
  corrupt?: boolean
}

export interface UnlockResult {
  ok: boolean
  fingerprint?: string
  error?: string
}

export interface CreateWrapResult {
  ok: boolean
  salt?: string
  nonce?: string
  wrapped_key?: string
  iterations?: number
  error?: string
}

export interface IndexQueryParams {
  kind?: string
  peer_hash?: string
  before?: [number, number]
  limit?: number
}

interface PendingRequest {
  resolve: (value: any) => void
  reject: (reason: any) => void
  timer: any
}

export class CryptoWorkerClient {
  private worker: Worker
  private nextId: number = 1
  private pending: Map<number, PendingRequest> = new Map()
  private terminated: boolean = false

  constructor(worker?: Worker) {
    if (worker) {
      this.worker = worker
    } else {
      this.worker = new Worker(new URL('./worker.ts', import.meta.url), {
        type: 'module',
      })
    }

    this.worker.onmessage = (event: MessageEvent) => {
      const data = event.data
      if (!data || typeof data.id === 'undefined') return

      const req = this.pending.get(data.id)
      if (req) {
        clearTimeout(req.timer)
        this.pending.delete(data.id)
        const { id: _unusedId, ...rest } = data
        req.resolve(rest)
      }
    }

    this.worker.onerror = (error: ErrorEvent) => {
      for (const req of this.pending.values()) {
        clearTimeout(req.timer)
        req.reject(new Error(`Crypto worker error: ${error.message || 'unknown'}`))
      }
      this.pending.clear()
    }
  }

  private send<T>(type: string, payload: any = {}, timeoutMs = 30000): Promise<T> {
    if (this.terminated) {
      return Promise.reject(new Error('Crypto worker terminated'))
    }

    const id = this.nextId++

    return new Promise<T>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id)
        reject(new Error(`Crypto worker request timeout (${type}, id: ${id})`))
      }, timeoutMs)

      this.pending.set(id, { resolve, reject, timer })
      this.worker.postMessage({ id, type, ...payload })
    })
  }

  async unlock(
    passphrase: string,
    wrap: KeyWrapData,
    timeoutMs = 60000
  ): Promise<UnlockResult> {
    const res = await this.send<UnlockResult>(
      'unlock',
      { passphrase, wrap },
      timeoutMs
    )
    return res
  }

  async lock(timeoutMs = 5000): Promise<{ ok: boolean }> {
    return this.send<{ ok: boolean }>('lock', {}, timeoutMs)
  }

  async openRecords(
    records: OpenRecordInput[],
    timeoutMs = 30000
  ): Promise<OpenRecordResult[]> {
    const res = await this.send<{ ok: boolean; results?: OpenRecordResult[]; error?: string }>(
      'openRecords',
      { records },
      timeoutMs
    )

    if (!res.ok) {
      throw new Error(res.error || 'Failed to open records')
    }

    return res.results || []
  }

  async peerHash(peerId: string, timeoutMs = 5000): Promise<string> {
    const res = await this.send<{ ok: boolean; peer_hash?: string; error?: string }>(
      'peerHash',
      { peer_id: peerId },
      timeoutMs
    )

    if (!res.ok || !res.peer_hash) {
      throw new Error(res.error || 'Failed to compute peer hash')
    }

    return res.peer_hash
  }

  async createWrap(
    passphrase: string,
    label?: string,
    timeoutMs = 60000
  ): Promise<CreateWrapResult> {
    return this.send<CreateWrapResult>(
      'createWrap',
      { passphrase, label },
      timeoutMs
    )
  }

  async indexBegin(timeoutMs = 5000): Promise<void> {
    const res = await this.send<{ ok: boolean; error?: string }>('indexBegin', {}, timeoutMs)
    if (!res.ok) {
      throw new Error(res.error || 'Failed to begin index')
    }
  }

  async indexAdd(
    records: any[],
    timeoutMs = 60000
  ): Promise<{ added: number; corrupt: number }> {
    const res = await this.send<{ ok: boolean; added?: number; corrupt?: number; error?: string }>(
      'indexAdd',
      { records },
      timeoutMs
    )
    if (!res.ok) {
      throw new Error(res.error || 'Failed to add records to index')
    }
    return { added: res.added ?? 0, corrupt: res.corrupt ?? 0 }
  }

  async indexQuery(
    params: IndexQueryParams = {},
    timeoutMs = 10000
  ): Promise<DecryptedIndexedRecord[]> {
    const res = await this.send<{ ok: boolean; rows?: DecryptedIndexedRecord[]; error?: string }>(
      'indexQuery',
      params,
      timeoutMs
    )
    if (!res.ok) {
      throw new Error(res.error || 'Failed to query index')
    }
    return res.rows || []
  }

  async indexStats(timeoutMs = 5000): Promise<{ count: number; corrupt: number }> {
    const res = await this.send<{ ok: boolean; count?: number; corrupt?: number; error?: string }>(
      'indexStats',
      {},
      timeoutMs
    )
    if (!res.ok) {
      throw new Error(res.error || 'Failed to get index stats')
    }
    return { count: res.count ?? 0, corrupt: res.corrupt ?? 0 }
  }

  terminate(): void {
    this.terminated = true
    for (const [, req] of this.pending.entries()) {
      clearTimeout(req.timer)
      req.reject(new Error('Crypto worker terminated'))
    }
    this.pending.clear()
    this.worker.terminate()
  }
}
