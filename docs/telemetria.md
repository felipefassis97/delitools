# Telemetria (opcional)

Ideia igual à do [fudo-print-doctor](https://github.com/Gartcia/fudo-print-doctor): cada ação
importante que a Dely resolve (instalar impressora, reconectar USB, teste de página, reiniciar
Spooler, limpar fila...) manda um evento curto pra uma planilha, pra você enxergar padrões entre
vários clientes sem precisar que ninguém te mande print.

É **totalmente opcional e silenciosa**: sem o arquivo `telemetria.txt`, o Delitools não manda nada;
se a rede falhar, ele ignora e segue o fluxo normal.

## 1. Criar o receptor (uma vez)

1. Planilha nova no Google Sheets.
2. **Extensões → Apps Script**, apaga tudo e cola [`tools/telemetria-appscript.gs`](../tools/telemetria-appscript.gs).
3. Troca `TOKEN` por uma chave inventada.
4. **Implantar → Nova implantação → Aplicativo da Web**:
   - *Executar como*: **Eu**
   - *Quem tem acesso*: **Qualquer pessoa** (não "com conta do Google" — dá erro 403)
5. Copia a URL que termina em `/exec`.

## 2. Conectar no Delitools

Cria um arquivo `telemetria.txt` (uma linha só, com a URL) na mesma pasta do `Delitools.exe`
(tanto na instalação quanto na versão portátil). A partir daí, cada evento relevante é reportado
sozinho.

**Esse arquivo nunca vai pro GitHub** (já está no `.gitignore`) — é local, por instalação.

## O que é mandado

| Campo | Exemplo |
|---|---|
| `pcId` | hash do MachineGuid do Windows — identifica a máquina sem identificar o cliente |
| `appVersion` | `2.12.0` |
| `so` | versão do Windows |
| `acao` | `instalar_usb`, `instalar_rede`, `teste_pagina`, `reiniciar_spooler`, `limpar_fila`, `reconectar_usb` |
| `resultado` | `ok`, `falhou`, `preso_na_fila` |
| `detalhe` | nome da impressora ou IP envolvido |

Não viaja log completo nem caminho de arquivo — só o resumo do evento.

## Ler os dados

```
GET <URL>/exec?key=TOKEN                 -> ultimos 100 eventos em JSON
GET <URL>/exec?key=TOKEN&limit=500
GET <URL>/exec?key=TOKEN&formato=csv
```

Ou direto na planilha, aba `eventos`.
