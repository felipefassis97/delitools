# ==============================================================
# PrinterInstaller V3
# Menu lateral + painel de conteudo dinamico
# Requer: Windows 10/11, PowerShell 5.1+, Admin
# ==============================================================

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

# ==============================================================
# CATALOGO
# ==============================================================
$script:Catalog = [ordered]@{
    "Bematech" = @("MP-100S TH", "MP-2800 TH", "MP-4200 TH")
    "Elgin"    = @("i7", "i8", "i9")
    "Epson"    = @("TM-T20", "TM-T20X", "TM-T88V", "TM-T88VI", "TM-U220")
}

$script:VidPidMap = [ordered]@{
    "04B8:0202" = @{ Brand = "Epson";    Model = "TM-T88V"    }
    "04B8:0E28" = @{ Brand = "Epson";    Model = "TM-T20"     }
    "04B8:0007" = @{ Brand = "Epson";    Model = "TM-U220"    }
    "0DD4:0003" = @{ Brand = "Bematech"; Model = "MP-4200 TH" }
    "0DD4:0001" = @{ Brand = "Bematech"; Model = "MP-2800 TH" }
    "1A86:7523" = @{ Brand = "Elgin";    Model = $null        }
    "04B8"      = @{ Brand = "Epson";    Model = $null }
    "0DD4"      = @{ Brand = "Bematech"; Model = $null }
}

