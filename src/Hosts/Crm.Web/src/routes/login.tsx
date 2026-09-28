import { useState, type FormEvent } from 'react';
import { useRouter, useSearch } from '@tanstack/react-router';
import { ApiError } from '@/lib/api';
import { login, type Session } from '@/lib/auth';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Input } from '@/components/ui/input';

export type LoginFormProps = {
  onSuccess: (session: Session) => void;
};

function problemMessage(error: unknown): string {
  if (error instanceof ApiError) {
    const fieldErrors = error.problem?.errors;
    if (fieldErrors) {
      return Object.entries(fieldErrors)
        .map(([field, messages]) => `${field} ${messages.join(', ')}`)
        .join('; ');
    }
    return error.problem?.detail ?? 'Sign in failed.';
  }
  return 'Sign in failed.';
}

export function LoginForm({ onSuccess }: LoginFormProps) {
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setError(null);
    setSubmitting(true);
    try {
      const session = await login(email, password);
      onSuccess(session);
    } catch (err) {
      setError(problemMessage(err));
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <Card className="max-w-sm">
      <CardHeader>
        <CardTitle as="h1">Sign in</CardTitle>
      </CardHeader>
      <CardContent>
        <form className="space-y-4" onSubmit={(event) => void handleSubmit(event)}>
          <div className="space-y-1 text-sm">
            <label htmlFor="email">Email</label>
            <Input
              id="email"
              name="email"
              type="email"
              autoComplete="username"
              value={email}
              onChange={(event) => setEmail(event.target.value)}
            />
          </div>
          <div className="space-y-1 text-sm">
            <label htmlFor="password">Password</label>
            <Input
              id="password"
              name="password"
              type="password"
              autoComplete="current-password"
              value={password}
              onChange={(event) => setPassword(event.target.value)}
            />
          </div>
          {error ? (
            <p role="alert" className="text-sm text-red-600">
              {error}
            </p>
          ) : null}
          <Button type="submit" disabled={submitting}>
            Sign in
          </Button>
        </form>
      </CardContent>
    </Card>
  );
}

function isSafeRedirectTarget(target: string | undefined): target is string {
  return typeof target === 'string' && target.startsWith('/') && !target.startsWith('//');
}

export function LoginPage() {
  const router = useRouter();
  const search = useSearch({ from: '/login' });

  return (
    <div className="flex min-h-screen items-center justify-center p-6">
      <LoginForm
        onSuccess={() => {
          const target = isSafeRedirectTarget(search.redirect) ? search.redirect : '/segments';
          router.history.push(target);
        }}
      />
    </div>
  );
}
