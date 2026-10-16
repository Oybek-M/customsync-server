import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'
import { flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { useAuthStore } from '../stores/auth'
import { api, enroll, clearTokens, ApiError } from '../api/client'
import { router, setActiveRouter } from '../router'

// authGuard.spec o'z router'ini yaratib guard'ni o'zi ulaydi -- bu yerda esa
// ilovaning HAQIQIY router'i va haqiqiy refresh yo'li tekshiriladi.
describe('auth wiring in the real app', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    setActiveRouter(null)
    localStorage.clear()
    clearTokens()
    vi.restoreAllMocks()
  })

  afterEach(() => {
    localStorage.clear()
    clearTokens()
    vi.restoreAllMocks()
  })

  it('the exported app router sends a non-enrolled user to /enroll', async () => {
    await router.push('/devices')
    expect(router.currentRoute.value.path).toBe('/enroll')
  })

  it('refresh 401 during a normal API call logs the panel out to /enroll', async () => {
    const store = useAuthStore()
    store.setCredentials('web-1', 'refresh-1')
    await router.push('/devices')
    expect(router.currentRoute.value.path).toBe('/devices')

    vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(null, { status: 401 }))
    await expect(api('/api/v1/devices')).rejects.toMatchObject({ status: 401 })

    await flushPromises()
    await new Promise((r) => setTimeout(r, 50))
    await flushPromises()
    expect(store.isEnrolled).toBe(false)
    expect(router.currentRoute.value.path).toBe('/enroll')
  })

  it('enroll keeps the code case and dashes as typed (server normalises)', async () => {
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockResolvedValueOnce(
      new Response(JSON.stringify({
        device_id: 'web-1', refresh_token: 'r', access_token: 'a',
        expires_at: new Date(Date.now() + 3600_000).toISOString(),
      }), { status: 200, headers: { 'Content-Type': 'application/json' } }))

    await enroll('  abc-12 x  ')
    expect(JSON.parse(fetchSpy.mock.calls[0][1]?.body as string).code).toBe('abc-12 x')
  })

  // Server JSON'ni snake_case'da o'qiydi va yozadi (Program.cs SnakeCaseLower).
  it('refresh sends snake_case body and reads snake_case response', async () => {
    useAuthStore().setCredentials('web-1', 'refresh-1')
    const fetchSpy = vi.spyOn(globalThis, 'fetch').mockImplementation(async (input) => {
      if (String(input).endsWith('/refresh')) {
        return new Response(JSON.stringify({
          refresh_token: 'refresh-2', access_token: 'access-2',
          expires_at: new Date(Date.now() + 3600_000).toISOString(),
        }), { status: 200, headers: { 'Content-Type': 'application/json' } })
      }
      return new Response('{}', { status: 200, headers: { 'Content-Type': 'application/json' } })
    })

    await api('/api/v1/devices')

    const refreshCall = fetchSpy.mock.calls.find(([u]) => String(u).endsWith('/refresh'))!
    expect(JSON.parse(refreshCall[1]?.body as string)).toEqual({ device_id: 'web-1', refresh_token: 'refresh-1' })
    const apiCall = fetchSpy.mock.calls.find(([u]) => String(u) === '/api/v1/devices')!
    expect(new Headers(apiCall[1]?.headers).get('Authorization')).toBe('Bearer access-2')
    expect(localStorage.getItem('cs.refreshToken')).toBe('refresh-2')
  })

  it('an aborted request rejects as AbortError, not as a network failure', async () => {
    useAuthStore().setCredentials('web-1', 'refresh-1')
    vi.spyOn(globalThis, 'fetch').mockImplementation(async (input) => {
      if (String(input).endsWith('/refresh')) {
        return new Response(JSON.stringify({
          refresh_token: 'refresh-2', access_token: 'a',
          expires_at: new Date(Date.now() + 3600_000).toISOString(),
        }), { status: 200, headers: { 'Content-Type': 'application/json' } })
      }
      throw new DOMException('aborted', 'AbortError')
    })

    const controller = new AbortController()
    controller.abort()
    const error = (await api('/api/v1/search', { signal: controller.signal }).catch((e: unknown) => e)) as Error
    expect(error).not.toBeInstanceOf(ApiError)
    expect(error.name).toBe('AbortError')
  })
})
