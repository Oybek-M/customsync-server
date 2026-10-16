<script setup lang="ts">
import { useRoute } from 'vue-router'
import { useVaultStore } from '../stores/vault'
import { useUiStore } from '../stores/ui'
import { useServerHealth } from '../composables/useServerHealth'
import { useAutoLock } from '../composables/useAutoLock'
import UnlockDialog from './UnlockDialog.vue'

const route = useRoute()
const vaultStore = useVaultStore()
const uiStore = useUiStore()
const health = useServerHealth()
useAutoLock()

function handleVaultIndicatorClick() {
  if (vaultStore.locked) {
    vaultStore.openUnlockDialog()
  } else {
    vaultStore.lock()
  }
}

const navItems = [
  { path: '/', label: 'Boshqaruv paneli' },
  { path: '/devices', label: 'Qurilmalar' },
  { path: '/archive', label: 'Arxiv' },
  { path: '/stats', label: 'Statistika' },
  { path: '/storage', label: 'Xotira' },
  { path: '/keys', label: 'Kalitlar' },
  { path: '/settings', label: 'Sozlamalar' },
  { path: '/audit', label: 'Audit' },
]
</script>

<template>
  <div v-if="route.path === '/enroll'">
    <router-view />
  </div>
  <div v-else class="cs-shell">
    <div
      v-if="uiStore.sidebarOpen"
      class="cs-sidebar-overlay"
      @click="uiStore.closeSidebar"
    ></div>

    <aside class="cs-sidebar" :class="{ 'is-open': uiStore.sidebarOpen }">
      <div class="cs-sidebar-brand">CustomSync</div>
      <nav class="cs-sidebar-nav">
        <router-link
          v-for="item in navItems"
          :key="item.path"
          :to="item.path"
          custom
          v-slot="{ href, navigate }"
        >
          <a
            :href="href"
            class="cs-nav-item"
            :class="{ 'is-active': route.path === item.path }"
            @click="(e) => { uiStore.closeSidebar(); navigate(e); }"
          >
            {{ item.label }}
          </a>
        </router-link>
      </nav>
    </aside>

    <div class="cs-main">
      <header class="cs-topbar">
        <div class="cs-topbar-left">
          <button
            class="cs-btn-icon cs-sidebar-toggle"
            aria-label="Menyu"
            @click="uiStore.toggleSidebar"
          >
            <i class="pi pi-bars"></i>
          </button>
          <span class="cs-status-indicator" :title="'Server: ' + health.status.value">
            <span
              class="cs-status-dot"
              :class="'cs-status-' + health.status.value"
            ></span>
            <span>Server</span>
          </span>
        </div>

        <div class="cs-topbar-right">
          <span
            class="cs-vault-indicator"
            role="button"
            tabindex="0"
            :title="vaultStore.locked ? 'Omborni ochish' : 'Qulflash'"
            style="cursor: pointer;"
            @click="handleVaultIndicatorClick"
          >
            <i :class="vaultStore.locked ? 'pi pi-lock' : 'pi pi-lock-open'"></i>
            <span>{{ vaultStore.locked ? 'Qulflangan' : 'Ochiq' }}</span>
          </span>

          <button
            class="cs-btn-icon cs-theme-toggle"
            :aria-label="'Mavzu: ' + uiStore.theme"
            @click="uiStore.toggleTheme"
          >
            <i :class="uiStore.theme === 'dark' ? 'pi pi-sun' : 'pi pi-moon'"></i>
          </button>
        </div>
      </header>

      <main class="cs-content">
        <router-view />
      </main>
    </div>
    <UnlockDialog />
  </div>
</template>
