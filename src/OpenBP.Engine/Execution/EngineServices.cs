namespace OpenBP.Engine.Execution;

/// <summary>Зовнішні залежності, які движок передає в контекст вузла.</summary>
internal sealed record EngineServices(
    IExpressionEvaluator Expressions,
    ICredentialProvider Credentials,
    IBinaryStore Binary,
    IExecutionLogger Logger);
