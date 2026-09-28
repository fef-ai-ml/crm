import { cn } from '@/lib/utils';

export type SpinnerProps = { className?: string };

/** J13: a hand-rolled primitive, not `window.confirm`-style browser chrome. `role="status"` plus the label is
 * what screen readers announce; the spin itself is decorative. */
export function Spinner({ className }: SpinnerProps) {
  return (
    <span
      role="status"
      aria-label="Loading"
      className={cn(
        'inline-block h-4 w-4 animate-spin rounded-full border-2 border-zinc-300 border-t-zinc-900 dark:border-zinc-700 dark:border-t-zinc-100',
        className,
      )}
    />
  );
}
