import { Button } from '@/components/ui/button';

export function LoginPage() {
  return (
    <form className="max-w-sm space-y-4" onSubmit={(e) => e.preventDefault()}>
      <h1 className="text-xl font-semibold">Sign in</h1>
      <label className="block text-sm">
        Email
        <input name="email" type="email" className="mt-1 w-full rounded-md border px-3 py-2" />
      </label>
      <label className="block text-sm">
        Password
        <input name="password" type="password" className="mt-1 w-full rounded-md border px-3 py-2" />
      </label>
      <Button type="submit" disabled>
        Sign in (available after Tenancy lands)
      </Button>
    </form>
  );
}
