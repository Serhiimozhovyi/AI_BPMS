import { Routes } from '@angular/router';
import { authGuard } from './auth/auth.guard';

export const routes: Routes = [
  {
    path: 'login',
    title: $localize`:@@login.pageTitle:Вхід`,
    loadComponent: () => import('./pages/login/login.page').then((m) => m.LoginPage),
  },
  {
    // Решта застосунку — лише після входу (F3-24).
    path: '',
    canActivateChild: [authGuard],
    children: [
      {
        // «Огляд» (F6-07) — перша сторінка після входу.
        path: '',
        pathMatch: 'full',
        title: $localize`:@@overview.title:Огляд`,
        loadComponent: () => import('./pages/overview/overview.page').then((m) => m.OverviewPage),
      },
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
        path: 'tables',
        title: $localize`:@@tables.title:Таблиці даних`,
        loadComponent: () => import('./pages/tables/tables.page').then((m) => m.TablesPage),
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
    ],
  },
  { path: '**', redirectTo: '' },
];
