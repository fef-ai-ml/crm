import { render, screen } from '@testing-library/react';
import { Button } from './button';

describe('Button', () => {
  it('renders children and applies variant classes', () => {
    render(<Button variant="outline">Save</Button>);
    const button = screen.getByRole('button', { name: 'Save' });
    expect(button.className).toContain('border');
  });

  it('is disabled when disabled', () => {
    render(<Button disabled>Run</Button>);
    expect((screen.getByRole('button', { name: 'Run' }) as HTMLButtonElement).disabled).toBe(true);
  });
});
