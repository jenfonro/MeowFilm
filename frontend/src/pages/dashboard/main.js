import { createApp } from 'vue';
import DashboardPage from './DashboardPage.vue';
import { fetchBootstrap } from '../../shared/bootstrap';

(async () => {
  const bootstrap = await fetchBootstrap('dashboard');
  if (!bootstrap || !bootstrap.siteName || !bootstrap.authenticated) {
    window.location.href = '/';
    return;
  }
  document.title = `管理后台 - ${bootstrap.siteName}`;
  createApp(DashboardPage, { bootstrap }).mount('#app');
})();
