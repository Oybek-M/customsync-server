import { defineStore } from 'pinia'
import { ref } from 'vue'

export type Theme = 'light' | 'dark'

function getInitialTheme(): Theme {
  try {
    const saved = localStorage.getItem('cs-theme')
    if (saved === 'light' || saved === 'dark') {
      return saved
    }
  } catch {
    // localStorage may be disabled or throw in restricted environments
  }
  return 'light'
}

export const useUiStore = defineStore('ui', () => {
  const theme = ref<Theme>(getInitialTheme())
  const sidebarOpen = ref(false)

  // If saved theme was already dark on startup, apply it
  if (typeof document !== 'undefined' && document.documentElement) {
    if (theme.value === 'dark') {
      document.documentElement.dataset.theme = 'dark'
    }
  }

  function applyTheme(t: Theme) {
    if (typeof document !== 'undefined' && document.documentElement) {
      document.documentElement.dataset.theme = t
    }
  }

  function setTheme(newTheme: Theme) {
    theme.value = newTheme
    applyTheme(newTheme)
    try {
      localStorage.setItem('cs-theme', newTheme)
    } catch {
      // storage may be unavailable; app must still work
    }
  }

  function toggleTheme() {
    setTheme(theme.value === 'dark' ? 'light' : 'dark')
  }

  function toggleSidebar() {
    sidebarOpen.value = !sidebarOpen.value
  }

  function closeSidebar() {
    sidebarOpen.value = false
  }

  return {
    theme,
    sidebarOpen,
    setTheme,
    toggleTheme,
    toggleSidebar,
    closeSidebar,
  }
})
