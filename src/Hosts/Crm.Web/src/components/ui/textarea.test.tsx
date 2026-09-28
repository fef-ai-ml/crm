import { render, screen } from '@testing-library/react';
import { Textarea } from './textarea';

describe('Textarea', () => {
  it('is reachable by its label and keeps caller classes', () => {
    render(
      <>
        <label htmlFor="desc">Description</label>
        <Textarea id="desc" className="h-24" />
      </>,
    );
    const textarea = screen.getByLabelText('Description') as HTMLTextAreaElement;
    expect(textarea.tagName).toBe('TEXTAREA');
    expect(textarea.className).toContain('h-24');
    expect(textarea.className).toContain('rounded-md');
  });

  it('forwards disabled', () => {
    render(<Textarea aria-label="Notes" disabled />);
    const textarea = screen.getByLabelText('Notes') as HTMLTextAreaElement;
    expect(textarea.disabled).toBe(true);
  });
});
