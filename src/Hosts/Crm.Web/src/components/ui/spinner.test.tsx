import { render, screen } from '@testing-library/react';
import { Spinner } from './spinner';

describe('Spinner', () => {
  it('renders a labelled loading indicator', () => {
    render(<Spinner />);
    expect(screen.getByLabelText('Loading')).toBeTruthy();
  });

  it('keeps caller classes', () => {
    render(<Spinner className="h-8 w-8" />);
    expect(screen.getByLabelText('Loading').className).toContain('h-8');
  });
});
