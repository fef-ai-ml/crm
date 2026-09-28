import { useState } from 'react';
import { fireEvent, render, screen } from '@testing-library/react';
import { Tabs, TabPanel } from './tabs';

type TabValue = 'a' | 'b' | 'c';

function Harness() {
  const [value, setValue] = useState<TabValue>('a');
  return (
    <div>
      <Tabs
        value={value}
        onChange={setValue}
        tabs={[
          { value: 'a', label: 'First' },
          { value: 'b', label: 'Second' },
          { value: 'c', label: 'Third' },
        ]}
      />
      <TabPanel value="a" active={value}>
        panel-a
      </TabPanel>
      <TabPanel value="b" active={value}>
        panel-b
      </TabPanel>
      <TabPanel value="c" active={value}>
        panel-c
      </TabPanel>
    </div>
  );
}

describe('Tabs', () => {
  it('renders an accessible tablist and only the active panel', () => {
    render(<Harness />);

    expect(screen.getByRole('tablist')).toBeTruthy();
    const tabs = screen.getAllByRole('tab');
    expect(tabs.map((tab) => tab.textContent)).toEqual(['First', 'Second', 'Third']);
    expect(screen.getByRole('tab', { name: 'First' }).getAttribute('aria-selected')).toBe('true');
    expect(screen.getByRole('tab', { name: 'Second' }).getAttribute('aria-selected')).toBe('false');

    expect(screen.getByRole('tabpanel').textContent).toBe('panel-a');
  });

  it('pairs each tab with its panel via aria-controls/id and the panel back via aria-labelledby/id', () => {
    render(<Harness />);

    const tab = screen.getByRole('tab', { name: 'First' });
    const panel = screen.getByRole('tabpanel');
    expect(tab.getAttribute('aria-controls')).toBe(panel.id);
    expect(panel.getAttribute('aria-labelledby')).toBe(tab.id);
  });

  it('clicking a tab switches the selection and the visible panel', () => {
    render(<Harness />);

    fireEvent.click(screen.getByRole('tab', { name: 'Second' }));

    expect(screen.getByRole('tab', { name: 'Second' }).getAttribute('aria-selected')).toBe('true');
    expect(screen.getByRole('tab', { name: 'First' }).getAttribute('aria-selected')).toBe('false');
    expect(screen.getByRole('tabpanel').textContent).toBe('panel-b');
  });

  it('ArrowRight/ArrowLeft move the selection between tabs', () => {
    render(<Harness />);

    fireEvent.keyDown(screen.getByRole('tablist'), { key: 'ArrowRight' });
    expect(screen.getByRole('tab', { name: 'Second' }).getAttribute('aria-selected')).toBe('true');

    fireEvent.keyDown(screen.getByRole('tablist'), { key: 'ArrowLeft' });
    expect(screen.getByRole('tab', { name: 'First' }).getAttribute('aria-selected')).toBe('true');
  });
});
