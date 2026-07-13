import './app.css';
import { mount } from 'svelte';
import App from './App.svelte';

const target = document.getElementById('app');

if (!target) {
  throw new Error('Unable to find the application mount point.');
}

mount(App, { target });
