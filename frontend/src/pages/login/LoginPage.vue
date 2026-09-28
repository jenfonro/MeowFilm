<template>
  <div class="card">
    <div class="brand">{{ bootstrap.siteName }}</div>
    <form id="loginForm" @submit.prevent="submit">
      <div class="field">
        <input
          class="input"
          v-model="username"
          name="username"
          type="text"
          placeholder="输入用户名"
          autocomplete="username"
        />
      </div>
      <div class="field">
        <input
          class="input"
          v-model="password"
          name="password"
          type="password"
          placeholder="输入访问密码"
          autocomplete="current-password"
        />
      </div>
      <div class="error" v-if="error">{{ error }}</div>
      <button type="submit" class="btn" :disabled="loading">
        {{ loading ? '登录中...' : '登录' }}
      </button>
    </form>
    <div class="footer">
      <div class="min-w-0 flex flex-col items-center gap-0.5">
        <div class="text-xs text-gray-500 truncate">{{ appVersion }}</div>
        <div v-if="backendCommit && backendCommit !== appVersion" class="text-xs text-gray-500 truncate">后端 {{ backendCommit }}</div>
        <div v-if="frontendCommit && frontendCommit !== appVersion" class="text-xs text-gray-500 truncate">前端 {{ frontendCommit }}</div>
      </div>
    </div>
  </div>
</template>

<script setup>
	import { ref } from 'vue';
	import { apiPostForm } from '../../shared/apiClient';

const props = defineProps({
  bootstrap: { type: Object, required: true },
});

	const appVersion =
	  (typeof window !== 'undefined' && window.__MEOWFILM_VERSION__) || 'beta';
	const backendCommit =
	  (typeof window !== 'undefined' && window.__MEOWFILM_BACKEND_COMMIT__) || appVersion;
	const frontendCommit =
	  (typeof window !== 'undefined' && window.__MEOWFILM_FRONTEND_COMMIT__) || appVersion;

const username = ref('');
const password = ref('');
const error = ref('');
const loading = ref(false);

const submit = async () => {
  error.value = '';
  const u = (username.value || '').trim();
  const p = password.value || '';
  if (!u || !p) {
    error.value = '请输入用户名和密码';
    return;
  }

	loading.value = true;
	try {
	  const data = await apiPostForm('/api/login', { username: u, password: p });
	  if (data && data.success) {
	    window.location.href = '/';
	    return;
	  }
	  error.value = (data && data.message) || '登录失败';
	} catch (e) {
	  error.value = (e && e.message) ? String(e.message) : '网络错误';
	} finally {
	  loading.value = false;
	}
};
</script>

<style scoped>
.card {
  --primary: #21a45a;
  --primary-dark: #1a8d4d;
  --muted: #8b95a1;
  --border: #e5e7eb;

  width: min(430px, 94vw);
  background: #fff;
  border-radius: 18px;
  box-shadow: 0 24px 60px rgba(0, 0, 0, 0.08);
  padding: 36px 32px 28px;
  text-align: center;
}
.brand {
  font-size: 28px;
  font-weight: 800;
  color: var(--primary);
  margin-bottom: 26px;
  letter-spacing: 0.3px;
}
.field {
  margin-bottom: 16px;
  text-align: left;
}
.input {
  width: 100%;
  padding: 14px 14px;
  border-radius: 10px;
  border: 1px solid var(--border);
  background: #f7f9fc;
  font-size: 15px;
  transition:
    border-color 0.18s,
    box-shadow 0.18s;
}
.input:focus {
  outline: none;
  border-color: var(--primary);
  box-shadow: 0 0 0 3px rgba(33, 164, 90, 0.15);
}
.btn {
  width: 100%;
  padding: 13px;
  border: none;
  border-radius: 10px;
  background: var(--primary);
  color: #fff;
  font-size: 15px;
  font-weight: 700;
  cursor: pointer;
  transition:
    background 0.15s ease,
    transform 0.05s;
  margin-top: 4px;
}
.btn:hover {
  background: var(--primary-dark);
}
.btn:active {
  transform: translateY(1px);
}
.btn:disabled {
  opacity: 0.7;
  cursor: default;
}
.error {
  margin: 8px 0 0;
  padding: 10px 12px;
  background: #fef2f2;
  color: #b91c1c;
  border: 1px solid #fecdd3;
  border-radius: 10px;
  font-size: 14px;
  text-align: left;
}
.footer {
  margin-top: 18px;
  font-size: 12px;
  color: var(--muted);
}
.footer span {
  margin: 0 4px;
  color: #21a45a;
  font-weight: 600;
}
</style>
