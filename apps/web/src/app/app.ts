import { ChangeDetectionStrategy, Component, effect, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatToolbarModule } from '@angular/material/toolbar';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from './auth/auth.service';
import { ProjectContextService } from './projects/project-context.service';
import { NO_GROUP } from './workflows/workflow-groups';

@Component({
  imports: [RouterOutlet, RouterLink, RouterLinkActive, MatToolbarModule, MatButtonModule],
  selector: 'app-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './app.scss',
  templateUrl: './app.html',
})
export class App {
  private readonly router = inject(Router);
  private readonly auth = inject(AuthService);
  protected readonly user = this.auth.user;
  protected readonly projects = inject(ProjectContextService);
  protected readonly sharedSpace = NO_GROUP;

  constructor() {
    // Проєкти (F6-14) залежать від користувача: після входу — перечитати, після виходу — забути.
    effect(() => {
      if (this.user()) this.projects.reload();
      else this.projects.projects.set([]);
    });
  }

  /** Вибір у шапці: порожнє значення — усі проєкти. */
  protected selectProject(value: string): void {
    this.projects.select(value === '' ? null : value);
  }

  /** Вихід: сесію закрито на Api — на сторінку входу; якщо Api недоступний, користувача все одно забуваємо. */
  protected logout(): void {
    this.auth.logout().subscribe({
      next: () => void this.router.navigate(['/login']),
      error: () => {
        this.auth.forget();
        void this.router.navigate(['/login']);
      },
    });
  }
}