$script:Base        = if ($PSScriptRoot -and $PSScriptRoot -ne '') { $PSScriptRoot } else {
    [System.IO.Path]::GetDirectoryName([System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName)
}
$script:DriversRoot = Join-Path $script:Base "Drivers"
$script:LastPrinter = $null
$script:PrinterList = @()

# ==============================================================
# CORES (tema)
# ==============================================================
$C = @{
    SidebarBg   = [System.Drawing.Color]::FromArgb(13, 27, 42)
    NavNormal   = [System.Drawing.Color]::FromArgb(13, 27, 42)
    NavHover    = [System.Drawing.Color]::FromArgb(22, 50, 78)
    NavSelected = [System.Drawing.Color]::FromArgb(0, 120, 215)
    NavText     = [System.Drawing.Color]::FromArgb(180, 210, 245)
    NavSelText  = [System.Drawing.Color]::White
    HeaderBg    = [System.Drawing.Color]::FromArgb(0, 102, 204)
    ContentBg   = [System.Drawing.Color]::FromArgb(248, 249, 250)
    PanelBg     = [System.Drawing.Color]::White
    StatusBg    = [System.Drawing.Color]::FromArgb(20, 20, 35)
    StatusText  = [System.Drawing.Color]::FromArgb(200, 215, 235)
    LogBg       = [System.Drawing.Color]::FromArgb(14, 17, 23)
    BtnGreen    = [System.Drawing.Color]::FromArgb(0, 140, 0)
    BtnBlue     = [System.Drawing.Color]::FromArgb(0, 110, 180)
    BtnPurple   = [System.Drawing.Color]::FromArgb(110, 60, 160)
    BtnOrange   = [System.Drawing.Color]::FromArgb(180, 100, 0)
    BtnTeal     = [System.Drawing.Color]::FromArgb(0, 120, 120)
}

# ==============================================================
# FUNCOES
# ==============================================================

function Test-IsAdmin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Write-Log {
    param([string]$Msg, [string]$Color = "White")
    $ts = Get-Date -Format "HH:mm:ss"
    $script:Log.SelectionStart  = $script:Log.TextLength
    $script:Log.SelectionLength = 0
    $script:Log.SelectionColor  = [System.Drawing.Color]::FromName($Color)
    $script:Log.AppendText("[$ts] $Msg`n")
    $script:Log.ScrollToCaret()
    $script:Log.Refresh()
}

function Find-UsbPrinter {
    Write-Log "Consultando USB via WMI..." "Cyan"
    $devices = @(Get-WmiObject Win32_PnPEntity -ErrorAction SilentlyContinue |
        Where-Object { $_.DeviceID -match "USB\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})" })
    if ($devices.Count -eq 0) { Write-Log "Nenhum dispositivo USB encontrado." "Yellow"; return @{ Found = $false } }

    foreach ($dev in $devices) {
        if ($dev.DeviceID -match "USB\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})") {
            $vid = $matches[1].ToUpper(); $pid = $matches[2].ToUpper()
            Write-Log "$($dev.Name)  [VID=$vid PID=$pid]" "Gray"
            $vpKey = "${vid}:${pid}"
            if ($script:VidPidMap.Contains($vpKey)) {
                $e = $script:VidPidMap[$vpKey]
                Write-Log "Identificado: $($e.Brand) $($e.Model)" "Lime"
                return @{ Found = $true; Vid = $vid; Pid = $pid; Brand = $e.Brand; Model = $e.Model }
            }
            if ($script:VidPidMap.Contains($vid)) {
                $e = $script:VidPidMap[$vid]
                Write-Log "Fabricante: $($e.Brand) — selecione o modelo" "Yellow"
                return @{ Found = $true; Vid = $vid; Pid = $pid; Brand = $e.Brand; Model = $null }
            }
        }
    }
    Write-Log "Nao reconhecido. Selecione manualmente." "Yellow"
    return @{ Found = $false }
}

function Install-PrinterDriver {
    param([string]$Brand, [string]$Model)
    $path = Join-Path $script:DriversRoot "$Brand\$Model"
    Write-Log "Pasta: $path" "Gray"
    if (-not (Test-Path $path)) {
        Write-Log "ERRO: Pasta nao encontrada." "Red"
        Write-Log "Crie Drivers\$Brand\$Model\ com os arquivos .inf." "Yellow"
        return @{ Success = $false; NewDrivers = @() }
    }
    $infs = @(Get-ChildItem $path -Filter "*.inf" -Recurse -ErrorAction SilentlyContinue)
    if ($infs.Count -eq 0) { Write-Log "ERRO: Nenhum .inf encontrado." "Red"; return @{ Success = $false; NewDrivers = @() } }
    Write-Log "Arquivos .inf: $($infs.Count)" "Cyan"
    $before  = @(Get-PrinterDriver -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
    $allOk   = $true
    foreach ($inf in $infs) {
        Write-Log ">> $($inf.Name)" "White"
        $out  = & pnputil.exe /add-driver "$($inf.FullName)" /install 2>&1
        $code = $LASTEXITCODE
        switch ($code) {
            0    { Write-Log "   OK" "Lime" }
            3010 { Write-Log "   OK (requer reinicializacao)" "Yellow" }
            default { Write-Log "   FALHA $code" "Red"; $allOk = $false }
        }
    }
    $after      = @(Get-PrinterDriver -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
    $newDrivers = @($after | Where-Object { $before -notcontains $_ })
    if ($newDrivers.Count -gt 0) { Write-Log "Novos drivers: $($newDrivers -join ', ')" "Cyan" }
    return @{ Success = $allOk; NewDrivers = $newDrivers }
}

function Add-NetworkPrinter {
    param([string]$Brand, [string]$Model, [string]$Ip, [int]$Port = 9100, [string]$Name)
    if ($Ip -notmatch '^\d{1,3}(\.\d{1,3}){3}$') { Write-Log "IP invalido: $Ip" "Red"; return $false }
    Write-Log "--- Driver ---" "Cyan"
    $result = Install-PrinterDriver -Brand $Brand -Model $Model
    $driverName = $null
    if ($result.NewDrivers.Count -gt 0) {
        $driverName = $result.NewDrivers[0]
    } else {
        $driverName = Get-PrinterDriver -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like "*$Brand*" -or $_.Name -like "*$Model*" } |
            Select-Object -ExpandProperty Name -First 1
    }
    if (-not $driverName) { Write-Log "ERRO: Driver nao identificado." "Red"; return $false }
    Write-Log "Driver: $driverName" "Cyan"

    $portName = "IP_${Ip}_${Port}"
    Write-Log "--- Porta $portName ---" "Cyan"
    if (-not (Get-PrinterPort -Name $portName -ErrorAction SilentlyContinue)) {
        try { Add-PrinterPort -Name $portName -PrinterHostAddress $Ip -PortNumber $Port -EA Stop; Write-Log "Porta criada." "Lime" }
        catch { Write-Log "ERRO porta: $_" "Red"; return $false }
    } else { Write-Log "Porta ja existe." "Yellow" }

    Write-Log "--- Adicionando impressora ---" "Cyan"
    $finalName = if ($Name) { $Name } else { "$Brand $Model (Rede)" }
    if (Get-Printer -Name $finalName -EA SilentlyContinue) { Remove-Printer -Name $finalName -EA SilentlyContinue; Write-Log "Anterior removida." "Yellow" }
    try {
        Add-Printer -Name $finalName -DriverName $driverName -PortName $portName -EA Stop
        Write-Log "Impressora: $finalName" "Lime"
        $script:LastPrinter = $finalName
        Update-StatusBar
        return $true
    } catch { Write-Log "ERRO: $_" "Red"; return $false }
}

function Invoke-TestPage {
    param([string]$PrinterName)
    if (-not $PrinterName) { Write-Log "Nenhuma impressora selecionada." "Red"; return }
    Write-Log "Pagina de teste: $PrinterName" "Cyan"
    try {
        $wmi = Get-WmiObject Win32_Printer -Filter "Name='$($PrinterName -replace "'","\'")'" -EA Stop
        if ($wmi) {
            $r = $wmi.PrintTestPage()
            if ($r.ReturnValue -eq 0) { Write-Log "Enviada com sucesso!" "Lime" }
            else { Write-Log "Falha. Codigo WMI: $($r.ReturnValue)" "Red" }
        } else { Write-Log "Impressora nao encontrada." "Red" }
    } catch { Write-Log "Erro: $_" "Red" }
}

function Reset-Spooler {
    param([bool]$ClearQueue = $false)
    try { Stop-Service Spooler -Force -EA Stop; Write-Log "Spooler parado." "Yellow" }
    catch { Write-Log "ERRO ao parar: $_" "Red"; return }
    if ($ClearQueue) {
        $dir = "$env:SystemRoot\System32\spool\PRINTERS"
        $n = @(Get-ChildItem $dir -File -EA SilentlyContinue).Count
        Get-ChildItem $dir -File -EA SilentlyContinue | Remove-Item -Force -EA SilentlyContinue
        Write-Log "$n arquivo(s) removidos da fila." "Yellow"
    }
    try { Start-Service Spooler -EA Stop; Write-Log "Spooler reiniciado!" "Lime" }
    catch { Write-Log "ERRO ao iniciar: $_" "Red" }
    Refresh-SpoolerPanel
}

function Update-StatusBar {
    if ($script:LastPrinter) {
        $script:lblStatusPrinter.Text      = "Impressora: $($script:LastPrinter)"
        $script:lblStatusDot.BackColor     = [System.Drawing.Color]::FromArgb(0, 200, 80)
    } else {
        $script:lblStatusPrinter.Text  = "Nenhuma impressora instalada nesta sessao"
        $script:lblStatusDot.BackColor = [System.Drawing.Color]::FromArgb(80, 80, 100)
    }
}

function Refresh-SpoolerPanel {
    $svc = Get-Service Spooler -EA SilentlyContinue
    if ($svc.Status -eq "Running") {
        $script:lblSpoolerSvc.Text      = "Spooler: ATIVO"
        $script:lblSpoolerSvc.ForeColor = [System.Drawing.Color]::FromArgb(0, 200, 80)
    } else {
        $script:lblSpoolerSvc.Text      = "Spooler: PARADO"
        $script:lblSpoolerSvc.ForeColor = [System.Drawing.Color]::FromArgb(255, 80, 80)
    }
    $script:lstQueue.Items.Clear()
    $jobs = @(Get-WmiObject Win32_PrintJob -EA SilentlyContinue)
    if ($jobs.Count -eq 0) {
        $script:lstQueue.Items.Add("(fila vazia)") | Out-Null
    } else {
        foreach ($j in $jobs) { $script:lstQueue.Items.Add("$($j.Name)  |  $($j.Document)  |  $($j.Status)") | Out-Null }
    }
}

function Refresh-TestPanel {
    $script:lstPrinters.Items.Clear()
    $script:PrinterList = @(Get-Printer -EA SilentlyContinue)
    if ($script:PrinterList.Count -eq 0) {
        $script:lstPrinters.Items.Add("(nenhuma impressora instalada)") | Out-Null
    } else {
        foreach ($p in $script:PrinterList) {
            $online = if ($p.PrinterStatus -eq 3) { "Online" } else { "Offline" }
            $script:lstPrinters.Items.Add("$($p.Name)  [$online]") | Out-Null
        }
    }
}

# ==============================================================
# ADMIN CHECK
# ==============================================================
if (-not (Test-IsAdmin)) {
    [System.Windows.Forms.MessageBox]::Show(
        "Execute como Administrador:`nClique direito -> 'Executar como administrador'",
        "Permissao Necessaria", "OK", "Warning") | Out-Null
    exit 1
}

# ==============================================================
# FORM
# ==============================================================
$FW = 700; $FH = 625
$SIDEBAR_W = 138; $HEADER_H = 55; $STATUS_H = 55
$CONTENT_X = $SIDEBAR_W + 4
$CONTENT_W = $FW - $CONTENT_X - 10
$CONTENT_Y = $HEADER_H + 8
$PANEL_H   = 310
$LOG_Y     = $CONTENT_Y + $PANEL_H + 8
$LOG_H     = $FH - $LOG_Y - $STATUS_H - 8

$form = New-Object System.Windows.Forms.Form
$form.Text            = "PrinterInstaller V3"
$form.ClientSize      = New-Object System.Drawing.Size($FW, $FH)
$form.StartPosition   = "CenterScreen"
$form.FormBorderStyle = "FixedDialog"
$form.MaximizeBox     = $false
$form.BackColor       = $C.ContentBg
$form.Font            = New-Object System.Drawing.Font("Segoe UI", 9)

# --- Header ---
$header           = New-Object System.Windows.Forms.Panel
$header.Size      = New-Object System.Drawing.Size($FW, $HEADER_H)
$header.Location  = New-Object System.Drawing.Point(0, 0)
$header.BackColor = $C.HeaderBg

$lblH1 = New-Object System.Windows.Forms.Label
$lblH1.Text      = "PrinterInstaller V3"
$lblH1.Font      = New-Object System.Drawing.Font("Segoe UI", 14, [System.Drawing.FontStyle]::Bold)
$lblH1.ForeColor = [System.Drawing.Color]::White
$lblH1.Size      = New-Object System.Drawing.Size(360, 34)
$lblH1.Location  = New-Object System.Drawing.Point(152, 8)

$lblH2 = New-Object System.Windows.Forms.Label
$lblH2.Text      = "Impressoras Termicas USB  |  pnputil  |  Windows 10/11"
$lblH2.Font      = New-Object System.Drawing.Font("Segoe UI", 8.5)
$lblH2.ForeColor = [System.Drawing.Color]::FromArgb(170, 210, 255)
$lblH2.Size      = New-Object System.Drawing.Size(400, 18)
$lblH2.Location  = New-Object System.Drawing.Point(153, 36)
$header.Controls.AddRange(@($lblH1, $lblH2))

# --- Sidebar ---
$sidebar           = New-Object System.Windows.Forms.Panel
$sidebar.Size      = New-Object System.Drawing.Size($SIDEBAR_W, $FH - $HEADER_H - $STATUS_H)
$sidebar.Location  = New-Object System.Drawing.Point(0, $HEADER_H)
$sidebar.BackColor = $C.SidebarBg

# Linha divisoria sidebar | conteudo
$divider           = New-Object System.Windows.Forms.Panel
$divider.Size      = New-Object System.Drawing.Size(2, $FH - $HEADER_H - $STATUS_H)
$divider.Location  = New-Object System.Drawing.Point($SIDEBAR_W, $HEADER_H)
$divider.BackColor = [System.Drawing.Color]::FromArgb(0, 80, 160)

# Cria botao de navegacao lateral
function New-NavButton {
    param([string]$Text, [string]$Sub, [int]$Y)
    $btn = New-Object System.Windows.Forms.Button
    $btn.Text      = "$Text`n$Sub"
    $btn.Size      = New-Object System.Drawing.Size($SIDEBAR_W, 68)
    $btn.Location  = New-Object System.Drawing.Point(0, $Y)
    $btn.FlatStyle = "Flat"
    $btn.FlatAppearance.BorderSize  = 0
    $btn.FlatAppearance.MouseOverBackColor = $C.NavHover
    $btn.BackColor = $C.NavNormal
    $btn.ForeColor = $C.NavText
    $btn.Font      = New-Object System.Drawing.Font("Segoe UI", 9, [System.Drawing.FontStyle]::Bold)
    $btn.Cursor    = [System.Windows.Forms.Cursors]::Hand
    $btn.TextAlign = "MiddleCenter"
    return $btn
}

$navUsb     = New-NavButton "USB"     "Instalar Driver"    16
$navNet     = New-NavButton "REDE"    "TCP / IP"           90
$navSpooler = New-NavButton "SPOOLER" "Fila / Servico"     164
$navTest    = New-NavButton "TESTE"   "Pagina de Teste"    238
$sidebar.Controls.AddRange(@($navUsb, $navNet, $navSpooler, $navTest))

# --- Selecionar nav ativo ---
$script:AllNavBtns = @($navUsb, $navNet, $navSpooler, $navTest)
function Select-Nav {
    param($active)
    foreach ($b in $script:AllNavBtns) {
        $b.BackColor = $C.NavNormal; $b.ForeColor = $C.NavText
    }
    $active.BackColor = $C.NavSelected; $active.ForeColor = $C.NavSelText
}

# --- Painel de conteudo base ---
$contentBase          = New-Object System.Windows.Forms.Panel
$contentBase.Size     = New-Object System.Drawing.Size($CONTENT_W, $PANEL_H)
$contentBase.Location = New-Object System.Drawing.Point($CONTENT_X, $CONTENT_Y)
$contentBase.BackColor = $C.ContentBg

# Helper: cria sub-painel branco com borda
function New-Card {
    param([int]$X, [int]$Y, [int]$W, [int]$H)
    $p = New-Object System.Windows.Forms.Panel
    $p.Size        = New-Object System.Drawing.Size($W, $H)
    $p.Location    = New-Object System.Drawing.Point($X, $Y)
    $p.BackColor   = $C.PanelBg
    $p.BorderStyle = "FixedSingle"
    return $p
}

# Helper: label negrito
function New-BoldLabel {
    param([string]$Text, [int]$X, [int]$Y, [int]$W = 100, [int]$H = 22)
    $l = New-Object System.Windows.Forms.Label
    $l.Text = $Text; $l.Size = New-Object System.Drawing.Size($W, $H)
    $l.Location = New-Object System.Drawing.Point($X, $Y)
    $l.Font = New-Object System.Drawing.Font("Segoe UI", 9, [System.Drawing.FontStyle]::Bold)
    return $l
}

# Helper: combobox padrao
function New-StdCombo {
    param([int]$X, [int]$Y, [int]$W = 310)
    $c = New-Object System.Windows.Forms.ComboBox
    $c.Size = New-Object System.Drawing.Size($W, 24); $c.Location = New-Object System.Drawing.Point($X, $Y)
    $c.DropDownStyle = "DropDownList"
    return $c
}

# Helper: botao colorido
function New-ActionButton {
    param([string]$Text, [int]$X, [int]$Y, [int]$W, [int]$H, [System.Drawing.Color]$Bg)
    $b = New-Object System.Windows.Forms.Button
    $b.Text = $Text; $b.Size = New-Object System.Drawing.Size($W, $H); $b.Location = New-Object System.Drawing.Point($X, $Y)
    $b.BackColor = $Bg; $b.ForeColor = [System.Drawing.Color]::White
    $b.FlatStyle = "Flat"; $b.FlatAppearance.BorderSize = 0
    $b.Font = New-Object System.Drawing.Font("Segoe UI", 9.5, [System.Drawing.FontStyle]::Bold)
    $b.Cursor = [System.Windows.Forms.Cursors]::Hand
    return $b
}

# Helper: popula combo de modelos
$fillModels = {
    param($bc, $mc)
    $mc.Items.Clear()
    $sel = $bc.SelectedItem.ToString()
    if ($script:Catalog.ContainsKey($sel)) {
        $mc.Enabled = $true
        $mc.Items.Add("-- Selecione o modelo --") | Out-Null
        $script:Catalog[$sel] | ForEach-Object { $mc.Items.Add($_) | Out-Null }
    } else {
        $mc.Items.Add("-- Selecione o fabricante primeiro --") | Out-Null
        $mc.Enabled = $false
    }
    $mc.SelectedIndex = 0
}

# =========================================================
# PAINEL USB
# =========================================================
$pnlUsb = New-Card 0 0 $CONTENT_W $PANEL_H; $pnlUsb.Visible = $true

$cardDetect = New-Card 8 8 ($CONTENT_W - 18) 55
$btnDetect  = New-ActionButton "Detectar Impressora USB" 8 12 185 30 $C.BtnBlue
$lblDetectStatus = New-Object System.Windows.Forms.Label
$lblDetectStatus.Text = "Clique para detectar automaticamente pelo VID/PID"
$lblDetectStatus.Size = New-Object System.Drawing.Size(290, 30); $lblDetectStatus.Location = New-Object System.Drawing.Point(202, 12)
$lblDetectStatus.ForeColor = [System.Drawing.Color]::Gray; $lblDetectStatus.TextAlign = "MiddleLeft"
$cardDetect.Controls.AddRange(@($btnDetect, $lblDetectStatus))

$cardUsbSel = New-Card 8 72 ($CONTENT_W - 18) 105
$lblUsbBrand = New-BoldLabel "Fabricante:" 10 15 90
$cmbUsbBrand = New-StdCombo 108 13
$cmbUsbBrand.Items.Add("-- Selecione o fabricante --") | Out-Null
foreach ($b in $script:Catalog.Keys) { $cmbUsbBrand.Items.Add($b) | Out-Null }
$cmbUsbBrand.SelectedIndex = 0
$lblUsbModel = New-BoldLabel "Modelo:" 10 55 90
$cmbUsbModel = New-StdCombo 108 53
$cmbUsbModel.Items.Add("-- Selecione o fabricante primeiro --") | Out-Null
$cmbUsbModel.SelectedIndex = 0; $cmbUsbModel.Enabled = $false
$cmbUsbBrand.Add_SelectedIndexChanged({ & $fillModels $cmbUsbBrand $cmbUsbModel })
$cardUsbSel.Controls.AddRange(@($lblUsbBrand, $cmbUsbBrand, $lblUsbModel, $cmbUsbModel))

$btnUsbInstall = New-ActionButton "INSTALAR DRIVER USB" 160 188 185 38 $C.BtnGreen
$pnlUsb.Controls.AddRange(@($cardDetect, $cardUsbSel, $btnUsbInstall))
$contentBase.Controls.Add($pnlUsb)

$btnDetect.Add_Click({
    $btnDetect.Enabled = $false; $lblDetectStatus.Text = "Detectando..."; $lblDetectStatus.ForeColor = [System.Drawing.Color]::Gray
    $form.Refresh(); $script:Log.Clear()
    $r = Find-UsbPrinter
    if ($r.Found) {
        $lblDetectStatus.Text = "VID=$($r.Vid)  PID=$($r.Pid)  |  $($r.Brand)"; $lblDetectStatus.ForeColor = [System.Drawing.Color]::FromArgb(0,160,0)
        $i = $cmbUsbBrand.Items.IndexOf($r.Brand); if ($i -ge 0) { $cmbUsbBrand.SelectedIndex = $i }
        if ($r.Model) { $m = $cmbUsbModel.Items.IndexOf($r.Model); if ($m -ge 0) { $cmbUsbModel.SelectedIndex = $m } }
    } else {
        $lblDetectStatus.Text = "Nao reconhecida — selecione manualmente abaixo"; $lblDetectStatus.ForeColor = [System.Drawing.Color]::DarkOrange
    }
    $btnDetect.Enabled = $true
})

$btnUsbInstall.Add_Click({
    $brand = $cmbUsbBrand.SelectedItem.ToString(); $model = $cmbUsbModel.SelectedItem.ToString()
    if ($brand -like "--*") { [System.Windows.Forms.MessageBox]::Show("Selecione um fabricante.", "Atencao", "OK", "Warning") | Out-Null; return }
    if ($model -like "--*") { [System.Windows.Forms.MessageBox]::Show("Selecione um modelo.", "Atencao", "OK", "Warning") | Out-Null; return }
    $btnUsbInstall.Enabled = $false; $btnUsbInstall.Text = "Instalando..."; $form.Refresh()
    $script:Log.Clear(); Write-Log "=== USB: $brand $model ===" "Cyan"
    $result = Install-PrinterDriver -Brand $brand -Model $model
    Write-Log "==============================" "Gray"
    if ($result.Success) {
        Write-Log "CONCLUIDO!" "Lime"
        Start-Sleep -Milliseconds 800
        $p = Get-Printer -EA SilentlyContinue | Where-Object { $_.Name -like "*$brand*" -or $_.Name -like "*$model*" } | Select-Object -First 1
        if ($p) { $script:LastPrinter = $p.Name; Write-Log "Impressora: $($p.Name)" "Lime"; Update-StatusBar }
        [System.Windows.Forms.MessageBox]::Show("Driver instalado!`n`n$brand  $model", "Sucesso", "OK", "Information") | Out-Null
    } else {
        Write-Log "FALHOU. Verifique o log." "Red"
        [System.Windows.Forms.MessageBox]::Show("Falha. Verifique o log.", "Erro", "OK", "Error") | Out-Null
    }
    $btnUsbInstall.Enabled = $true; $btnUsbInstall.Text = "INSTALAR DRIVER USB"
})

# =========================================================
# PAINEL REDE
# =========================================================
$pnlNet = New-Card 0 0 $CONTENT_W $PANEL_H; $pnlNet.Visible = $false

$cardConn = New-Card 8 8 ($CONTENT_W - 18) 60
$lblNetIp = New-BoldLabel "IP:" 10 17 30
$txtNetIp = New-Object System.Windows.Forms.TextBox; $txtNetIp.Size = New-Object System.Drawing.Size(148, 24); $txtNetIp.Location = New-Object System.Drawing.Point(44, 15); $txtNetIp.Text = "192.168.1."
$lblNetPort = New-BoldLabel "Porta:" 205 17 46
$txtNetPort = New-Object System.Windows.Forms.TextBox; $txtNetPort.Size = New-Object System.Drawing.Size(58, 24); $txtNetPort.Location = New-Object System.Drawing.Point(255, 15); $txtNetPort.Text = "9100"
$lblNetName = New-BoldLabel "Nome:" 325 17 46
$txtNetName = New-Object System.Windows.Forms.TextBox; $txtNetName.Size = New-Object System.Drawing.Size(148, 24); $txtNetName.Location = New-Object System.Drawing.Point(375, 15); $txtNetName.Text = "Impressora Rede"
$cardConn.Controls.AddRange(@($lblNetIp, $txtNetIp, $lblNetPort, $txtNetPort, $lblNetName, $txtNetName))

$cardNetSel = New-Card 8 78 ($CONTENT_W - 18) 105
$lblNetBrand = New-BoldLabel "Fabricante:" 10 15 90
$cmbNetBrand = New-StdCombo 108 13
$cmbNetBrand.Items.Add("-- Selecione o fabricante --") | Out-Null
foreach ($b in $script:Catalog.Keys) { $cmbNetBrand.Items.Add($b) | Out-Null }
$cmbNetBrand.SelectedIndex = 0
$lblNetModel = New-BoldLabel "Modelo:" 10 55 90
$cmbNetModel = New-StdCombo 108 53
$cmbNetModel.Items.Add("-- Selecione o fabricante primeiro --") | Out-Null
$cmbNetModel.SelectedIndex = 0; $cmbNetModel.Enabled = $false
$cmbNetBrand.Add_SelectedIndexChanged({ & $fillModels $cmbNetBrand $cmbNetModel })
$cmbNetModel.Add_SelectedIndexChanged({
    $b = $cmbNetBrand.SelectedItem.ToString(); $m = $cmbNetModel.SelectedItem.ToString()
    if (-not ($b -like "--*") -and -not ($m -like "--*")) { $txtNetName.Text = "$b $m ($($txtNetIp.Text.Trim()))" }
})
$cardNetSel.Controls.AddRange(@($lblNetBrand, $cmbNetBrand, $lblNetModel, $cmbNetModel))

$btnNetInstall = New-ActionButton "INSTALAR EM REDE" 148 195 200 38 $C.BtnBlue
$pnlNet.Controls.AddRange(@($cardConn, $cardNetSel, $btnNetInstall))
$contentBase.Controls.Add($pnlNet)

$btnNetInstall.Add_Click({
    $brand = $cmbNetBrand.SelectedItem.ToString(); $model = $cmbNetModel.SelectedItem.ToString()
    $ip = $txtNetIp.Text.Trim(); $port = [int]($txtNetPort.Text.Trim()); $name = $txtNetName.Text.Trim()
    if ($brand -like "--*") { [System.Windows.Forms.MessageBox]::Show("Selecione um fabricante.", "Atencao", "OK", "Warning") | Out-Null; return }
    if ($model -like "--*") { [System.Windows.Forms.MessageBox]::Show("Selecione um modelo.", "Atencao", "OK", "Warning") | Out-Null; return }
    if (-not $ip) { [System.Windows.Forms.MessageBox]::Show("Digite o IP.", "Atencao", "OK", "Warning") | Out-Null; return }
    if ($port -le 0 -or $port -gt 65535) { $port = 9100 }
    $btnNetInstall.Enabled = $false; $btnNetInstall.Text = "Instalando..."; $form.Refresh()
    $script:Log.Clear(); Write-Log "=== Rede: $brand $model  $ip`:$port ===" "Cyan"
    $ok = Add-NetworkPrinter -Brand $brand -Model $model -Ip $ip -Port $port -Name $name
    Write-Log "==============================" "Gray"
    if ($ok) {
        Write-Log "IMPRESSORA DE REDE INSTALADA!" "Lime"
        [System.Windows.Forms.MessageBox]::Show("Impressora adicionada!`n`n$name  ($ip)", "Sucesso", "OK", "Information") | Out-Null
    } else {
        Write-Log "FALHOU. Verifique o log." "Red"
        [System.Windows.Forms.MessageBox]::Show("Falha. Verifique o log.", "Erro", "OK", "Error") | Out-Null
    }
    $btnNetInstall.Enabled = $true; $btnNetInstall.Text = "INSTALAR EM REDE"
})

# =========================================================
# PAINEL SPOOLER
# =========================================================
$pnlSpooler = New-Card 0 0 $CONTENT_W $PANEL_H; $pnlSpooler.Visible = $false

$cardSvcStatus = New-Card 8 8 ($CONTENT_W - 18) 40
$script:lblSpoolerSvc = New-Object System.Windows.Forms.Label
$script:lblSpoolerSvc.Text = "Verificando..."; $script:lblSpoolerSvc.Size = New-Object System.Drawing.Size(260, 28)
$script:lblSpoolerSvc.Location = New-Object System.Drawing.Point(10, 6)
$script:lblSpoolerSvc.Font = New-Object System.Drawing.Font("Segoe UI", 10, [System.Drawing.FontStyle]::Bold)
$cardSvcStatus.Controls.Add($script:lblSpoolerSvc)

$lblQueueTitle = New-BoldLabel "Fila de impressao:" 8 58 200
$script:lstQueue = New-Object System.Windows.Forms.ListBox
$script:lstQueue.Size = New-Object System.Drawing.Size($CONTENT_W - 18, 120)
$script:lstQueue.Location = New-Object System.Drawing.Point(8, 80)
$script:lstQueue.Font = New-Object System.Drawing.Font("Consolas", 8.5)
$script:lstQueue.BackColor = [System.Drawing.Color]::FromArgb(245, 245, 250)
$script:lstQueue.BorderStyle = "FixedSingle"

$btnRefreshSpooler  = New-ActionButton "Atualizar"         8   215  110 34 $C.BtnBlue
$btnRestartSpooler  = New-ActionButton "Reiniciar Spooler" 128 215  155 34 $C.BtnPurple
$btnClearQueue      = New-ActionButton "Limpar Fila"       293 215  140 34 $C.BtnOrange

$pnlSpooler.Controls.AddRange(@($cardSvcStatus, $lblQueueTitle, $script:lstQueue, $btnRefreshSpooler, $btnRestartSpooler, $btnClearQueue))
$contentBase.Controls.Add($pnlSpooler)

$btnRefreshSpooler.Add_Click({ Refresh-SpoolerPanel })
$btnRestartSpooler.Add_Click({
    $script:Log.Clear(); Write-Log "=== Reiniciando Spooler ===" "Cyan"
    Reset-Spooler -ClearQueue $false
})
$btnClearQueue.Add_Click({
    $r = [System.Windows.Forms.MessageBox]::Show("Limpar toda a fila e reiniciar o Spooler?", "Confirmar", "YesNo", "Warning")
    if ($r -eq "Yes") { $script:Log.Clear(); Write-Log "=== Limpando Fila ===" "Cyan"; Reset-Spooler -ClearQueue $true }
})

# =========================================================
# PAINEL TESTE
# =========================================================
$pnlTest = New-Card 0 0 $CONTENT_W $PANEL_H; $pnlTest.Visible = $false

$lblPrinterList = New-BoldLabel "Impressoras instaladas:" 8 8 200
$script:lstPrinters = New-Object System.Windows.Forms.ListBox
$script:lstPrinters.Size = New-Object System.Drawing.Size($CONTENT_W - 18, 145)
$script:lstPrinters.Location = New-Object System.Drawing.Point(8, 32)
$script:lstPrinters.Font = New-Object System.Drawing.Font("Consolas", 8.5)
$script:lstPrinters.BackColor = [System.Drawing.Color]::FromArgb(245, 245, 250)
$script:lstPrinters.BorderStyle = "FixedSingle"

$btnRefreshPrinters = New-ActionButton "Atualizar Lista"    8   190 140 34 $C.BtnBlue
$btnSendTest        = New-ActionButton "Imprimir Teste"     160 190 155 34 $C.BtnTeal
$btnSetDefault      = New-ActionButton "Definir Padrao"     326 190 140 34 ([System.Drawing.Color]::FromArgb(80, 80, 80))

$pnlTest.Controls.AddRange(@($lblPrinterList, $script:lstPrinters, $btnRefreshPrinters, $btnSendTest, $btnSetDefault))
$contentBase.Controls.Add($pnlTest)

$btnRefreshPrinters.Add_Click({ Refresh-TestPanel })

$btnSendTest.Add_Click({
    $idx = $script:lstPrinters.SelectedIndex
    if ($idx -lt 0 -or $script:PrinterList.Count -eq 0 -or $idx -ge $script:PrinterList.Count) {
        [System.Windows.Forms.MessageBox]::Show("Selecione uma impressora da lista.", "Atencao", "OK", "Warning") | Out-Null; return
    }
    $printerName = $script:PrinterList[$idx].Name
    $script:Log.Clear(); Write-Log "=== Teste ===" "Cyan"
    Invoke-TestPage -PrinterName $printerName
})

$btnSetDefault.Add_Click({
    $idx = $script:lstPrinters.SelectedIndex
    if ($idx -lt 0 -or $script:PrinterList.Count -eq 0 -or $idx -ge $script:PrinterList.Count) {
        [System.Windows.Forms.MessageBox]::Show("Selecione uma impressora da lista.", "Atencao", "OK", "Warning") | Out-Null; return
    }
    $name = $script:PrinterList[$idx].Name
    try {
        (Get-WmiObject Win32_Printer -Filter "Name='$($name -replace "'","\'")'" -EA Stop).SetDefaultPrinter() | Out-Null
        Write-Log "Impressora padrao definida: $name" "Lime"
        [System.Windows.Forms.MessageBox]::Show("Impressora padrao: $name", "Definido", "OK", "Information") | Out-Null
    } catch { Write-Log "Erro: $_" "Red" }
})

# =========================================================
# LOG
# =========================================================
$lblLog           = New-Object System.Windows.Forms.Label
$lblLog.Text      = "Log:"
$lblLog.Size      = New-Object System.Drawing.Size(60, 18)
$lblLog.Location  = New-Object System.Drawing.Point($CONTENT_X, $LOG_Y)
$lblLog.Font      = New-Object System.Drawing.Font("Segoe UI", 8.5, [System.Drawing.FontStyle]::Bold)
$lblLog.ForeColor = [System.Drawing.Color]::DimGray

$script:Log              = New-Object System.Windows.Forms.RichTextBox
$script:Log.Size         = New-Object System.Drawing.Size($CONTENT_W, $LOG_H)
$script:Log.Location     = New-Object System.Drawing.Point($CONTENT_X, $LOG_Y + 20)
$script:Log.ReadOnly     = $true
$script:Log.BackColor    = $C.LogBg
$script:Log.ForeColor    = [System.Drawing.Color]::White
$script:Log.Font         = New-Object System.Drawing.Font("Consolas", 8.5)
$script:Log.BorderStyle  = "None"
$script:Log.ScrollBars   = "Vertical"

# =========================================================
# STATUS BAR (sempre visivel)
# =========================================================
$statusBar           = New-Object System.Windows.Forms.Panel
$statusBar.Size      = New-Object System.Drawing.Size($FW, $STATUS_H)
$statusBar.Location  = New-Object System.Drawing.Point(0, $FH - $STATUS_H)
$statusBar.BackColor = $C.StatusBg

# Indicador (bolinha colorida)
$script:lblStatusDot = New-Object System.Windows.Forms.Label
$script:lblStatusDot.Size     = New-Object System.Drawing.Size(14, 14)
$script:lblStatusDot.Location = New-Object System.Drawing.Point(16, 20)
$script:lblStatusDot.BackColor = [System.Drawing.Color]::FromArgb(80, 80, 100)

$script:lblStatusPrinter = New-Object System.Windows.Forms.Label
$script:lblStatusPrinter.Text      = "Nenhuma impressora instalada nesta sessao"
$script:lblStatusPrinter.ForeColor = $C.StatusText
$script:lblStatusPrinter.Size      = New-Object System.Drawing.Size(290, 22)
$script:lblStatusPrinter.Location  = New-Object System.Drawing.Point(38, 16)
$script:lblStatusPrinter.Font      = New-Object System.Drawing.Font("Segoe UI", 8.5)

$btnQkSpooler = New-ActionButton "Reiniciar Spooler" 345 10 148 34 $C.BtnPurple
$btnQkClear   = New-ActionButton "Limpar Fila"       502 10 130 34 $C.BtnOrange
$statusBar.Controls.AddRange(@($script:lblStatusDot, $script:lblStatusPrinter, $btnQkSpooler, $btnQkClear))

$btnQkSpooler.Add_Click({
    $script:Log.Clear(); Write-Log "=== Reiniciando Spooler ===" "Cyan"; Reset-Spooler -ClearQueue $false
})
$btnQkClear.Add_Click({
    $r = [System.Windows.Forms.MessageBox]::Show("Limpar toda a fila e reiniciar o Spooler?", "Confirmar", "YesNo", "Warning")
    if ($r -eq "Yes") { $script:Log.Clear(); Write-Log "=== Limpando Fila ===" "Cyan"; Reset-Spooler -ClearQueue $true }
})

# =========================================================
# NAVEGACAO
# =========================================================
function Show-Panel {
    param($panel, $navBtn, [scriptblock]$OnShow = $null)
    $script:AllPanels | ForEach-Object { $_.Visible = $false }
    $panel.Visible = $true
    Select-Nav $navBtn
    if ($OnShow) { & $OnShow }
}

$script:AllPanels = @($pnlUsb, $pnlNet, $pnlSpooler, $pnlTest)

$navUsb.Add_Click({     Show-Panel $pnlUsb     $navUsb })
$navNet.Add_Click({     Show-Panel $pnlNet     $navNet })
$navSpooler.Add_Click({ Show-Panel $pnlSpooler $navSpooler { Refresh-SpoolerPanel } })
$navTest.Add_Click({    Show-Panel $pnlTest    $navTest    { Refresh-TestPanel    } })

# =========================================================
# MONTA FORM
# =========================================================
$form.Controls.AddRange(@(
    $header,
    $sidebar,
    $divider,
    $contentBase,
    $lblLog, $script:Log,
    $statusBar
))

# Estado inicial: USB selecionado
Select-Nav $navUsb

Write-Log "PrinterInstaller V3 pronto." "Lime"
Write-Log "Navegue pelo menu lateral para acessar as funcoes." "White"
Write-Log "Drivers devem estar em: Drivers\Fabricante\Modelo\" "Gray"

[System.Windows.Forms.Application]::Run($form)
