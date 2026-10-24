import { HttpErrorResponse } from '@angular/common/http';

const messages: Record<string, string> = {
    invalid_credentials: $localize`:@@error.invalidCredentials:Невірний email або пароль.`,
    locked: $localize`:@@error.locked:Забагато невдалих спроб. Спробуйте за 15 хвилин.`,
    invite_invalid: $localize`:@@error.inviteInvalid:Посилання недійсне або прострочене. Попросіть координатора про нове.`,
    weak_password: $localize`:@@error.weakPassword:Пароль — щонайменше 10 символів.`,
    slot_exists: $localize`:@@error.slotExists:Слот на цей час уже є.`,
    slot_has_bookings: $localize`:@@error.slotHasBookings:Слот має записи: закрийте його замість видалення.`,
    in_past: $localize`:@@error.inPast:Час уже минув.`,
    email_taken: $localize`:@@error.emailTaken:Обліковий запис з таким email уже є.`,
    psychologist_required: $localize`:@@error.psychologistRequired:Оберіть психолога для облікового запису.`,
    notice_empty: $localize`:@@error.noticeEmpty:Текст оголошення порожній.`,
    notice_unchanged: $localize`:@@error.noticeUnchanged:Це оголошення вже надіслано. Змініть текст, щоб надіслати нове.`,
    number_required: $localize`:@@error.numberRequired:Тут потрібне ціле число.`,
    cannot_disable_self: $localize`:@@error.cannotDisableSelf:Не можна вимкнути власний обліковий запис.`,
};

/** Повідомлення для людини з відповіді бекенду (`{ code, message }`). */
export function errorText(error: unknown): string {
    if (error instanceof HttpErrorResponse) {
        const code = (error.error as { code?: string } | null)?.code;
        if (code && messages[code]) {
            return messages[code];
        }
        if (error.status === 403) {
            return $localize`:@@error.forbidden:Недостатньо прав.`;
        }
        if (error.status === 404) {
            return $localize`:@@error.notFound:Не знайдено.`;
        }
    }
    return $localize`:@@error.generic:Не вдалося виконати дію. Спробуйте ще раз.`;
}
