import { useEffect, useState } from 'react';

/** Returns `value`, delayed by `delayMs` after it last changed (J6: 500 ms preview, J14: 300 ms search). */
export function useDebouncedValue<T>(value: T, delayMs: number): T {
  const [debounced, setDebounced] = useState(value);

  useEffect(() => {
    const timer = setTimeout(() => setDebounced(value), delayMs);
    return () => clearTimeout(timer);
  }, [value, delayMs]);

  return debounced;
}
