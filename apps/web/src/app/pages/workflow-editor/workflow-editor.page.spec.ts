import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { WorkflowEditorComponent } from '../../workflow-editor/workflow-editor.component';
import { WorkflowEditorPage } from './workflow-editor.page';

describe('WorkflowEditorPage', () => {
  it('передає :id з роуту в редактор і оновлює його при переході', () => {
    const params = new BehaviorSubject(convertToParamMap({ id: 'w1' }));
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([]), { provide: ActivatedRoute, useValue: { paramMap: params } }],
    });
    const fixture = TestBed.createComponent(WorkflowEditorPage);
    fixture.detectChanges();
    const editor = () => fixture.debugElement.query((d) => d.componentInstance instanceof WorkflowEditorComponent).componentInstance as WorkflowEditorComponent;
    expect(editor().workflowId()).toBe('w1');

    params.next(convertToParamMap({ id: 'w2' }));
    fixture.detectChanges();
    expect(editor().workflowId()).toBe('w2');
    TestBed.inject(HttpTestingController);
  });
});
