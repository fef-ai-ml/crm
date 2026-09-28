import { useEffect, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { ApiError } from '@/lib/api';
import { api } from '@/lib/client';
import { useDebouncedValue } from '@/lib/useDebouncedValue';
import { validateRule } from '@/rules/validate';
import type { RuleTree } from '@/rules/types';
import { canonicalJson } from './canonical';

const PREVIEW_DEBOUNCE_MS = 500;
const CACHE_AGE_MS = 2000;

/** `'error'` extends the four states the brief names (J6): a network/500 failure needs its own rendering
 * (`error` carries the message), distinct from `timedOut` (a clean 5 s server response of `{ count: null,
 * timedOut: true }`) — decision not dictated by the brief, called out in the report. */
export type LivePreviewStatus = 'invalid' | 'pending' | 'ready' | 'timedOut' | 'error';

export type LivePreviewResult = {
  status: LivePreviewStatus;
  count: number | null;
  cached: boolean;
  error: string | null;
};

/** Debounced, validity-gated live count for the segment builder (J6). The CURRENT tree's validity decides
 * `'invalid'` immediately (no waiting on the debounce for a rule the user is still typing); the DEBOUNCED
 * tree's canonical JSON is the query key, so two edits that end up structurally identical — same content,
 * different object — share one cached request within the 60 s `staleTime`. */
export function useLivePreview(tree: RuleTree): LivePreviewResult {
  const debouncedTree = useDebouncedValue(tree, PREVIEW_DEBOUNCE_MS);
  const isCurrentValid = validateRule(tree).length === 0;
  const isDebouncedValid = validateRule(debouncedTree).length === 0;

  const query = useQuery({
    queryKey: ['preview', canonicalJson(debouncedTree)],
    queryFn: () => api.segments.preview(debouncedTree),
    enabled: isDebouncedValid,
    staleTime: 60_000,
  });

  // `Date.now()` is impure and may not run during render (react-hooks/purity), and `react-hooks/set-state-in-
  // effect` wants any `setState` inside an effect to happen inside a callback an external system invokes, not
  // synchronously in the effect body — an interval is that external system; its first tick (scheduled via
  // `setTimeout(tick, 0)`, not called directly) both satisfies the lint rule and re-evaluates on a cadence so a
  // card left open ticks from "" to "cached" on its own, not just on the next unrelated re-render.
  const cachedAt = query.data?.cachedAt ?? null;
  const [isStale, setIsStale] = useState(false);

  useEffect(() => {
    if (cachedAt === null) return;
    const cachedAtMs = new Date(cachedAt).getTime();
    const tick = () => setIsStale(Date.now() - cachedAtMs > CACHE_AGE_MS);
    const immediate = setTimeout(tick, 0);
    const interval = setInterval(tick, 500);
    return () => {
      clearTimeout(immediate);
      clearInterval(interval);
    };
  }, [cachedAt]);

  const cached = cachedAt !== null && isStale;

  if (!isCurrentValid) {
    return { status: 'invalid', count: null, cached: false, error: null };
  }

  if (!isDebouncedValid || query.isPending) {
    return { status: 'pending', count: null, cached: false, error: null };
  }

  if (query.isError) {
    const message =
      query.error instanceof ApiError ? (query.error.problem?.detail ?? 'Preview failed') : 'Preview failed';
    return { status: 'error', count: null, cached: false, error: message };
  }

  const data = query.data;
  if (data.timedOut || data.count === null) {
    return { status: 'timedOut', count: null, cached: false, error: null };
  }

  return { status: 'ready', count: data.count, cached, error: null };
}
