import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { defineComponent } from 'vue'
import { useServerHealth } from '../composables/useServerHealth'

describe('useServerHealth', () => {
  beforeEach(() => {
    vi.useFakeTimers()
  })

  afterEach(() => {
    vi.restoreAllMocks()
    vi.useRealTimers()
  })

  const TestComponent = defineComponent({
    setup() {
      const health = useServerHealth()
      return { health }
    },
    template: '<div><span class="status">{{ health.status.value }}</span></div>',
  })

  it('sets status to "ok" on 200 response', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(
      new Response(JSON.stringify({ status: 'ok' }), { status: 200 })
    )

    const wrapper = mount(TestComponent)
    expect(wrapper.vm.health.status.value).toBe('unknown')

    // Flush promises
    await flushPromises()

    expect(wrapper.vm.health.status.value).toBe('ok')
    expect(globalThis.fetch).toHaveBeenCalledWith('/api/v1/health', expect.any(Object))
  })

  it('sets status to "down" on non-200 response', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(
      new Response(JSON.stringify({ error: 'Server Error' }), { status: 500 })
    )

    const wrapper = mount(TestComponent)
    await flushPromises()

    expect(wrapper.vm.health.status.value).toBe('down')
  })

  it('sets status to "down" on network error', async () => {
    vi.spyOn(globalThis, 'fetch').mockRejectedValueOnce(new Error('Failed to fetch'))

    const wrapper = mount(TestComponent)
    await flushPromises()

    expect(wrapper.vm.health.status.value).toBe('down')
  })

  it('sets status to "down" on timeout', async () => {
    // Hang fetch until aborted
    vi.spyOn(globalThis, 'fetch').mockImplementation((_url, init) => {
      const signal = init?.signal
      return new Promise((_resolve, reject) => {
        if (signal) {
          signal.addEventListener('abort', () => {
            const err = new DOMException('The operation was aborted.', 'AbortError')
            reject(err)
          })
        }
      })
    })

    const wrapper = mount(TestComponent)
    // Advance timer past the 5000ms timeout
    await vi.advanceTimersByTimeAsync(5000)
    await flushPromises()

    expect(wrapper.vm.health.status.value).toBe('down')
  })

  it('polls every 30 seconds and stops polling on unmount', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response(JSON.stringify({ status: 'ok' }), { status: 200 })
    )

    const wrapper = mount(TestComponent)
    await flushPromises()

    expect(fetchSpy).toHaveBeenCalledTimes(1)

    // Advance 30s -> triggers second poll
    await vi.advanceTimersByTimeAsync(30000)
    await flushPromises()
    expect(fetchSpy).toHaveBeenCalledTimes(2)

    // Unmount component
    wrapper.unmount()
    fetchSpy.mockClear()

    // Advance another 30s, 60s -> no further fetch calls
    await vi.advanceTimersByTimeAsync(60000)
    await flushPromises()

    expect(fetchSpy).toHaveBeenCalledTimes(0)
  })
})
