import { useEffect, useRef, type ReactNode } from 'react';
import { cn } from '@/lib/utils';

export type DialogProps = {
  open: boolean;
  /** Fired both for an explicit close (a caller-rendered "Close" button) and a native one (Escape triggers
   * the browser's default cancel → close sequence on a `<dialog>` opened with `showModal`). */
  onClose: () => void;
  /** Accessible name for the native `<dialog>`, rendered as `aria-label`. Required so every dialog announces
   * itself to assistive tech — a visible heading inside it is not enough without an ARIA relationship. */
  label: string;
  children: ReactNode;
  className?: string;
};

/** J13: wraps the native `<dialog>` element rather than a hand-rolled overlay, so focus trapping, Escape-to-
 * close and the top-layer stacking all come from the platform. `open` is imperative on `<dialog>` — there is
 * no React prop for it — so an effect opens the dialog when `open` becomes true, and its cleanup closes it:
 * that covers both a normal `open → false` transition (the cleanup runs before the next effect invocation)
 * and an unmount while still open (the cleanup runs then too, instead of leaving the native dialog open with
 * nothing left to close it). `dialog.open` is read back and also set explicitly alongside the imperative
 * calls, because jsdom's stubbed `showModal`/`close` (`dialog.test.tsx`) don't touch that property the way a
 * real browser does, and Testing Library treats a `<dialog>` without the `open` attribute as inaccessible. */
export function Dialog({ open, onClose, label, children, className }: DialogProps) {
  const ref = useRef<HTMLDialogElement>(null);

  useEffect(() => {
    const dialog = ref.current;
    if (!dialog || !open) return;

    if (!dialog.open) {
      dialog.showModal();
      dialog.open = true;
    }

    return () => {
      if (dialog.open) {
        dialog.close();
        dialog.open = false;
      }
    };
  }, [open]);

  return (
    <dialog
      ref={ref}
      aria-label={label}
      onClose={onClose}
      className={cn(
        'rounded-lg border border-zinc-200 bg-white p-6 shadow-lg backdrop:bg-black/40 dark:border-zinc-800 dark:bg-zinc-900',
        className,
      )}
    >
      {children}
    </dialog>
  );
}
