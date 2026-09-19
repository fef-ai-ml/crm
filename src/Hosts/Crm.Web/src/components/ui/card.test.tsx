import { render, screen } from '@testing-library/react';
import { Card, CardContent, CardHeader, CardTitle } from './card';

describe('Card', () => {
  it('renders the title as a level-2 heading by default and shows its content', () => {
    render(
      <Card>
        <CardHeader>
          <CardTitle>Platform</CardTitle>
        </CardHeader>
        <CardContent>body text</CardContent>
      </Card>,
    );
    expect(screen.getByRole('heading', { level: 2, name: 'Platform' })).toBeTruthy();
    expect(screen.getByText('body text')).toBeTruthy();
  });

  it('lets a page promote the title to its h1', () => {
    render(<CardTitle as="h1">Sign in</CardTitle>);
    expect(screen.getByRole('heading', { level: 1, name: 'Sign in' })).toBeTruthy();
  });
});
