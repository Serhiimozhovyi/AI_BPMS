import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { CardTableDirective } from './card-table.directive';

@Component({
  imports: [CardTableDirective],
  template: `
    <table appCardTable>
      <thead>
        <tr>
          <th data-no-label>Назва</th>
          <th>Стан</th>
        </tr>
      </thead>
      <tbody>
        @for (r of rows(); track r) {
          <tr>
            <td>{{ r }}</td>
            <td>ok</td>
          </tr>
        }
      </tbody>
    </table>
  `,
})
class Host {
  rows = signal(['a']);
}

describe('CardTableDirective', () => {
  it('підписує клітинки заголовками, окрім позначених data-no-label, і підхоплює нові рядки', async () => {
    const fixture = TestBed.createComponent(Host);
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    const cells = () => Array.from(host.querySelectorAll('tbody td'));
    expect(cells().map((c) => c.getAttribute('data-label'))).toEqual([null, 'Стан']);

    fixture.componentInstance.rows.set(['a', 'b']);
    await fixture.whenStable();
    await vi.waitFor(() => expect(cells().map((c) => c.getAttribute('data-label'))).toEqual([null, 'Стан', null, 'Стан']));
  });
});
