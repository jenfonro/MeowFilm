export function initMainInteractions() {
  const sidebarToggle = document.getElementById('sidebarToggleBtn');
  const sidebar = document.getElementById('desktopSidebar');
  const sidebarOffset = document.getElementById('sidebarOffset');
  if (sidebarToggle && sidebar && sidebarOffset) {
    const logoRow = sidebar.querySelector('.logo-row');
    const logoText = sidebar.querySelector('.logo-text');
    const logoImg = sidebar.querySelector('.logo-mark img');
    const brandBox = sidebar.querySelector('.brand-box');
    const navTextSpans = sidebar.querySelectorAll('.nav-label');
    const syncCollapsed = (collapsed) => {
      sidebarOffset.classList.toggle('collapsed', collapsed);
      [logoRow, logoText, logoImg, brandBox].forEach((el) => {
        if (el) el.style.display = collapsed ? 'none' : '';
      });
      navTextSpans.forEach((s) => (s.style.display = collapsed ? 'none' : ''));
    };
    sidebarToggle.addEventListener('click', () => syncCollapsed(sidebar.classList.toggle('collapsed')));
  }

  const segButtons = document.querySelectorAll('.seg-toggle .seg-btn');
  if (segButtons.length) {
    segButtons.forEach((btn) => {
      btn.addEventListener('click', () => {
        segButtons.forEach((b) => b.classList.toggle('active', b === btn));
      });
    });
  }

  const themeBtn = document.getElementById('themeToggleBtn');
  const sunIcon = document.querySelector('#themeToggleBtn .sun');
  const moonIcon = document.querySelector('#themeToggleBtn .moon');
  if (themeBtn) {
    const STORAGE_KEY = 'meowfilm_theme';
    const applyTheme = (dark) => {
      document.documentElement.classList.toggle('dark', dark);
      document.body.classList.toggle('dark', dark);
      if (sunIcon && moonIcon) {
        sunIcon.classList.toggle('hidden', !dark);
        moonIcon.classList.toggle('hidden', dark);
      }
      localStorage.setItem(STORAGE_KEY, dark ? 'dark' : 'light');
    };
    const saved = localStorage.getItem(STORAGE_KEY);
    const initialDark = saved === 'dark';
    applyTheme(initialDark);
    themeBtn.addEventListener('click', () => {
      const darkNow = !document.documentElement.classList.contains('dark');
      applyTheme(darkNow);
    });
  }
}
