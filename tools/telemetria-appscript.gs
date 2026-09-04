/**
 * Delitools - receptor de telemetria
 * ------------------------------------------------------------------
 * Recebe os eventos que o Delitools manda (via Dely) e guarda numa planilha,
 * uma linha por evento. Tambem expoe um doGet pra ler os dados de fora.
 *
 * Baseado no receptor do FudoPrintDoctor (github.com/Gartcia/fudo-print-doctor).
 *
 * COMO IMPLANTAR
 *   1. Planilha nova no Google Sheets.
 *   2. Extensoes > Apps Script. Apaga tudo que tiver e cola este arquivo.
 *   3. Troca TOKEN por uma chave inventada (letras e numeros, sem espaco).
 *   4. Implantar > Nova implantacao > tipo "Aplicativo da Web":
 *        Executar como: Eu
 *        Quem tem acesso: Qualquer pessoa
 *      (NAO "Qualquer pessoa com conta do Google" — isso da erro 403,
 *      o Delitools nao autentica com sua conta Google.)
 *      Copia a URL que termina em /exec.
 *   5. Cola essa URL num arquivo chamado telemetria.txt, na mesma pasta do
 *      Delitools.exe (uma linha so, com a URL). O Delitools comeca a mandar
 *      sozinho a partir dai — sem esse arquivo, ele nao manda nada.
 *
 * SEGURANCA
 *   A URL /exec e publica (quem tiver ela pode escrever). Por isso doPost
 *   valida o formato do payload antes de gravar, e doGet exige o TOKEN.
 *   Se aparecer lixo na planilha, e so reimplantar (Nova versao) pra trocar
 *   a URL e atualizar o telemetria.txt.
 *   NAO sobe este arquivo com o TOKEN de verdade pra um repositorio publico.
 *
 * COMO LER OS DADOS
 *   GET <URL>/exec?key=TOKEN            -> ultimos 100 eventos em JSON
 *   GET <URL>/exec?key=TOKEN&limit=500  -> mais linhas
 *   GET <URL>/exec?key=TOKEN&formato=csv
 */

var TOKEN = 'TROCAR-POR-UMA-CHAVE';
var ABA   = 'eventos';

var COLUNAS = [
  'recebido', 'timestamp', 'pcId', 'appVersion', 'so',
  'acao', 'resultado', 'detalhe', 'json'
];

function doPost(e) {
  try {
    var d = JSON.parse(e.postData.contents);
    if (!payloadValido_(d)) {
      return json_({ ok: false, error: 'payload nao reconhecido' });
    }
    var aba = obterAba_();
    aba.appendRow(montarLinha_(d));
    return json_({ ok: true });
  } catch (err) {
    return json_({ ok: false, error: String(err) });
  }
}

function payloadValido_(d) {
  if (!d || typeof d !== 'object') { return false; }
  if (typeof d.schemaVersion !== 'string' || !/^\d+\.\d+$/.test(d.schemaVersion)) { return false; }
  if (typeof d.acao !== 'string' || !d.acao) { return false; }
  if (typeof d.resultado !== 'string' || !d.resultado) { return false; }
  return true;
}

function doGet(e) {
  var p = (e && e.parameter) ? e.parameter : {};
  if (p.key !== TOKEN) {
    return json_({ ok: false, error: 'chave invalida' });
  }

  var aba = obterAba_();
  var limite = Math.min(parseInt(p.limit || '100', 10) || 100, 5000);
  var total = aba.getLastRow();
  if (total < 2) { return json_({ ok: true, linhas: [] }); }

  var desde = Math.max(2, total - limite + 1);
  var dados = aba.getRange(desde, 1, total - desde + 1, COLUNAS.length).getValues();

  if (p.formato === 'csv') {
    var linhas = [COLUNAS.join(',')];
    for (var i = 0; i < dados.length; i++) {
      linhas.push(dados[i].map(function (c) {
        var s = String(c === null || c === undefined ? '' : c).replace(/"/g, '""');
        return '"' + s + '"';
      }).join(','));
    }
    return ContentService.createTextOutput(linhas.join('\n')).setMimeType(ContentService.MimeType.CSV);
  }

  var linhasObj = dados.map(function (r) {
    var o = {};
    for (var i = 0; i < COLUNAS.length; i++) { o[COLUNAS[i]] = r[i]; }
    delete o.json;
    return o;
  });
  return json_({ ok: true, quantidade: linhasObj.length, linhas: linhasObj });
}

function obterAba_() {
  var ss = SpreadsheetApp.getActiveSpreadsheet();
  var aba = ss.getSheetByName(ABA);
  if (!aba) {
    aba = ss.insertSheet(ABA);
    aba.appendRow(COLUNAS);
    aba.setFrozenRows(1);
  }
  return aba;
}

function montarLinha_(d) {
  return [
    new Date(), d.timestamp || '', d.pcId || '', d.appVersion || '', d.so || '',
    d.acao || '', d.resultado || '', d.detalhe || '', JSON.stringify(d)
  ];
}

function json_(obj) {
  return ContentService.createTextOutput(JSON.stringify(obj)).setMimeType(ContentService.MimeType.JSON);
}
