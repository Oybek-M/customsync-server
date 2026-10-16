// @vitest-environment node
import { describe, it, expect } from 'vitest'
import { handleMessage, createInitialWorkerState } from '../crypto/worker'
import {
  unwrapPassphrase,
  fingerprint,
  base64ToBytes,
} from '../crypto/primitives'
import { loadTestVectors } from './testVectorsHelper'

describe('worker createWrap', () => {
  const vectors = loadTestVectors()
  const wrapCase = vectors.key_wrap.cases.find(
    (c: any) => c.name.includes('UTF-8') || c.iterations === 1000
  ) || vectors.key_wrap.cases[0]

  it('rejects createWrap while locked', async () => {
    const state = createInitialWorkerState()
    const res = await handleMessage(state, {
      id: 1,
      type: 'createWrap',
      passphrase: 'yangi-kuchli-parol-1234',
      label: 'Yangi o\'ram',
    })

    expect(res).toEqual({
      id: 1,
      ok: false,
      error: 'locked',
    })
  })

  it('keeps master while unlocked and creates wrap that re-opens to same fingerprint', async () => {
    const state = createInitialWorkerState()

    // 1. Unlock
    const unlockRes = await handleMessage(state, {
      id: 1,
      type: 'unlock',
      passphrase: wrapCase.passphrase,
      wrap: wrapCase,
    })
    expect(unlockRes.ok).toBe(true)
    expect(unlockRes.fingerprint).toBe(wrapCase.fingerprint)
    expect(state.locked).toBe(false)

    // 2. createWrap with a new passphrase
    const newPassphrase = 'yangi-maxfiy-parol-kamida-12'
    const createRes = await handleMessage(state, {
      id: 2,
      type: 'createWrap',
      passphrase: newPassphrase,
      label: 'Zaxira o\'rami',
    })

    expect(createRes.id).toBe(2)
    expect(createRes.ok).toBe(true)
    expect(createRes.iterations).toBe(600000)
    expect(typeof createRes.salt).toBe('string')
    expect(typeof createRes.nonce).toBe('string')
    expect(typeof createRes.wrapped_key).toBe('string')

    // Salt should be 16 bytes base64 (24 chars with padding)
    const saltBytes = base64ToBytes(createRes.salt)
    expect(saltBytes.length).toBe(16)

    // Nonce should be 12 bytes base64 (16 chars)
    const nonceBytes = base64ToBytes(createRes.nonce)
    expect(nonceBytes.length).toBe(12)

    // Verify: Re-open with new passphrase must produce the EXACT same fingerprint
    const reopenedMaster = await unwrapPassphrase(
      {
        salt: createRes.salt,
        nonce: createRes.nonce,
        wrapped_key: createRes.wrapped_key,
        iterations: createRes.iterations,
      },
      newPassphrase
    )
    expect(reopenedMaster).not.toBeNull()
    const reopenedFp = await fingerprint(reopenedMaster!)
    expect(reopenedFp).toBe(wrapCase.fingerprint)

    // 3. Lock zeroes master key and subsequent createWrap fails
    const lockRes = await handleMessage(state, { id: 3, type: 'lock' })
    expect(lockRes).toEqual({ id: 3, ok: true })
    expect(state.locked).toBe(true)

    const postLockCreate = await handleMessage(state, {
      id: 4,
      type: 'createWrap',
      passphrase: newPassphrase,
      label: 'Zaxira o\'rami',
    })
    expect(postLockCreate).toEqual({
      id: 4,
      ok: false,
      error: 'locked',
    })
  })
})
