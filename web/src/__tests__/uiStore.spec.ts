import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { useUiStore } from '../stores/ui'

describe('uiStore', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    localStorage.clear()
    delete document.documentElement.dataset.theme
  })

  afterEach(() => {
    vi.restoreAllMocks()
    localStorage.clear()
    delete document.documentElement.dataset.theme
  })

  it('theme toggle sets document.documentElement.dataset.theme and persists it', () => {
    const uiStore = useUiStore()
    expect(uiStore.theme).toBe('light')
    expect(document.documentElement.dataset.theme).toBeUndefined()

    // Toggle to dark
    uiStore.toggleTheme()
    expect(uiStore.theme).toBe('dark')
    expect(document.documentElement.dataset.theme).toBe('dark')
    expect(localStorage.getItem('cs-theme')).toBe('dark')

    // Toggle to light
    uiStore.toggleTheme()
    expect(uiStore.theme).toBe('light')
    expect(document.documentElement.dataset.theme).toBe('light')
    expect(localStorage.getItem('cs-theme')).toBe('light')
  })

  it('when localStorage throws, toggling still works and nothing is thrown', () => {
    const uiStore = useUiStore()

    // Stub localStorage.setItem to throw an error (e.g. QuotaExceeded or SecurityError in private browsing)
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('The operation is insecure.', 'SecurityError')
    })

    expect(() => {
      uiStore.toggleTheme()
    }).not.toThrow()

    expect(uiStore.theme).toBe('dark')
    expect(document.documentElement.dataset.theme).toBe('dark')

    expect(() => {
      uiStore.toggleTheme()
    }).not.toThrow()

    expect(uiStore.theme).toBe('light')
    expect(document.documentElement.dataset.theme).toBe('light')
  })
})
