# Delitools

Ferramenta desktop (Windows) para instalar, diagnosticar e configurar impressoras térmicas e balanças em pontos de venda — sem depender de driver específico de fabricante.

## O que resolve

Cada marca de impressora térmica (Epson, Elgin, Bematech, XPrinter, Bixolon, SAM4S...) trazia seu próprio instalador, protocolo de rede e comportamento de driver. O Delitools substitui isso por um caminho único: **USB e rede sempre instalam com o driver genérico do Windows** (`Generic / Text Only`), e o app cuida do resto — achar a porta certa, criar a fila de impressão, detectar o que está plugado, e falar com a impressora quando ela precisa de configuração de rede.

## Módulos

1. **Instalar Impressora** — nome + tipo de conexão (USB ou Rede). Detecta impressora USB por VID/PID; no modo rede, varre a sub-rede local na porta 9100 pra achar impressoras sem precisar saber o IP.
2. **Impressoras Instaladas** — lista, testa, define como padrão e remove.
3. **Detectar Impressoras** — portas registradas no Spooler e no registro do Windows (USBPRINT), pegando o dispositivo mesmo antes de qualquer driver ser instalado.
4. **Corrigir Impressão** — reiniciar o Spooler, limpar fila travada, abrir o Gerenciador de Dispositivos.
5. **Ferramentas** — status do Spooler, ping/teste de porta de rede, gaveta de dinheiro (ESC/POS), backup e restauração de impressoras.
6. **Imprimir Teste** — página de teste em qualquer impressora já instalada.
7. **Balanças** — leitura de peso via porta COM serial (Toledo Prix 3, Filizola, Urano, Elgin DP).
8. **Config IP** — troca o IP de impressoras de rede, com fallback pra ferramentas oficiais de fabricante quando o protocolo não é suportado direto.

## Como é construído

- **C# + WinForms**, arquivo único (`PrinterInstaller/PrinterInstaller.cs`).
- **.NET Framework 4.x** — já vem em praticamente todo Windows 7 SP1 em diante, sem dependência externa.
- Se auto-eleva (UAC) ao abrir — instalar driver, mexer no Spooler e trocar IP exigem admin.
- Integra com o sistema via **WMI** (`Win32_Printer`, `Win32_PnPEntity`, `Win32_PrinterPort`), **PowerShell** (`Add-Printer`, `Add-PrinterPort`), **pnputil**, **Registro do Windows** e **sockets TCP/UDP** brutos.
- 100% local — sem servidor, sem banco de dados.

## Protocolos usados

| Protocolo | Porta / canal | Onde é usado | Como funciona |
|---|---|---|---|
| RAW / JetDirect | TCP 9100 | Impressão de rede, busca de impressoras, teste de porta | Envia os bytes ESC/POS crus, sem spooler de rede dedicado |
| USBPRINT | Registro do Windows | Detectar porta USB certa | `HKLM\SYSTEM\...\Enum\USBPRINT` mapeia dispositivo→porta, preenchido mesmo sem driver |
| XPrinter V3.0C | UDP 3000 | Config IP — Ethernet | Pacote binário próprio: cabeçalho + IP local + IP novo + máscara + gateway + flag DHCP |
| @eipca | TCP 9100 (texto) | Config IP — Ethernet e USB | Comando texto (`@eipca\nIP:...\nSM:...\nGW:...`), entendido por Epson TM-Net, Elgin e Bematech |
| Toledo / Filizola | Serial (COM), 9600 baud | Balanças | Quadro `STX + sinal + peso + "Kg" + ETX`; leitura pedida com ENQ (`0x05`) ou `STX+'P'+ETX` |

## Estrutura do projeto

```
PrinterInstaller/
  PrinterInstaller.cs      # codigo-fonte (unico arquivo)
  compile.bat               # compila com o csc.exe do .NET Framework
  Delitools_Setup.iss       # script do instalador (Inno Setup)
  install.ps1
  convert-to-exe.ps1
  Drivers/                  # drivers de fabricante (nao usados pelo app, mantidos so como referencia)
  NetConfigTools/           # ferramentas oficiais de configuracao de rede por fabricante
    BIXOLON/
    EPSON/
    XPRINTER/
    3NSTAR/
    SAM4S/
```

> **Nota sobre `NetConfigTools/`**: contém utilitários de terceiros (executáveis oficiais de configuração de rede baixados dos sites dos respectivos fabricantes), não código deste projeto. Cada um está sujeito à licença do seu próprio fabricante.

## Compilar

```bat
cd PrinterInstaller
compile.bat
```

Gera `Delitools.exe` usando o compilador do .NET Framework já presente no Windows.

## Empacotar o instalador

Requer [Inno Setup](https://jrsoftware.org/isinfo.php):

```powershell
ISCC.exe PrinterInstaller\Delitools_Setup.iss
```

## Diagnóstico inteligente

Parte da lógica de diagnóstico foi portada do [fudo-print-doctor](https://github.com/Gartcia/fudo-print-doctor)
(motor de suporte da Fudo, em PowerShell) pro chat da Dely:

- Classifica dispositivo USB por nível de certeza (evita confundir mouse/hub com impressora) e
  filtra impressoras virtuais (PDF/XPS/Fax) das listas de ação.
- Avalia todas as filas instaladas e diagnostica só a que está com problema — não mexe nas saudáveis.
- Reconexão guiada de USB com recriação segura de fila (testa antes de apagar a antiga, nunca
  deixa o cliente sem fila).
- Confirma impressora de rede de verdade (`DLE EOT 1`), não só "porta 9100 aberta".
- Exclusão de antivírus antes de abrir ferramenta de fabricante não assinada.
- Teste de página com confirmação real de que o trabalho saiu da fila.
- [Telemetria opcional](docs/telemetria.md) — eventos por PC numa planilha, sem servidor próprio.
