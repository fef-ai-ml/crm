import { render, screen } from '@testing-library/react';
import { Select } from './select';

const OPTIONS = [
  { value: 'and', label: 'And' },
  { value: 'or', label: 'Or' },
];

describe('Select', () => {
  it('is reachable by its label and renders every option', () => {
    render(
      <>
        <label htmlFor="op">Operator</label>
        <Select id="op" options={OPTIONS} defaultValue="or" />
      </>,
    );
    const select = screen.getByLabelText('Operator') as HTMLSelectElement;
    expect(select.value).toBe('or');
    expect(screen.getAllByRole('option')).toHaveLength(2);
  });

  it('forwards aria-label, disabled and caller classes', () => {
    render(<Select aria-label="Comparator" options={OPTIONS} disabled className="w-40" />);
    const select = screen.getByLabelText('Comparator') as HTMLSelectElement;
    expect(select.disabled).toBe(true);
    expect(select.className).toContain('w-40');
    expect(select.className).toContain('rounded-md');
  });
});
