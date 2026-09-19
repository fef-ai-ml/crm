import * as React from 'react';
import { cn } from '@/lib/utils';

export function Card({ className, ...props }: React.ComponentProps<'div'>) {
  return (
    <div
      className={cn(
        'rounded-lg border border-zinc-200 bg-white shadow-sm dark:border-zinc-800 dark:bg-zinc-900',
        className,
      )}
      {...props}
    />
  );
}

export function CardHeader({ className, ...props }: React.ComponentProps<'div'>) {
  return <div className={cn('flex flex-col gap-1.5 p-6 pb-3', className)} {...props} />;
}

export type CardTitleProps = React.ComponentProps<'h2'> & { as?: 'h1' | 'h2' | 'h3' };

export function CardTitle({ as: Heading = 'h2', className, ...props }: CardTitleProps) {
  return (
    <Heading
      className={cn('text-xl font-semibold leading-none tracking-tight', className)}
      {...props}
    />
  );
}

export function CardContent({ className, ...props }: React.ComponentProps<'div'>) {
  return <div className={cn('p-6 pt-3', className)} {...props} />;
}
