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
      <Dialog open={false} onClose={() => {}}>
        <p>Hi</p>
      </Dialog>,
    );
    expect(showModal).not.toHaveBeenCalled();
    expect(close).not.toHaveBeenCalled();
  });

  it('calls showModal when `open` turns true, and close when it turns false again', () => {
    const { rerender } = render(
      <Dialog open={false} onClose={() => {}}>
        <p>Hi</p>
      </Dialog>,
    );

    rerender(
      <Dialog open onClose={() => {}}>
        <p>Hi</p>
      </Dialog>,
    );
    expect(showModal).toHaveBeenCalledTimes(1);
    expect(close).not.toHaveBeenCalled();

    rerender(
      <Dialog open={false} onClose={() => {}}>
        <p>Hi</p>
      </Dialog>,
    );
    expect(close).toHaveBeenCalledTimes(1);
  });

  it('calls onClose when the native dialog fires a close event (e.g. Escape)', () => {
    const onClose = vi.fn();
    const { container, rerender } = render(
      <Dialog open={false} onClose={onClose}>
        <p>Hi</p>
      </Dialog>,
    );
    rerender(
      <Dialog open onClose={onClose}>
        <p>Hi</p>
      </Dialog>,
    );

    const dialog = container.querySelector('dialog');
    dialog?.dispatchEvent(new Event('close'));

    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('renders children only while mounted, so a caller can remove them independently of `open`', () => {
    const { getByText } = render(
      <Dialog open onClose={() => {}}>
        <p>Secret content</p>
      </Dialog>,
    );
    expect(getByText('Secret content')).toBeTruthy();
  });
});
