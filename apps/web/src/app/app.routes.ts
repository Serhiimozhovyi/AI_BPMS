import { inject } from '@angular/core';
import { Router, Routes, UrlMatcher, UrlSegment } from '@angular/router';
import { authGuard } from './auth/auth.guard';

/** `/form/<шлях з кількох сегментів>` — публічна сторінка форми (F6-03). */
export const formMatcher: UrlMatcher = (segments: UrlSegment[]) =>
  segments.length > 1 && segments[0].path === 'form' ? { consumed: segments } : null;

/**
 * `/executions` і `/executions/:id` (F3-81) — одна сторінка: матчер, а не два маршрути, щоб відкриття виконання не
 * перестворювало її (фільтри, прокрутка і запити лишаються).
 */
export const executionsMatcher: UrlMatcher = (segments: UrlSegment[]) =>
  segments.length >= 1 && segments.length <= 2 && segments[0].path === 'executions'
    ? { consumed: segments, posParams: segments[1] ? { id: segments[1] } : undefined }
    : null;

export const routes: Routes = [
  {
    // Публічна форма (F6-03): без входу (доступ перевіряє Api за налаштуванням вузла) і без меню застосунку.
    matcher: formMatcher,
    title: $localize`:@@form.pageTitle:Форма`,
    loadComponent: () => import('./pages/form/form.page').then((m) => m.FormPage),
  },
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
        // Список проєктів (F8-03).
        path: 'projects',
        pathMatch: 'full',
        title: $localize`:@@projects.title:Проєкти`,
        loadComponent: () => import('./pages/projects/projects.page').then((m) => m.ProjectsPage),
      },
      {
        // Проєкт як робочий простір: вкладки — дочірні маршрути (F8-03); учасники — F6-14.
        path: 'projects/:id',
        title: $localize`:@@project.title:Проєкт`,
        loadComponent: () => import('./pages/project/project.page').then((m) => m.ProjectPage),
        children: [
          { path: '', pathMatch: 'full', redirectTo: 'overview' },
          {
            path: 'overview',
            loadComponent: () => import('./pages/overview/overview.page').then((m) => m.OverviewPage),
          },
          {
            path: 'workflows',
            loadComponent: () => import('./pages/workflows/workflows.page').then((m) => m.WorkflowsPage),
          },
          {
            path: 'tables',
            loadComponent: () => import('./pages/tables/tables.page').then((m) => m.TablesPage),
          },
          {
            path: 'connections',
            loadComponent: () => import('./pages/connections/connections.page').then((m) => m.ConnectionsPage),
          },
          {
            path: 'app',
            loadComponent: () => import('./pages/project/project-app.page').then((m) => m.ProjectAppPage),
            // Сторінки застосунку (F8-04): список/картка/форма за ключем сторінки.
            children: [
              {
                path: ':key',
                loadComponent: () => import('./pages/project/app-page.page').then((m) => m.AppPagePage),
              },
              {
                path: ':key/:rowKey',
                loadComponent: () => import('./pages/project/app-page.page').then((m) => m.AppPagePage),
              },
            ],
          },
          {
            path: 'members',
            loadComponent: () => import('./pages/project/project-members.page').then((m) => m.ProjectMembersPage),
          },
        ],
      },
      {
        matcher: executionsMatcher,
        title: $localize`:@@executions.title:Виконання`,
        loadComponent: () => import('./pages/executions/executions.page').then((m) => m.ExecutionsPage),
      },
      {
        // Задачі погодження core.approval (F6-02); картка — і за токеном з $execution.taskUrl.
        path: 'tasks',
        title: $localize`:@@tasks.title:Задачі`,
        loadComponent: () => import('./pages/tasks/tasks.page').then((m) => m.TasksPage),
      },
      {
        path: 'tasks/:id',
        title: $localize`:@@task.pageTitle:Задача`,
        loadComponent: () => import('./pages/tasks/task.page').then((m) => m.TaskPage),
      },
      {
        path: 'tables',
        title: $localize`:@@tables.title:Таблиці даних`,
        loadComponent: () => import('./pages/tables/tables.page').then((m) => m.TablesPage),
      },
      {
        // «Підключення» (F3-99): конектори, каталог і облікові дані в одному місці; `?tab=connected|catalog|credentials`.
        path: 'connections',
        title: $localize`:@@connections.title:Підключення`,
        loadComponent: () => import('./pages/connections/connections.page').then((m) => m.ConnectionsPage),
      },
      {
        // Старі посилання (документація, сповіщення, шаблони): `?catalog=<id>` відкриває каталог із підсвіченим записом.
        path: 'connectors',
        redirectTo: ({ queryParams }) => {
          const catalog = queryParams['catalog'] as string | undefined;
          return inject(Router).createUrlTree(['/connections'], {
            queryParams: catalog === undefined ? { tab: 'connected' } : { tab: 'catalog', catalog },
          });
        },
      },
      {
        path: 'credentials',
        redirectTo: () => inject(Router).createUrlTree(['/connections'], { queryParams: { tab: 'credentials' } }),
      },
      {
        path: 'settings',
        title: $localize`:@@settings.title:Налаштування`,
        loadComponent: () => import('./pages/settings/settings.page').then((m) => m.SettingsPage),
      },
      {
        // Невідома адреса (F3-84): сторінка «Не знайдено» замість мовчазного перекидання на «Огляд».
        path: '**',
        title: $localize`:@@notfound.pageTitle:Не знайдено`,
        loadComponent: () => import('./pages/not-found/not-found.page').then((m) => m.NotFoundPage),
      },
    ],
  },
];
