/** Тип тригера в списку сценаріїв (F3-80): підпис і іконка Material Symbols. */
export interface TriggerInfo {
  label: string;
  icon: string;
}

const TRIGGERS: Record<string, TriggerInfo> = {
  'core.manualTrigger': { label: $localize`:@@workflows.trigger.manual:Вручну`, icon: 'touch_app' },
  'core.webhook': { label: $localize`:@@workflows.trigger.webhook:Webhook`, icon: 'webhook' },
  'core.formTrigger': { label: $localize`:@@workflows.trigger.form:Форма`, icon: 'description' },
  'core.schedule': { label: $localize`:@@workflows.trigger.schedule:За розкладом`, icon: 'schedule' },
  'mcp.poll': { label: $localize`:@@workflows.trigger.poll:Опитування`, icon: 'sync' },
  'mcp.subscribe': { label: $localize`:@@workflows.trigger.subscribe:Подія конектора`, icon: 'notifications' },
  'core.errorTrigger': { label: $localize`:@@workflows.trigger.error:Помилка сценарію`, icon: 'error' },
  'core.workflowTrigger': { label: $localize`:@@workflows.trigger.workflow:З іншого сценарію`, icon: 'account_tree' },
};

/** Підпис і іконка тригера за типом вузла; невідомий тип показується як є. */
export function triggerInfo(type: string): TriggerInfo {
  return TRIGGERS[type] ?? { label: type, icon: 'bolt' };
}
