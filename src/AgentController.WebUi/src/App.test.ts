import { render, screen } from '@testing-library/svelte';
import { describe, expect, it } from 'vitest';
import App from './App.svelte';

describe('App', () => {
  it('renders the Agent Controller scaffold', () => {
    render(App);

    expect(screen.getByRole('heading', { level: 1, name: 'Agent Controller' })).toBeInTheDocument();
    expect(screen.getByText('Web UI scaffold ready')).toBeVisible();
  });
});
