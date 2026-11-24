const UNSET = Symbol('unset');

/**
 * Runs an async worker over items with a bounded concurrency limit.
 * Stops pulling new items once `shouldContinue()` returns false or on first error,
 * while always waiting for all already-started in-flight workers to drain before settling.
 */
export async function mapWithConcurrency(items, limit, worker, { shouldContinue, stopOnError = true } = {}) {
  if (!items.length) return [];
  const results = new Array(items.length).fill(UNSET);
  let nextIndex = 0;
  let firstError;
  let hasError = false;
  const workerCount = Math.max(1, Math.min(limit, items.length));

  const runWorker = async () => {
    while (true) {
      if (stopOnError && hasError) return;
      if (shouldContinue && !shouldContinue()) return;
      const index = nextIndex++;
      if (index >= items.length) return;
      try {
        results[index] = await worker(items[index], index);
      } catch (error) {
        if (!hasError) {
          hasError = true;
          firstError = error;
        }
      }
    }
  };

  await Promise.all(Array.from({ length: workerCount }, runWorker));
  if (hasError) throw firstError;
  const dense = [];
  for (let i = 0; i < results.length; i++) {
    if (results[i] !== UNSET) dense.push(results[i]);
  }
  return dense;
}
