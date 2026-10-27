import 'vite/modulepreload-polyfill';
import React from 'react';
import ReactDOM from 'react-dom/client';
import { AuthPage } from './AuthPage';
import type { AuthPageState } from './types';
import '../styles/fields.css';

const rootElement = document.getElementById('root');
const stateElement = document.getElementById('auth-page-state');

if (!rootElement || !stateElement?.textContent) {
  throw new Error('Auth page bootstrap state is missing.');
}

const initialState = JSON.parse(stateElement.textContent) as AuthPageState;

ReactDOM.createRoot(rootElement).render(
  <React.StrictMode>
    <AuthPage initialState={initialState} />
  </React.StrictMode>,
);
