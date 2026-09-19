import { render, screen } from '@testing-library/react';
import { Input } from './input';

describe('Input', () => {
  it('is reachable by its label and keeps caller classes', () => {
    render(
      <>
        <label htmlFor="email">Email</label>
        <Input id="email" type="email" className="w-40" />
      </>,
    );
    const input = screen.getByLabelText('Email') as HTMLInputElement;
    expect(input.type).toBe('email');
    expect(input.className).toContain('w-40');
    expect(input.className).toContain('rounded-md');
  });

  it('defaults to a text input and forwards disabled', () => {
    render(<Input aria-label="Name" disabled />);
    const input = screen.getByLabelText('Name') as HTMLInputElement;
    expect(input.type).toBe('text');
    expect(input.disabled).toBe(true);
  });
});
