import type { KeyboardEvent, ReactNode } from 'react';
import { cn } from '@/lib/utils';

export type TabItem<T extends string> = { value: T; label: string };

export type TabsProps<T extends string> = {
  tabs: TabItem<T>[];
  value: T;
  onChange: (value: T) => void;
  className?: string;
};

/** J13: a hand-rolled accessible tabs primitive (`role="tablist"`/`"tab"`/`aria-selected`), same cva/`cn`
 * idiom as the other `components/ui` primitives. Arrow-key navigation is a nice-to-have the brief marks
 * optional; clicking is the path E2E and the unit tests exercise. */
export function Tabs<T extends string>({ tabs, value, onChange, className }: TabsProps<T>) {
  function handleKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    const index = tabs.findIndex((tab) => tab.value === value);
    if (index === -1) return;

    if (event.key === 'ArrowRight') {
      event.preventDefault();
      onChange(tabs[(index + 1) % tabs.length].value);
    } else if (event.key === 'ArrowLeft') {
      event.preventDefault();
      onChange(tabs[(index - 1 + tabs.length) % tabs.length].value);
    }
  }

  return (
    <div
      role="tablist"
      className={cn('flex gap-1 border-b border-zinc-200 dark:border-zinc-800', className)}
      onKeyDown={handleKeyDown}
    >
      {tabs.map((tab) => {
        const selected = tab.value === value;
        return (
          <button
            key={tab.value}
            type="button"
            role="tab"
            id={`tab-${tab.value}`}
            aria-selected={selected}
            aria-controls={`tabpanel-${tab.value}`}
            tabIndex={selected ? 0 : -1}
            className={cn(
              '-mb-px border-b-2 px-3 py-2 text-sm font-medium transition-colors',
              selected
                ? 'border-zinc-900 text-zinc-900 dark:border-zinc-100 dark:text-zinc-100'
                : 'border-transparent text-zinc-500 hover:text-zinc-900 dark:text-zinc-400 dark:hover:text-zinc-100',
            )}
            onClick={() => onChange(tab.value)}
          >
            {tab.label}
          </button>
        );
      })}
    </div>
  );
}

export type TabPanelProps<T extends string> = {
  value: T;
  active: T;
  children: ReactNode;
};

/** Unmounts inactive panels (rather than hiding with CSS) so an inactive Runs/Members tab holds no live
 * query subscription and does no polling or fetching. */
export function TabPanel<T extends string>({ value, active, children }: TabPanelProps<T>) {
  if (value !== active) return null;
  return (
    <div role="tabpanel" id={`tabpanel-${value}`} aria-labelledby={`tab-${value}`}>
      {children}
    </div>
  );
}
