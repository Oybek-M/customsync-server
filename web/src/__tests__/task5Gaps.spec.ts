import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { defineComponent } from 'vue'
import KeysView from '../views/KeysView.vue'
import { useVaultStore } from '../stores/vault'
import { useAuthStore } from '../stores/auth'
import { useAutoLock } from '../composables/useAutoLock'
import { clearTokens, setAccessTokenForTest } from '../api/client'
import { CryptoWorkerClient } from '../crypto/worker-api'
import { handleMessage, createInitialWorkerState } from '../crypto/worker'
import { loadTestVectors } from './testVectorsHelper'

const twoWraps = [
  { wrap_id: 'w1', wrap_type: 'passphrase', label: 'Asosiy', created_at: '2026-10-10T12:00:00Z', last_used_at: null },
  { wrap_id: 'w2', wrap_type: 'passphrase', label: 'Zaxira', created_at: '2026-10-11T12:00:00Z', last_used_at: null },
]

describe('task 5 gaps found by mutation testing', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    localStorage.clear()
    clearTokens()
    useAuthStore().setCredentials('dev-test-web', 'mock-refresh-token')
    setAccessTokenForTest('mock-access-token')
  })

  afterEach(() => {
    localStorage.clear()
    clearTokens()
    vi.restoreAllMocks()
    vi.useRealTimers()
  })

  // Doimiy "kamida 12 belgi" ko'rsatmasi matnda bor -- shuning uchun
  // matnni emas, hech narsa yuborilmaganini tekshiramiz.
  it('an 11-character passphrase never reaches the worker or the server', async () => {
    const posted: string[] = []
    vi.spyOn(globalThis, 'fetch').mockImplementation(async (_u, init) => {
      if (init?.method === 'POST') posted.push('POST')
      return new Response(JSON.stringify(twoWraps), { status: 200, headers: { 'Content-Type': 'application/json' } })
    })
    const sent: string[] = []
    const fakeWorker = { onmessage: null as any, onerror: null as any, postMessage: (d: any) => sent.push(d.type), terminate: () => {} }
    const vault = useVaultStore()
    vault.locked = false
    vault.setWorkerClient(new CryptoWorkerClient(fakeWorker as any))

    const wrapper = mount(KeysView)
    await flushPromises()
    await wrapper.find('.cs-btn-new-wrap').trigger('click')
    await wrapper.find('input[name="label"]').setValue('Yangi')
    await wrapper.find('input[name="passphrase"]').setValue('12345678901')
    await wrapper.find('input[name="confirmPassphrase"]').setValue('12345678901')
    await wrapper.find('.cs-btn-submit-wrap').trigger('click')
    await flushPromises()

    expect(sent).not.toContain('createWrap')
    expect(posted).toEqual([])
  })

  it('every listener added by useAutoLock is removed on unmount', async () => {
    vi.useFakeTimers()
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } }))
    const added: [string, unknown][] = []
    const removed: [string, unknown][] = []
    const addSpy = vi.spyOn(window, 'addEventListener').mockImplementation((t: string, h: any) => { added.push([t, h]) })
    const removeSpy = vi.spyOn(window, 'removeEventListener').mockImplementation((t: string, h: any) => { removed.push([t, h]) })
    useVaultStore().locked = false

    const wrapper = mount(defineComponent({ setup() { useAutoLock(); return {} }, template: '<div />' }))
    await flushPromises()
    wrapper.unmount()

    expect(added.length).toBeGreaterThan(0)
    for (const [type, handler] of added)
      expect(removed.some(([t, h]) => t === type && h === handler)).toBe(true)
    addSpy.mockRestore()
    removeSpy.mockRestore()
  })
})

describe('worker createWrap safety', () => {
  const wrapCase = loadTestVectors().key_wrap.cases[0]

  async function unlocked() {
    const state = createInitialWorkerState()
    const res = await handleMessage(state, { id: 1, type: 'unlock', passphrase: wrapCase.passphrase, wrap: wrapCase })
    expect(res.ok).toBe(true)
    return state
  }

  // Bir xil salt+nonce: bir xil parol bilan yaratilgan o'ramlar bir xil
  // shifrmatn beradi va GCM nonce qayta ishlatiladi.
  it('two wraps get fresh salt and nonce', async () => {
    const state = await unlocked()
    const a: any = await handleMessage(state, { id: 2, type: 'createWrap', passphrase: 'bir-xil-uzun-parol-2026', label: 'a' })
    const b: any = await handleMessage(state, { id: 3, type: 'createWrap', passphrase: 'bir-xil-uzun-parol-2026', label: 'b' })
    expect(a.ok && b.ok).toBe(true)
    expect(a.salt).not.toBe(b.salt)
    expect(a.nonce).not.toBe(b.nonce)
  }, 60_000)

  // Qayta ochish tekshiruvi haqiqatan ishlaydi: kutilgan barmoq izi mos
  // kelmasa o'ram qaytarilmaydi (serverga hech qachon chiqmaydi).
  it('a wrap that does not re-open to the expected fingerprint is not returned', async () => {
    const state = await unlocked()
    ;(state as any).fingerprint = '0000000000000000'
    const res: any = await handleMessage(state, { id: 4, type: 'createWrap', passphrase: 'bir-xil-uzun-parol-2026', label: 'c' })
    expect(res.ok).toBe(false)
    expect(res.wrapped_key).toBeUndefined()
  }, 60_000)
})
