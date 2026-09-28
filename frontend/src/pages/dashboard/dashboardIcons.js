import { h } from 'vue';

function createIcon(className, width, height, children) {
  return {
    inheritAttrs: true,
    render() {
      return h(
        'svg',
        {
          xmlns: 'http://www.w3.org/2000/svg',
          width,
          height,
          viewBox: '0 0 24 24',
          fill: 'none',
          stroke: 'currentColor',
          'stroke-width': '2',
          'stroke-linecap': 'round',
          'stroke-linejoin': 'round',
          class: className
        },
        children
      );
    }
  };
}

export const SettingsIcon = createIcon('lucide lucide-settings', 18, 18, [
  h('path', { d: 'M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z' }),
  h('circle', { cx: '12', cy: '12', r: '3' })
]);

export const UserIcon = createIcon('lucide lucide-users', 20, 20, [
  h('path', { d: 'M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2' }),
  h('circle', { cx: '9', cy: '7', r: '4' }),
  h('path', { d: 'M22 21v-2a4 4 0 0 0-3-3.87' }),
  h('path', { d: 'M16 3.13a4 4 0 0 1 0 7.75' })
]);

export const CloudIcon = createIcon('lucide lucide-cloud', 20, 20, [
  h('path', { d: 'M17.5 19H9a7 7 0 1 1 6.71-9h.79a4.5 4.5 0 1 1 1 9Z' })
]);

export const LinkIcon = createIcon('lucide lucide-link-2', 20, 20, [
  h('path', { d: 'M15 7h3a5 5 0 0 1 0 10h-3' }),
  h('path', { d: 'M9 17H6a5 5 0 0 1 0-10h3' }),
  h('line', { x1: '8', x2: '16', y1: '12', y2: '12' })
]);

export const FilmIcon = createIcon('lucide lucide-film', 20, 20, [
  h('rect', { width: '18', height: '18', x: '3', y: '3', rx: '2' }),
  h('path', { d: 'M7 3v18' }),
  h('path', { d: 'M3 7.5h4' }),
  h('path', { d: 'M3 12h18' }),
  h('path', { d: 'M3 16.5h4' }),
  h('path', { d: 'M17 3v18' }),
  h('path', { d: 'M17 7.5h4' }),
  h('path', { d: 'M17 16.5h4' })
]);

export const SparklesIcon = createIcon('lucide lucide-sparkles', 20, 20, [
  h('path', { d: 'M9.937 15.5A2 2 0 0 0 8.5 14.063l-6.135-1.582a.5.5 0 0 1 0-.962L8.5 9.937A2 2 0 0 0 9.937 8.5l1.582-6.135a.5.5 0 0 1 .962 0L14.063 8.5A2 2 0 0 0 15.5 9.937l6.135 1.582a.5.5 0 0 1 0 .962L15.5 14.063a2 2 0 0 0-1.437 1.437l-1.582 6.135a.5.5 0 0 1-.962 0z' }),
  h('path', { d: 'M20 3v4' }),
  h('path', { d: 'M22 5h-4' }),
  h('path', { d: 'M4 17v2' }),
  h('path', { d: 'M5 18H3' })
]);

export const DatabaseIcon = createIcon('lucide lucide-database', 20, 20, [
  h('ellipse', { cx: '12', cy: '5', rx: '9', ry: '3' }),
  h('path', { d: 'M3 5v14c0 1.66 4.03 3 9 3s9-1.34 9-3V5' }),
  h('path', { d: 'M3 12c0 1.66 4.03 3 9 3s9-1.34 9-3' })
]);

export const WandIcon = createIcon('lucide lucide-wand-2', 20, 20, [
  h('path', { d: 'm21.64 3.64-1.28-1.28a1.21 1.21 0 0 0-1.72 0L2 18.99V22h3.01L21.64 5.36a1.21 1.21 0 0 0 0-1.72Z' }),
  h('path', { d: 'm14 7 3 3' }),
  h('path', { d: 'M5 6v4' }),
  h('path', { d: 'M19 14v4' }),
  h('path', { d: 'M10 2v2' }),
  h('path', { d: 'M7 8H3' }),
  h('path', { d: 'M21 16h-4' }),
  h('path', { d: 'M11 3H9' })
]);

export const PanelsIcon = createIcon('lucide lucide-panels-top-left', 20, 20, [
  h('rect', { width: '18', height: '18', x: '3', y: '3', rx: '2' }),
  h('path', { d: 'M3 9h18' }),
  h('path', { d: 'M9 21V9' })
]);
