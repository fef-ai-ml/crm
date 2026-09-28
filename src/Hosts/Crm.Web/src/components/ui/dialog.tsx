import { useEffect, useRef, type ReactNode } from 'react';
import { cn } from '@/lib/utils';

export type DialogProps = {
  open: boolean;
  /** Fired both for an explicit close (a caller-rendered "Close" button) and a native one (Escape triggers
   * the browser's default cancel → close sequence on a `<dialog>` opened with `showModal`). */
  onClose: () => void;
  children: ReactNode;
  className?: string;
};

/** J13: wraps the native `<dialog>` element rather than a hand-rolled overlay, so focus trapping, Escape-to-
 * close and the top-layer stacking all come from the platform. `open` is imperative on `<dialog>` — there is
 * no React prop for it — so an effect drives `showModal()`/`close()` off a controlled boolean; a ref tracks
 * what the dialog was last told to do (rather than reading back the DOM's own `.open`) since jsdom's stubs
 * (`dialog.test.tsx`) don't flip that property the way a real browser does. */
export function Dialog({ open, onClose, children, className }: DialogProps) {
  const ref = useRef<HTMLDialogElement>(null);
  const wasOpenRef = useRef(false);

  useEffect(() => {
    const dialog = ref.current;
    if (!dialog) return;

    if (open && !wasOpenRef.current) {
      dialog.showModal();
    } else if (!open && wasOpenRef.current) {
      dialog.close();
    }
    // A real browser's showModal()/close() already reflect this onto the `open` attribute; it is set
    // explicitly too because Testing Library treats a `<dialog>` without `open` as inaccessible, and jsdom's
    // stubbed showModal/close in unit tests (`dialog.test.tsx`) don't touch the attribute themselves.
    dialog.open = open;
    wasOpenRef.current = open;
  }, [open]);

  return (
    <dialog
      ref={ref}
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
