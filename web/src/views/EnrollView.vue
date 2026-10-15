<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { useAuthStore } from '../stores/auth'
import { ApiError } from '../api/client'

const router = useRouter()
const authStore = useAuthStore()

const code = ref('')
const name = ref('Web panel')
const isPending = ref(false)
const errorMessage = ref('')

function getErrorMessage(err: unknown): string {
  if (err instanceof ApiError) {
    if (err.status === 400 || err.code === 'invalid_or_used_code') {
      return "Ulanish kodi noto'g'ri yoki allaqachon ishlatilgan"
    }
    if (err.status === 0 || err.code === 'network_error') {
      return "Serverga ulanib bo'lmadi. Internet yoki server holatini tekshiring"
    }
  }
  return "Ulanishda kutilmagan xatolik yuz berdi"
}

async function onSubmit() {
  const trimmed = code.value.trim()
  if (!trimmed || isPending.value) return

  isPending.value = true
  errorMessage.value = ''

  try {
    await authStore.enroll(trimmed, name.value)
    await router.push('/')
  } catch (err) {
    errorMessage.value = getErrorMessage(err)
  } finally {
    isPending.value = false
  }
}
</script>

<template>
  <div class="cs-enroll-layout">
    <div class="cs-card cs-enroll-card">
      <div class="cs-enroll-header">
        <h1 class="cs-enroll-title">CustomSync</h1>
        <p class="cs-enroll-desc">Serverni boshqarish uchun ulanish kodini kiriting</p>
      </div>

      <form @submit.prevent="onSubmit">
        <div class="cs-form-group">
          <label for="enroll-code" class="cs-label">Ulanish kodi</label>
          <input
            id="enroll-code"
            v-model="code"
            type="text"
            autocomplete="off"
            spellcheck="false"
            class="cs-input"
            placeholder="Kodni kiriting (masalan: ABCD-1234)"
            data-test="code-input"
          />
        </div>

        <div class="cs-form-group">
          <label for="enroll-name" class="cs-label">Qurilma nomi</label>
          <input
            id="enroll-name"
            v-model="name"
            type="text"
            autocomplete="off"
            spellcheck="false"
            class="cs-input"
            placeholder="Qurilma nomi"
            data-test="name-input"
          />
        </div>

        <button
          type="submit"
          class="cs-btn cs-btn-primary cs-btn-block"
          :disabled="isPending || !code.trim()"
          data-test="submit-button"
        >
          <i v-if="isPending" class="pi pi-spin pi-spinner"></i>
          <span>{{ isPending ? 'Ulanmoqda...' : 'Ulash' }}</span>
        </button>

        <div v-if="errorMessage" class="cs-error-msg" role="alert">
          {{ errorMessage }}
        </div>
      </form>
    </div>
  </div>
</template>
