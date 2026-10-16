import { describe, it, expect } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import PrimeVue from 'primevue/config'
import RecordTable, { formatPreview } from '../components/RecordTable.vue'
import type { DecryptedIndexedRecord } from '../crypto/worker-api'

if (typeof globalThis.ResizeObserver === 'undefined') {
  globalThis.ResizeObserver = class ResizeObserver {
    observe() {}
    unobserve() {}
    disconnect() {}
  } as any
}

Object.defineProperty(HTMLElement.prototype, 'clientHeight', { configurable: true, value: 600 })
Object.defineProperty(HTMLElement.prototype, 'offsetHeight', { configurable: true, value: 600 })
Object.defineProperty(HTMLElement.prototype, 'scrollHeight', { configurable: true, value: 600 })
Object.defineProperty(HTMLElement.prototype, 'clientWidth', { configurable: true, value: 800 })
Object.defineProperty(HTMLElement.prototype, 'offsetWidth', { configurable: true, value: 800 })
Object.defineProperty(HTMLElement.prototype, 'scrollWidth', { configurable: true, value: 800 })
Object.defineProperty(HTMLElement.prototype, 'offsetParent', {
  configurable: true,
  get() {
    return document.body
  },
})

describe('RecordTable.vue', () => {
  it('preview renders <script> text literally without HTML injection', async () => {
    const maliciousPayload = {
      text: '<script>alert("xss")</script>',
    }

    // 1. Direct preview formatter output
    const preview = formatPreview(maliciousPayload)
    expect(preview).toBe('<script>alert("xss")</script>')

    // 2. Component mounting test
    const record: DecryptedIndexedRecord = {
      record_id: 'rec-xss-1',
      kind: 'deleted',
      peer_hash: 'peer-hash-1234',
      msg_id: 1,
      occurred_at: 1700000000,
      seq: 1,
      device_id: 'dev-1',
      json: maliciousPayload,
    }

    const wrapper = mount(RecordTable, {
      attachTo: document.body,
      global: {
        plugins: [PrimeVue],
      },
      props: {
        rows: [record],
        loading: false,
        deviceNames: { 'dev-1': 'Test Device' },
      },
    })
    await flushPromises()

    // HTML should escape the script tag
    expect(wrapper.html()).not.toContain('<script>alert("xss")</script>')
    expect(wrapper.html()).toContain('&lt;script&gt;alert("xss")&lt;/script&gt;')

    // DOM should have no executable script element
    expect(wrapper.find('script').exists()).toBe(false)

    // Rendered text is literal
    expect(wrapper.text()).toContain('<script>alert("xss")</script>')
    expect(wrapper.text()).toContain('Test Device')
    expect(wrapper.text()).toContain('rec-xss-1')
  })
})
