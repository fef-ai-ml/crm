import { render, screen } from '@testing-library/react';
import { Badge } from './badge';

describe('Badge', () => {
  it('renders children and applies variant classes', () => {
    render(<Badge variant="success">Real-time</Badge>);
    const badge = screen.getByText('Real-time');
    expect(badge.className).toContain('emerald');
  });

  it('defaults to the default variant and keeps caller classes', () => {
    render(<Badge className="ml-2">Batch</Badge>);
    const badge = screen.getByText('Batch');
    expect(badge.className).toContain('ml-2');
    expect(badge.className).toContain('rounded-full');
  });
});
