import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { ConnectorCatalogComponent } from '../connectors/connector-catalog.component';
import { ConnectorDetail } from '../connectors/connectors-api.service';

export interface NewConnectionDialogData {
  /** Запис каталогу, з якого встановлено поточне підключення вузла; `null` — показати весь каталог. */
  catalogId: string | null;
}

/**
 * «Нове підключення» (F3-95): крок «Підключення» каталогу (назва, налаштування, облікові дані) просто з панелі вузла.
 * Закривається встановленим підключенням (`ConnectorDetail`) або без значення.
 */
@Component({
  selector: 'app-new-connection-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatDialogModule, ConnectorCatalogComponent],
  template: `
    <h2 mat-dialog-title i18n="@@newconnection.title">Нове підключення</h2>
    <mat-dialog-content data-testid="new-connection">
      <app-connector-catalog [onlyId]="data.catalogId" [autoBegin]="data.catalogId !== null" (installed)="ref.close($event)" />
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" data-testid="new-connection-close" mat-dialog-close i18n="@@newconnection.close">Закрити</button>
    </mat-dialog-actions>
  `,
})
export class NewConnectionDialogComponent {
  protected readonly data = inject<NewConnectionDialogData>(MAT_DIALOG_DATA);
  protected readonly ref = inject<MatDialogRef<NewConnectionDialogComponent, ConnectorDetail>>(MatDialogRef);
}
