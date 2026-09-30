Instruções de execução;

Pré-requisito: .NET SDK 8 ou superior.

bash
dotnet test Ultracar.slnx                 # 30 testes, ~5 s
dotnet run --project src/Ultracar.Api     # http://localhost:5028/swagger


Em desenvolvimento a api-key vem desligada, então dá para chamar a API direto pelo Swagger ou pelo `curl`. Fora de Development ela é obrigatória; veja [Autenticação](#autenticação).

Decisões e trade-offs

**Persistência em memória.** O foco do desafio é resiliência e idempotência, e assim o projeto roda com um `dotnet run`, sem banco nem Docker. O repositório fica atrás de uma interface (`IFiscalDocumentRepository`), então trocar por um banco não mexe no resto. *Custo:* os dados somem ao reiniciar e só roda uma instância.

**Como a duplicidade é evitada.**
1. **Por OS:** o `workOrderId` funciona como índice único. Mesmo com várias requisições simultâneas para a mesma OS, só uma é gravada (há teste com 50 em paralelo). Pedido repetido com os mesmos dados devolve a solicitação existente. Com dados diferentes é `409`, porque provavelmente é um erro do lado do cliente.
2. **Na integradora:** o Id da solicitação vai como **chave de idempotência**. Esse é o ponto delicado: depois de um timeout não dá para saber se a integradora emitiu ou não. Tentando de novo com a mesma chave, ela devolve o documento que já tinha emitido em vez de criar outro. O cenário `-TIMEOUT` mostra exatamente isso.

**Emissão fora da requisição.** O cliente recebe `202` na hora e acompanha pelo `GET`. A lentidão da integradora não prende conexões do ERP.

**Falha temporária x definitiva.** A decisão fica em um lugar só, o `FiscalDocumentProcessor`:
- **Temporária** (tenta de novo): timeout, indisponibilidade, erro inesperado de rede e "aprovado" sem número ou protocolo. Esse último conta como temporário porque não dá para confiar na resposta, e a retentativa com a mesma chave traz o documento real.
- **Definitiva** (não tenta de novo): rejeição de negócio. Repetir o mesmo pedido não muda nada.

**Retentativa.** Backoff exponencial com teto (2 s, 4 s, 8 s... até 60 s), no máximo 5 tentativas. Esgotou, vira `Failed` com `RETRIES_EXHAUSTED` e o último erro. Tudo configurável na seção `FiscalProcessing` do `appsettings.json`.

**Emissão que ficou pendente.** O worker sempre busca o que está `Pending` ou `WaitingRetry` com horário vencido; nada depende de fila em memória que possa se perder. Com banco de dados e várias instâncias, faltaria só tratar quem ficou preso em `Processing` porque o processo caiu no meio: um prazo (lease) e um `UPDATE ... WHERE` atômico para uma instância "pegar" a solicitação. Está em melhorias.

**Suporte e auditoria.** Cada solicitação guarda o histórico de tudo que aconteceu (`/history`): criação, cada tentativa, falhas com código e motivo, e emissão. Os logs são estruturados com `FiscalDocumentId`, `WorkOrderId` e `Attempt`, e os erros da API trazem `traceId`.

**Repositório trabalha com cópias.** O repositório guarda e devolve cópias do `FiscalDocument` (`Clone()`), como aconteceria com um banco. Assim a API nunca lê o objeto enquanto o worker está alterando.

**Validação simples.** DataAnnotations no request: CPF/CNPJ com 11/14 dígitos, IBGE com 7, valor maior que zero. Sem dígito verificador: o desafio não pede regra fiscal real, e o CPF do exemplo nem é válido.

**Tempo testável.** Todo o código usa `TimeProvider`, então os testes de timeout e backoff usam um relógio falso e rodam na hora, sem `sleep`.

* Autenticação

O documento não pede, mas a API tem uma api-key simples (header `api-key`) nas rotas `/api`. É **ligada por padrão** e desligada no `appsettings.Development.json` para a demonstração. Em outro ambiente:

bash
ApiKey__Value=minha-chave dotnet run --project src/Ultracar.Api --environment Production
```

Sem `ApiKey:Value`, a API nem sobe (a menos que `ApiKey:Enabled=false`).



Uso de IA


- **Ferramenta:** Claude Code (Claude Opus 5.5).
- **Onde ajudou:** montar o mock da integradora fiscal (FakeFiscalIntegrator), com um cenário para cada situação pedida no desafio: aprovada, resposta demorada, timeout, fora do ar, rejeitada e resposta inconsistente;
simular o processamento em segundo plano no worker (FiscalDocumentWorker), com timeout na chamada, retentativa e backoff;
escrever os testes e este README.


## Melhorias com mais tempo

1. Banco relacional (PostgreSQL/SQL Server) com `UNIQUE (WorkOrderId)`, índice em `(Status, NextAttemptAt)`, histórico em tabela própria e lease para várias instâncias.
2. Circuit breaker (`Microsoft.Extensions.Resilience`/Polly) para não gastar tentativas enquanto a integradora está fora.
3. Consultar a integradora pela chave antes de reenviar, para integradoras que não sejam idempotentes.
4. Reprocessamento manual de solicitações `Failed` (endpoint para o suporte).
5. Notificar o ERP (webhook) quando a emissão terminar, em vez de o cliente ficar consultando.
6. Observabilidade: OpenTelemetry, métricas por status e alerta para `RETRIES_EXHAUSTED`.
7. Autenticação por cliente (OAuth2/JWT) no lugar de uma api-key única.
8. Dockerfile e validação fiscal real (dígito verificador, município existente).
