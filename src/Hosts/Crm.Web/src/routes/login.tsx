import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Input } from '@/components/ui/input';

export function LoginPage() {
  return (
    <Card className="max-w-sm">
      <CardHeader>
        <CardTitle as="h1">Sign in</CardTitle>
      </CardHeader>
      <CardContent>
        <form className="space-y-4" onSubmit={(e) => e.preventDefault()}>
          <div className="space-y-1 text-sm">
            <label htmlFor="email">Email</label>
            <Input id="email" name="email" type="email" autoComplete="username" />
          </div>
          <div className="space-y-1 text-sm">
            <label htmlFor="password">Password</label>
            <Input id="password" name="password" type="password" autoComplete="current-password" />
          </div>
          <Button type="submit" disabled>
            Sign in (wired by the Web plan)
          </Button>
        </form>
      </CardContent>
    </Card>
  );
}
