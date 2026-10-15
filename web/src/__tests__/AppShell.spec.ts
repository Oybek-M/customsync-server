import { describe, it, expect, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import AppShell from '../components/AppShell.vue'
import { routes } from '../router'
import { useVaultStore } from '../stores/vault'

describe('AppShell.vue', () => {
  let router: ReturnType<typeof createRouter>

  beforeEach(async () => {
    setActivePinia(createPinia())
    router = createRouter({
      history: createMemoryHistory(),
      routes,
    })
    router.push('/')
    await router.isReady()
  })

  it('renders all 8 sidebar items in the stated order with the stated labels', () => {
    const wrapper = mount(AppShell, {
      global: {
        plugins: [router],
      },
    })

    const expectedLabels = [
      'Boshqaruv paneli',
      'Qurilmalar',
      'Arxiv',
      'Statistika',
      'Xotira',
      'Kalitlar',
      'Sozlamalar',
      'Audit',
    ]

    const navItems = wrapper.findAll('.cs-nav-item')
    expect(navItems.length).toBe(8)

    const labels = navItems.map((item) => item.text().trim())
    expect(labels).toEqual(expectedLabels)
  })

  it('clicking a sidebar item navigates and marks exactly that item .is-active', async () => {
    const wrapper = mount(AppShell, {
      global: {
        plugins: [router],
      },
    })

    await router.isReady()

    // Initially on '/', only first item is active
    let navItems = wrapper.findAll('.cs-nav-item')
    expect(navItems[0].classes()).toContain('is-active')
    const otherInitialActive = navItems.slice(1).filter((i) => i.classes().includes('is-active'))
    expect(otherInitialActive.length).toBe(0)

    // Click 'Qurilmalar' (second item, /devices)
    await navItems[1].trigger('click')
    await flushPromises()
    // Dynamic import needs a microtask tick
    await new Promise((r) => setTimeout(r, 50))
    await flushPromises()

    expect(router.currentRoute.value.path).toBe('/devices')

    navItems = wrapper.findAll('.cs-nav-item')
    expect(navItems[1].classes()).toContain('is-active')

    // Ensure NO other item is .is-active
    const nonActiveItems = [navItems[0], ...navItems.slice(2)]
    for (const item of nonActiveItems) {
      expect(item.classes()).not.toContain('is-active')
    }
  })

  it('vault indicator reflects the store (locked -> closed icon/label; set false -> open)', async () => {
    const vaultStore = useVaultStore()
    vaultStore.locked = true

    const wrapper = mount(AppShell, {
      global: {
        plugins: [router],
      },
    })

    const indicator = wrapper.find('.cs-vault-indicator')
    expect(indicator.exists()).toBe(true)
    expect(indicator.text().toLowerCase()).toMatch(/qulf|yopiq/)
    expect(indicator.find('.pi-lock').exists()).toBe(true)
    expect(indicator.find('.pi-lock-open').exists()).toBe(false)

    // Unlock
    vaultStore.locked = false
    await wrapper.vm.$nextTick()

    expect(indicator.text().toLowerCase()).toMatch(/ochiq/)
    expect(indicator.find('.pi-lock-open').exists()).toBe(true)
    expect(indicator.find('.pi-lock').exists()).toBe(false)
  })
})
