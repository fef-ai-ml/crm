import * as React from 'react';
import { cn } from '@/lib/utils';

export type InputProps = React.ComponentProps<'input'>;

export function Input({ className, type = 'text', ...props }: InputProps) {
  return (
    <input
      type={type}
      className={cn(
        'flex h-9 w-full rounded-md border border-zinc-300 bg-transparent px-3 py-1 text-sm shadow-sm transition-colors placeholder:text-zinc-400 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-zinc-400 disabled:cursor-not-allowed disabled:opacity-50 dark:border-zinc-700',
        className,
      )}
      {...props}
    />
  );
}
