# ============================================================
# Converte install.ps1 -> PrinterInstaller.exe usando PS2EXE
# Nao precisa de Admin para rodar este script
# Documentacao PS2EXE: https://github.com/MScholtes/PS2EXE
# ============================================================

$input_file  = Join-Path $PSScriptRoot "install.ps1"
$output_file = Join-Path $PSScriptRoot "PrinterInstaller.exe"
$icon_file   = Join-Path $PSScriptRoot "icon.ico"   # opcional: coloque um .ico aqui

# Instala o modulo PS2EXE se ainda nao estiver presente
if (-not (Get-Module -ListAvailable -Name ps2exe)) {
    Write-Host "Instalando PS2EXE..." -ForegroundColor Yellow
    Install-Module -Name ps2exe -Scope CurrentUser -Force -AllowClobber
    Write-Host "PS2EXE instalado." -ForegroundColor Green
}

Import-Module ps2exe -Force

Write-Host ""
Write-Host "Convertendo: $input_file" -ForegroundColor Cyan
Write-Host "Destino    : $output_file" -ForegroundColor Cyan
Write-Host ""

$params = @{
    inputFile    = $input_file
    outputFile   = $output_file
    noConsole    = $true      # suprime a janela de console preta (apenas GUI)
    requireAdmin = $true      # pede elevacao automaticamente ao executar o EXE
    title        = "PrinterInstaller"
    description  = "Instalador de Impressoras Termicas USB"
    company      = "PrinterInstaller"
    version      = "2.0.0.0"
}

# Adiciona icone customizado se o arquivo existir
if (Test-Path $icon_file) {
    $params.iconFile = $icon_file
    Write-Host "Icone: $icon_file" -ForegroundColor DarkGray
}

# Executa a conversao
Invoke-ps2exe @params

Write-Host ""
if (Test-Path $output_file) {
    Write-Host "Sucesso! EXE gerado em:" -ForegroundColor Green
    Write-Host "  $output_file" -ForegroundColor White
    Write-Host ""
    Write-Host "Para distribuir: copie a pasta PrinterInstaller inteira" -ForegroundColor Yellow
    Write-Host "  PrinterInstaller.exe + pasta Drivers\" -ForegroundColor Yellow
} else {
    Write-Host "FALHA na geracao do EXE. Verifique os erros acima." -ForegroundColor Red
}
