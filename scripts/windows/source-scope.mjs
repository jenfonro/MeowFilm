// Windows-owned trees and shared Node tools consumed by its build/test entries.
// iOS templates are checked in their generated runtime, not as Windows sources.
export const sourceDirectories = ['apps/windows', 'apps/runtime', 'packages', 'scripts/windows', 'scripts/quickjs'];

export function isWindowsSource(path) {
  if (/(?:^|\/)(?:bin|obj|node_modules|\.git)(?:\/|$)/.test(path) ||
      /^packages\/script-host\/build(?:\/|$)/.test(path)) return false;
  return sourceDirectories.some(directory => path.startsWith(directory + '/')) || /^scripts\/[^/]+\.mjs$/.test(path);
}
