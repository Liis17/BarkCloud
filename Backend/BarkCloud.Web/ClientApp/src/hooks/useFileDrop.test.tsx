import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { useFileDrop } from './useFileDrop';

const file = new File(['content'], 'photo.jpg', { type: 'image/jpeg' });
const dataTransfer = {
  types: ['Files'],
  files: [file],
} as unknown as DataTransfer;

function Harness({ onFiles }: { onFiles: (files: File[], target: { id: string; label: string } | null) => void }) {
  const { over, target, dropHandlers, getTargetHandlers } = useFileDrop(onFiles);
  return (
    <div data-testid="root" {...dropHandlers}>
      <div data-testid="child">child</div>
      <div data-testid="target" {...getTargetHandlers({ id: 'folder-1', label: 'Документы' })}>target</div>
      <output data-testid="state">{over ? target?.label || 'over' : 'out'}</output>
    </div>
  );
}

describe('useFileDrop', () => {
  it('clears the overlay when the drag leaves the container', () => {
    const onFiles = vi.fn();
    render(<Harness onFiles={onFiles} />);
    const root = screen.getByTestId('root');

    fireEvent.dragEnter(root, { dataTransfer });
    expect(screen.getByTestId('state').textContent).toBe('over');

    fireEvent.dragLeave(root, { dataTransfer, relatedTarget: document.body });
    expect(screen.getByTestId('state').textContent).toBe('out');
  });

  it('does not clear the overlay while moving between children', () => {
    const onFiles = vi.fn();
    render(<Harness onFiles={onFiles} />);
    const root = screen.getByTestId('root');
    const child = screen.getByTestId('child');

    fireEvent.dragEnter(root, { dataTransfer });
    fireEvent.dragEnter(child, { dataTransfer, relatedTarget: root });
    fireEvent.dragLeave(child, { dataTransfer, relatedTarget: root });

    expect(screen.getByTestId('state').textContent).toBe('over');
  });

  it('clears on window dragend and browser blur', () => {
    const onFiles = vi.fn();
    render(<Harness onFiles={onFiles} />);
    const root = screen.getByTestId('root');

    fireEvent.dragEnter(root, { dataTransfer });
    fireEvent(window, new Event('dragend'));
    expect(screen.getByTestId('state').textContent).toBe('out');

    fireEvent.dragEnter(root, { dataTransfer });
    fireEvent(window, new Event('blur'));
    expect(screen.getByTestId('state').textContent).toBe('out');
  });

  it('clears on an external document dragleave', () => {
    const onFiles = vi.fn();
    render(<Harness onFiles={onFiles} />);
    const root = screen.getByTestId('root');

    fireEvent.dragEnter(root, { dataTransfer });
    fireEvent(document, new Event('dragleave'));

    expect(screen.getByTestId('state').textContent).toBe('out');
  });

  it('calls a folder target once and keeps its target metadata', () => {
    const onFiles = vi.fn();
    render(<Harness onFiles={onFiles} />);
    const target = screen.getByTestId('target');

    fireEvent.dragEnter(target, { dataTransfer });
    fireEvent.drop(target, { dataTransfer });

    expect(onFiles).toHaveBeenCalledTimes(1);
    expect(onFiles).toHaveBeenCalledWith([file], { id: 'folder-1', label: 'Документы' });
    expect(screen.getByTestId('state').textContent).toBe('out');
  });
});
