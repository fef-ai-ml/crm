import { render, screen } from '@testing-library/react';
import { LoginPage } from './login';

describe('LoginPage', () => {
  it('labels both fields and keeps submit disabled until the Web plan wires auth', () => {
    render(<LoginPage />);
    expect((screen.getByLabelText('Email') as HTMLInputElement).type).toBe('email');
    expect((screen.getByLabelText('Password') as HTMLInputElement).type).toBe('password');
    expect((screen.getByRole('button', { name: /Sign in/ }) as HTMLButtonElement).disabled).toBe(
      true,
    );
  });
});
