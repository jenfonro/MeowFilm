import { createApp } from 'vue';
import IndexPage from './IndexPage.vue';
import { fetchBootstrap } from '../../shared/bootstrap';

(async () => {
  const bootstrap = await fetchBootstrap('index');
  if (bootstrap && bootstrap.siteName) document.title = bootstrap.siteName;
  createApp(IndexPage, { bootstrap }).mount('#app');
})();
