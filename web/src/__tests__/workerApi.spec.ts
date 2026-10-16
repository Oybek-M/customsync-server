// @vitest-environment node
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { CryptoWorkerClient } from '../crypto/worker-api'
import type { KeyWrapData } from '../crypto/primitives'

class MockWorker {
  onmessage: ((ev: MessageEvent) => void) | null = null
  onerror: ((ev: ErrorEvent) => void) | null = null
  terminated = false
  messagesSent: any[] = []

  postMessage(msg: any) {
    this.messagesSent.push(msg)
  }

  terminate() {
    this.terminated = true
  }

  // Helper to simulate worker response
  respond(data: any) {
    if (this.onmessage) {
      this.onmessage({ data } as MessageEvent)
    }
  }
}

describe('CryptoWorkerClient', () => {
  let mockWorker: MockWorker
  let client: CryptoWorkerClient

  beforeEach(() => {
    vi.useFakeTimers()
    mockWorker = new MockWorker()
    client = new CryptoWorkerClient(mockWorker as any)
  })

  afterEach(() => {
    client.terminate()
    vi.useRealTimers()
  })

  it('sends unlock request with auto-increment id and resolves on response', async () => {
    const wrap: KeyWrapData = {
      salt: 'c2FsdA==',
      nonce: 'bm9uY2U=',
      wrapped_key: 'd3JhcHBlZA==',
      iterations: 600000,
    }

    const promise = client.unlock('my-passphrase', wrap)
    expect(mockWorker.messagesSent.length).toBe(1)
    const req = mockWorker.messagesSent[0]
    expect(req.type).toBe('unlock')
    expect(req.passphrase).toBe('my-passphrase')
    expect(req.id).toBeDefined()

    mockWorker.respond({ id: req.id, ok: true, fingerprint: 'fp-12345678' })
    const res = await promise
    expect(res).toEqual({ ok: true, fingerprint: 'fp-12345678' })
  })

  it('rejects on timeout if worker does not respond', async () => {
    const wrap: KeyWrapData = {
      salt: 'c2FsdA==',
      nonce: 'bm9uY2U=',
      wrapped_key: 'd3JhcHBlZA==',
      iterations: 600000,
    }

    const promise = client.unlock('my-passphrase', wrap, 5000)

    // Advance timers past timeout
    vi.advanceTimersByTime(5001)

    await expect(promise).rejects.toThrow(/timeout/i)
  })

  it('lock and peerHash send typed messages and resolve responses', async () => {
    // 1. Lock
    const lockPromise = client.lock()
    const lockReq = mockWorker.messagesSent[0]
    expect(lockReq.type).toBe('lock')
    mockWorker.respond({ id: lockReq.id, ok: true })
    await expect(lockPromise).resolves.toEqual({ ok: true })

    // 2. peerHash
    const peerPromise = client.peerHash('123456')
    const peerReq = mockWorker.messagesSent[1]
    expect(peerReq.type).toBe('peerHash')
    expect(peerReq.peer_id).toBe('123456')
    mockWorker.respond({ id: peerReq.id, ok: true, peer_hash: 'ph-abcdef' })
    await expect(peerPromise).resolves.toBe('ph-abcdef')
  })

  it('terminate cancels pending requests and calls worker.terminate()', async () => {
    const wrap: KeyWrapData = {
      salt: 'c2FsdA==',
      nonce: 'bm9uY2U=',
      wrapped_key: 'd3JhcHBlZA==',
      iterations: 600000,
    }

    const promise = client.unlock('my-passphrase', wrap)
    client.terminate()

    expect(mockWorker.terminated).toBe(true)
    await expect(promise).rejects.toThrow(/terminated/i)
  })
})
