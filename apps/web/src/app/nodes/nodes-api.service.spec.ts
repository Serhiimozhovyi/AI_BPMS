import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { NodeTypeInfo, NodesApiService } from './nodes-api.service';

describe('NodesApiService', () => {
  it('list → GET /api/v1/nodes', () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const api = TestBed.inject(NodesApiService);
    const http = TestBed.inject(HttpTestingController);
    const ifNode: NodeTypeInfo = {
      type: 'core.if',
      version: 1,
      displayName: 'Умова',
      group: 'logic',
      inputs: ['main'],
      outputs: ['true', 'false'],
      parametersSchema: { type: 'object' },
      isTrigger: false,
    };

    let types: string[] = [];
    api.list().subscribe((list) => (types = list.map((n) => n.type)));
    http.expectOne({ method: 'GET', url: '/api/v1/nodes' }).flush([ifNode]);

    expect(types).toEqual(['core.if']);
    http.verify();
  });
});
