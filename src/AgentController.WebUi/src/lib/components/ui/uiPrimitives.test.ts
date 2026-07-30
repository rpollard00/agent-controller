import { fireEvent, render, screen } from '@testing-library/svelte';
import { describe, expect, it, vi } from 'vitest';
import Pagination from './Pagination.svelte';
import UiPrimitivesHarness from './UiPrimitivesHarness.svelte';

describe('Dialog', () => {
  it('keeps the existing compact dialog presentation and actions', async () => {
    render(UiPrimitivesHarness);

    await fireEvent.click(screen.getByRole('button', { name: 'Open diagnostics' }));

    const dialog = screen.getByRole('dialog', { name: 'Delete item?' });
    expect(dialog).toHaveAttribute('aria-describedby');
    expect(dialog.className).toContain('32rem');
    expect(screen.getByText('This action cannot be undone.')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Cancel' })).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Close dialog' })).not.toBeInTheDocument();
  });

  it('renders a bounded wide dialog with context, close control, and external link', async () => {
    render(UiPrimitivesHarness, { variant: 'wide', withExternalLink: true });
    await fireEvent.click(screen.getByRole('button', { name: 'Open diagnostics' }));

    const dialog = screen.getByRole('dialog', { name: 'Pickup diagnostics' });
    expect(dialog.className).toContain('max-h-[calc(100dvh-2rem)]');
    expect(dialog.className).toContain('64rem');
    expect(dialog).toHaveAccessibleDescription('Work item #42 · Project');
    expect(screen.getByRole('button', { name: 'Close dialog' })).toBeVisible();
    expect(screen.getByRole('link', { name: /View item/ })).toHaveAttribute(
      'href',
      'https://example.test/item/42',
    );
  });

  it('contains keyboard focus, closes with Escape, and restores trigger focus', async () => {
    render(UiPrimitivesHarness, { variant: 'wide' });
    const trigger = screen.getByRole('button', { name: 'Open diagnostics' });
    trigger.focus();
    await fireEvent.click(trigger);

    const close = screen.getByRole('button', { name: 'Close dialog' });
    const last = screen.getByRole('button', { name: 'Delete' });
    last.focus();
    await fireEvent.keyDown(last, { key: 'Tab' });
    expect(close).toHaveFocus();

    await fireEvent.keyDown(close, { key: 'Tab', shiftKey: true });
    expect(last).toHaveFocus();

    await fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Escape' });
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(trigger).toHaveFocus();
  });

  it('presents loading and retryable error states without diagnostic content', async () => {
    const view = render(UiPrimitivesHarness, { variant: 'wide', loading: true });
    await fireEvent.click(screen.getByRole('button', { name: 'Open diagnostics' }));
    expect(screen.getByRole('status')).toHaveTextContent('Loading diagnostics…');
    expect(screen.queryByText('Diagnostic content')).not.toBeInTheDocument();

    view.unmount();
    render(UiPrimitivesHarness, { variant: 'wide', error: 'Diagnostics could not be loaded.' });
    await fireEvent.click(screen.getByRole('button', { name: 'Open diagnostics' }));
    expect(screen.getByRole('alert')).toHaveTextContent('Diagnostics could not be loaded.');
    await fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(screen.getByTestId('retry-count')).toHaveTextContent('1');
  });
});

describe('Pagination', () => {
  it('shows the current range and moves in either direction', async () => {
    const onprevious = vi.fn();
    const onnext = vi.fn();
    render(Pagination, { page: 2, pageSize: 50, total: 183, onprevious, onnext });

    expect(screen.getByText('51–100 of 183')).toBeVisible();
    await fireEvent.click(screen.getByRole('button', { name: 'Previous' }));
    await fireEvent.click(screen.getByRole('button', { name: 'Next' }));
    expect(onprevious).toHaveBeenCalledOnce();
    expect(onnext).toHaveBeenCalledOnce();
  });

  it('disables controls at pagination boundaries', () => {
    const callbacks = { onprevious: vi.fn(), onnext: vi.fn() };
    const first = render(Pagination, { page: 1, pageSize: 50, total: 183, ...callbacks });
    expect(screen.getByText('1–50 of 183')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Previous' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Next' })).toBeEnabled();

    first.unmount();
    render(Pagination, { page: 4, pageSize: 50, total: 183, ...callbacks });
    expect(screen.getByText('151–183 of 183')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Previous' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled();
  });

  it('announces an empty range and disables both controls', () => {
    render(Pagination, {
      page: 1,
      pageSize: 50,
      total: 0,
      onprevious: vi.fn(),
      onnext: vi.fn(),
    });

    expect(screen.getByText('0–0 of 0')).toHaveAttribute('aria-live', 'polite');
    expect(screen.getByRole('button', { name: 'Previous' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled();
  });
});
