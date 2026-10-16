import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import KeysView from '../views/KeysView.vue'
import { useVaultStore } from '../stores/vault'
import { useAuthStore } from '../stores/auth'
import { clearTokens, setAccessTokenForTest } from '../api/client'
import { CryptoWorkerClient } from '../crypto/worker-api'

function createFakeWorkerClient() {
  const mockWorker = {
    onmessage: null as any,
    onerror: null as any,
    postMessage: (data: any) => {
      if (data.type === 'createWrap') {
        mockWorker.onmessage?.({
          data: {
            id: data.id,
            ok: true,
            salt: 'YWJjZGVmZ2hpamtsbW5vcA==',
            nonce: 'MTIzNDU2Nzg5MDEy',
            wrapped_key: 'd3JhcHBlZF9rZXlfYmFzZTY0X3N0cmluZw==',
            iterations: 600000,
          },
        })
      }
    },
    terminate: () => {},
  }
  return new CryptoWorkerClient(mockWorker as any)
}

describe('KeysView.vue', () => {
  let originalFetch: any

  beforeEach(() => {
    setActivePinia(createPinia())
    localStorage.clear()
    clearTokens()
    const authStore = useAuthStore()
    authStore.setCredentials('dev-test-web', 'mock-refresh-token')
    setAccessTokenForTest('mock-access-token')
    originalFetch = globalThis.fetch
  })

  afterEach(() => {
    localStorage.clear()
    clearTokens()
    globalThis.fetch = originalFetch
    vi.restoreAllMocks()
  })

  const singleWrap = [
    {
      wrap_id: 'wrap-1',
      wrap_type: 'passphrase',
      label: 'Yagona o\'ram',
      created_at: '2026-10-10T12:00:00Z',
      last_used_at: null,
    },
  ]

  const multipleWraps = [
    {
      wrap_id: 'wrap-1',
      wrap_type: 'passphrase',
      label: 'Birinchi o\'ram',
      created_at: '2026-10-10T12:00:00Z',
      last_used_at: null,
    },
    {
      wrap_id: 'wrap-2',
      wrap_type: 'passphrase',
      label: 'Ikkinchi o\'ram',
      created_at: '2026-10-11T12:00:00Z',
      last_used_at: '2026-10-12T15:30:00Z',
    },
  ]

  it('disables delete when only one wrap is listed and shows the exact warning text', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => singleWrap,
    } as any)

    const wrapper = mount(KeysView)
    await flushPromises()

    const deleteBtn = wrapper.find('.cs-btn-delete')
    expect(deleteBtn.exists()).toBe(true)
    expect(deleteBtn.attributes('disabled')).toBeDefined()

    const warningText = "Bu oxirgi o'ram. O'chirsangiz arxivga kirish butunlay yo'qoladi."
    expect(wrapper.text()).toContain(warningText)
  })

  it('allows delete when multiple wraps exist and requires confirm dialog naming the label', async () => {
    let deletedId = ''
    globalThis.fetch = vi.fn().mockImplementation(async (url: string, init?: any) => {
      if (init?.method === 'DELETE') {
        deletedId = url.split('/').pop()!
        return { ok: true, status: 204 }
      }
      return {
        ok: true,
        status: 200,
        json: async () => multipleWraps,
      }
    })

    const wrapper = mount(KeysView)
    await flushPromises()

    const deleteBtns = wrapper.findAll('.cs-btn-delete')
    expect(deleteBtns.length).toBe(2)
    expect(deleteBtns[0].attributes('disabled')).toBeUndefined()

    // Click delete on first wrap
    await deleteBtns[0].trigger('click')

    // Confirm dialog appears naming the label "Birinchi o'ram"
    expect(wrapper.text()).toContain("Birinchi o'ram")

    // Confirm delete
    const confirmBtn = wrapper.find('.cs-btn-confirm-delete')
    expect(confirmBtn.exists()).toBe(true)
    await confirmBtn.trigger('click')
    await flushPromises()

    expect(deletedId).toBe('wrap-1')
  })

  it('handles server 409 last_wrap by showing message and reloading wrap list', async () => {
    let getCalls = 0
    globalThis.fetch = vi.fn().mockImplementation(async (_url: string, init?: any) => {
      if (init?.method === 'DELETE') {
        return {
          ok: false,
          status: 409,
          json: async () => ({ error: 'last_wrap' }),
        }
      }
      getCalls++
      return {
        ok: true,
        status: 200,
        json: async () => multipleWraps,
      }
    })

    const wrapper = mount(KeysView)
    await flushPromises()

    const initialGetCalls = getCalls

    const deleteBtns = wrapper.findAll('.cs-btn-delete')
    await deleteBtns[0].trigger('click')

    const confirmBtn = wrapper.find('.cs-btn-confirm-delete')
    await confirmBtn.trigger('click')
    await flushPromises()

    const warningText = "Bu oxirgi o'ram. O'chirsangiz arxivga kirish butunlay yo'qoladi."
    expect(wrapper.text()).toContain(warningText)
    // Reloads wrap list on 409
    expect(getCalls).toBeGreaterThan(initialGetCalls)
  })

  it('new-wrap form rejects passphrase < 12 characters and mismatched confirmation', async () => {
    const vaultStore = useVaultStore()
    vaultStore.locked = false // Must be unlocked to create wrap

    globalThis.fetch = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => multipleWraps,
    } as any)

    const wrapper = mount(KeysView)
    await flushPromises()

    // Open new wrap modal
    const newWrapBtn = wrapper.find('.cs-btn-new-wrap')
    expect(newWrapBtn.exists()).toBe(true)
    await newWrapBtn.trigger('click')

    // 1. Passphrase < 12 chars
    const labelInput = wrapper.find('input[name="label"]')
    const pwInput = wrapper.find('input[name="passphrase"]')
    const confirmPwInput = wrapper.find('input[name="confirmPassphrase"]')
    const submitBtn = wrapper.find('.cs-btn-submit-wrap')

    await labelInput.setValue('Yangi kalit')
    await pwInput.setValue('qisqa')
    await confirmPwInput.setValue('qisqa')
    await submitBtn.trigger('click')
    await flushPromises()

    expect(wrapper.text()).toMatch(/12 belgidan|kamida 12/i)

    // 2. Mismatched confirmation
    await pwInput.setValue('yetarlicha-uzun-parol-123')
    await confirmPwInput.setValue('boshqa-parol-mos-emas')
    await submitBtn.trigger('click')
    await flushPromises()

    expect(wrapper.text()).toMatch(/mos kelmadi|mos emas/i)
  })

  it('submits new wrap with snake_case and base64 fields created by worker', async () => {
    const vaultStore = useVaultStore()
    vaultStore.locked = false
    vaultStore.setWorkerClient(createFakeWorkerClient())

    let postBody: any = null
    globalThis.fetch = vi.fn().mockImplementation(async (_url: string, init?: any) => {
      if (init?.method === 'POST') {
        postBody = JSON.parse(init.body)
        return {
          ok: true,
          status: 200,
          json: async () => ({ wrap_id: 'wrap-created-123' }),
        }
      }
      return {
        ok: true,
        status: 200,
        json: async () => multipleWraps,
      }
    })

    const wrapper = mount(KeysView)
    await flushPromises()

    await wrapper.find('.cs-btn-new-wrap').trigger('click')

    const labelInput = wrapper.find('input[name="label"]')
    const pwInput = wrapper.find('input[name="passphrase"]')
    const confirmPwInput = wrapper.find('input[name="confirmPassphrase"]')

    await labelInput.setValue('Zaxira o\'rami 2026')
    await pwInput.setValue('maxfiy-kuchli-parol-2026')
    await confirmPwInput.setValue('maxfiy-kuchli-parol-2026')

    await wrapper.find('.cs-btn-submit-wrap').trigger('click')
    await flushPromises()

    expect(postBody).not.toBeNull()
    expect(postBody.wrap_type).toBe('passphrase')
    expect(postBody.label).toBe("Zaxira o'rami 2026")
    expect(postBody.iterations).toBe(600000)
    expect(postBody.salt).toBe('YWJjZGVmZ2hpamtsbW5vcA==')
    expect(postBody.nonce).toBe('MTIzNDU2Nzg5MDEy')
    expect(postBody.wrapped_key).toBe('d3JhcHBlZF9rZXlfYmFzZTY0X3N0cmluZw==')
  })
})
