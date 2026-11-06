// Windows-owned trees and shared Node tools consumed by its build/test entries.
// iOS templates are checked in their generated runtime, not as Windows sources.
export const sourceDirectories = ['apps/windows', 'apps/runtime', 'packages', 'scripts/windows', 'scripts/quickjs'];
export const sharedScripts = [
  'scripts/accept-reader-sources.mjs',
  'scripts/accept-sources.mjs',
  'scripts/package-runtime.mjs',
  'scripts/run-tests.mjs',
  'scripts/source-acceptance-diagnostics.mjs',
  'scripts/test-packages.mjs',
  'scripts/test-root.mjs',
  'scripts/verify-boundaries.mjs',
  'scripts/verify-dependency-lock.mjs',
  'scripts/verify-native-javascript.mjs',
];

export function isWindowsSource(path) {
  return sourceDirectories.some(directory => path.startsWith(directory + '/')) || sharedScripts.includes(path);
}
