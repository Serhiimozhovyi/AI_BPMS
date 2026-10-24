import { createCustomElement } from '@angular/elements';
import { createApplication } from '@angular/platform-browser';
import { elementConfig } from './element.config';
import { OpenbpCanvasElement } from './openbp-canvas.component';

/** Реєструє `<openbp-canvas>` (F4-13); повторне підключення скрипта нічого не ламає. */
createApplication(elementConfig)
  .then((app) => {
    if (!customElements.get('openbp-canvas')) {
      customElements.define('openbp-canvas', createCustomElement(OpenbpCanvasElement, { injector: app.injector }));
    }
  })
  .catch((error: unknown) => console.error('openbp-canvas:', error));
