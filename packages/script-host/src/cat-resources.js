/** Track reachable native objects without keeping an otherwise collectable
 * in-memory stream alive. OS-backed resources have their own native owners;
 * explicit shutdown still visits every live object and waits for real close. */
export class NativeResources {
  constructor() {
    this.entriesByObject = new WeakMap();
    this.entriesByReference = new Map();
    this.collected = new FinalizationRegistry(reference => {
      const entry = this.entriesByReference.get(reference);
      this.entriesByReference.delete(reference);
      entry?.onCollect?.();
    });
  }
  set(value, metadata, onCollect) {
    const entry = this.entriesByObject.get(value);
    if (entry) { entry.metadata = metadata; return this; }
    const reference = new WeakRef(value);
    const next = { reference, metadata, onCollect };
    this.entriesByObject.set(value, next);
    this.entriesByReference.set(reference, next);
    this.collected.register(value, reference, reference);
    return this;
  }
  add(value) { return this.set(value, true); }
  get(value) { return this.entriesByObject.get(value)?.metadata; }
  has(value) { return this.entriesByObject.has(value); }
  delete(value) {
    const entry = this.entriesByObject.get(value);
    if (!entry) return false;
    this.entriesByObject.delete(value);
    this.entriesByReference.delete(entry.reference);
    this.collected.unregister(entry.reference);
    return true;
  }
  *entries() {
    for (const [reference, entry] of this.entriesByReference) {
      const value = reference.deref();
      if (value) yield [value, entry.metadata];
    }
  }
  *keys() { for (const [value] of this.entries()) yield value; }
  [Symbol.iterator]() { return this.entries(); }
  get size() {
    let count = 0;
    for (const _entry of this.entries()) count++;
    return count;
  }
  clear() {
    for (const reference of this.entriesByReference.keys()) this.collected.unregister(reference);
    this.entriesByReference.clear();
    this.entriesByObject = new WeakMap();
  }
}
