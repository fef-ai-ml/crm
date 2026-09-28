/** A JSON string of `value` with every object's keys sorted recursively, so two rule trees that differ only in
 * key order (or two edits that leave the tree structurally identical) produce the same string — used as the
 * TanStack Query key for the live preview (J6) so identical trees share one cached request. */
export function canonicalJson(value: unknown): string {
  return JSON.stringify(sortForCanonicalJson(value));
}

function sortForCanonicalJson(value: unknown): unknown {
  if (Array.isArray(value)) {
    return value.map(sortForCanonicalJson);
  }
  if (value !== null && typeof value === 'object') {
    const sorted: Record<string, unknown> = {};
    for (const key of Object.keys(value as Record<string, unknown>).sort()) {
      sorted[key] = sortForCanonicalJson((value as Record<string, unknown>)[key]);
    }
    return sorted;
  }
  return value;
}
