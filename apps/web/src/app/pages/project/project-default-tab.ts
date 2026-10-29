import { Injector } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ProjectPagesApiService } from '../../projects/project-pages-api.service';

/**
 * Вкладка, яку відкриває `/projects/:id` без сегмента: «Застосунок», якщо в проєкті є сторінки застосунку, видимі
 * користувачу, інакше — «Огляд» (і так само, якщо список сторінок не вдалося прочитати).
 */
export async function projectDefaultTab(injector: Injector, projectId: string): Promise<'app' | 'overview'> {
  try {
    const pages = await firstValueFrom(injector.get(ProjectPagesApiService).list(projectId));
    return pages.length > 0 ? 'app' : 'overview';
  } catch {
    return 'overview';
  }
}
