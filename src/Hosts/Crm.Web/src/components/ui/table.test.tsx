import { render, screen } from '@testing-library/react';
import { Table, TBody, TD, TH, THead, TR } from './table';

describe('Table primitives', () => {
  it('renders a semantic table with rows and cells', () => {
    render(
      <Table>
        <THead>
          <TR>
            <TH>Name</TH>
          </TR>
        </THead>
        <TBody>
          <TR data-testid="row-1">
            <TD>Alice</TD>
          </TR>
        </TBody>
      </Table>,
    );

    expect(screen.getByRole('table')).toBeTruthy();
    expect(screen.getByRole('columnheader', { name: 'Name' })).toBeTruthy();
    expect(screen.getByTestId('row-1')).toBeTruthy();
    expect(screen.getByRole('cell', { name: 'Alice' })).toBeTruthy();
  });

  it('keeps caller classes on every part', () => {
    render(
      <Table className="table-x">
        <THead className="head-x">
          <TR className="tr-x">
            <TH className="th-x">H</TH>
          </TR>
        </THead>
        <TBody className="body-x">
          <TR>
            <TD className="td-x">C</TD>
          </TR>
        </TBody>
      </Table>,
    );

    expect(screen.getByRole('table').className).toContain('table-x');
    expect(screen.getByRole('columnheader').className).toContain('th-x');
    expect(screen.getByRole('cell').className).toContain('td-x');
  });
});
