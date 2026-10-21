import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'workflows' },
  {
    path: 'workflows',
    title: $localize`:@@workflows.title:Сценарії`,
    loadComponent: () => import('./pages/workflows/workflows.page').then((m) => m.WorkflowsPage),
  },
  {
    path: 'workflows/:id',
    title: $localize`:@@workfloweditor.title:Редактор сценарію`,
    loadComponent: () =>
      import('./pages/workflow-editor/workflow-editor.page').then((m) => m.WorkflowEditorPage),
  },
  {
    path: 'executions',
    title: $localize`:@@executions.title:Виконання`,
    loadComponent: () => import('./pages/executions/executions.page').then((m) => m.ExecutionsPage),
  },
  {
    path: 'connectors',
    title: $localize`:@@connectors.title:Конектори`,
    loadComponent: () => import('./pages/connectors/connectors.page').then((m) => m.ConnectorsPage),
  },
  {
    path: 'credentials',
    title: $localize`:@@credentials.title:Облікові дані`,
    loadComponent: () =>
      import('./pages/credentials/credentials.page').then((m) => m.CredentialsPage),
  },
  {
    path: 'settings',
    title: $localize`:@@settings.title:Налаштування`,
    loadComponent: () => import('./pages/settings/settings.page').then((m) => m.SettingsPage),
  },
  { path: '**', redirectTo: 'workflows' },
];
