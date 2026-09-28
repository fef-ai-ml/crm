import { render } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { Dialog } from './dialog';

describe('Dialog', () => {
  let showModal: ReturnType<typeof vi.fn<() => void>>;
  let close: ReturnType<typeof vi.fn<(returnValue?: string) => void>>;

  beforeEach(() => {
    // jsdom's <dialog> support is partial; stub the two imperative methods the wrapper drives.
    showModal = vi.fn();
    close = vi.fn();
    HTMLDialogElement.prototype.showModal = showModal;
    HTMLDialogElement.prototype.close = close;
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('does not call showModal or close before it is ever opened', () => {
    render(
      <Dialog open={false} onClose={() => {}} label="Test dialog">
        <p>Hi</p>
      </Dialog>,
    );
    expect(showModal).not.toHaveBeenCalled();
    expect(close).not.toHaveBeenCalled();
  });

  it('calls showModal when `open` turns true, and close when it turns false again', () => {
    const { rerender } = render(
      <Dialog open={false} onClose={() => {}} label="Test dialog">
        <p>Hi</p>
      </Dialog>,
    );

    rerender(
      <Dialog open onClose={() => {}} label="Test dialog">
        <p>Hi</p>
      </Dialog>,
    );
    expect(showModal).toHaveBeenCalledTimes(1);
    expect(close).not.toHaveBeenCalled();

    rerender(
      <Dialog open={false} onClose={() => {}} label="Test dialog">
        <p>Hi</p>
      </Dialog>,
    );
    expect(close).toHaveBeenCalledTimes(1);
  });

  it('calls onClose when the native dialog fires a close event (e.g. Escape)', () => {
    const onClose = vi.fn();
    const { container, rerender } = render(
      <Dialog open={false} onClose={onClose} label="Test dialog">
        <p>Hi</p>
      </Dialog>,
    );
    rerender(
      <Dialog open onClose={onClose} label="Test dialog">
        <p>Hi</p>
      </Dialog>,
    );

    const dialog = container.querySelector('dialog');
    dialog?.dispatchEvent(new Event('close'));

    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('exposes `label` as the dialog\'s accessible name', () => {
    const { getByRole } = render(
      <Dialog open onClose={() => {}} label="Key created">
        <p>Hi</p>
      </Dialog>,
    );
    expect(getByRole('dialog', { name: 'Key created' })).toBeTruthy();
  });

  it('closes on unmount while still open, instead of leaving the native dialog open', () => {
    const { unmount } = render(
      <Dialog open onClose={() => {}} label="Test dialog">
        <p>Secret content</p>
      </Dialog>,
    );
    expect(showModal).toHaveBeenCalledTimes(1);
    expect(close).not.toHaveBeenCalled();

    unmount();

    expect(close).toHaveBeenCalledTimes(1);
  });

  it('does not call close on unmount when it was never opened', () => {
    const { unmount } = render(
      <Dialog open={false} onClose={() => {}} label="Test dialog">
        <p>Hi</p>
      </Dialog>,
    );

    unmount();

    expect(close).not.toHaveBeenCalled();
  });
});
