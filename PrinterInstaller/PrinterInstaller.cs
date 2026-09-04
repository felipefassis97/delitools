// Delitools v2.12.0
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Management;
using System.IO.Ports;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

static class Program {
    [STAThread]
    static void Main() {
        AppDomain.CurrentDomain.UnhandledException += (s, e) => {
            var ex = e.ExceptionObject as Exception;
            MessageBox.Show("Erro inesperado (nao tratado):\n\n"+(ex!=null?ex.GetType().Name+": "+ex.Message+"\n\n"+ex.StackTrace:e.ExceptionObject),"Erro",MessageBoxButtons.OK,MessageBoxIcon.Error);
        };
        try {
            if (!IsAdmin()) {
                try { Process.Start(new ProcessStartInfo { FileName=Application.ExecutablePath, Verb="runas", UseShellExecute=true }); }
                catch (Exception ex) { MessageBox.Show("Erro ao elevar:\n"+ex.Message+"\n\nExecute como Administrador.","Permissao",MessageBoxButtons.OK,MessageBoxIcon.Error); }
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => {
                MessageBox.Show("Erro inesperado:\n\n"+e.Exception.GetType().Name+": "+e.Exception.Message+"\n\n"+e.Exception.StackTrace,"Erro",MessageBoxButtons.OK,MessageBoxIcon.Error);
            };
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.Run(new MainForm());
        } catch (Exception ex) {
            MessageBox.Show("Erro fatal:\n\n"+ex.GetType().Name+": "+ex.Message+"\n\n"+ex.StackTrace,"Erro",MessageBoxButtons.OK,MessageBoxIcon.Error);
        }
    }
    static bool IsAdmin() {
        using (var id=WindowsIdentity.GetCurrent())
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

class MainForm : Form {
    // ── Win32 ────────────────────────────────────────────────
    [DllImport("Gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int x1,int y1,int x2,int y2,int cx,int cy);
    [DllImport("winspool.Drv",EntryPoint="OpenPrinterA",  SetLastError=true)] static extern bool OpenPrinter(string n,out IntPtr h,IntPtr d);
    [DllImport("winspool.Drv",EntryPoint="ClosePrinter")]                     static extern bool ClosePrinter(IntPtr h);
    [DllImport("winspool.Drv",EntryPoint="StartDocPrinterA",SetLastError=true)] static extern int StartDocPrinter(IntPtr h,int lv,ref DOCINFOA di);
    [DllImport("winspool.Drv",EntryPoint="EndDocPrinter")]                    static extern bool EndDocPrinter(IntPtr h);
    [DllImport("winspool.Drv",EntryPoint="StartPagePrinter")]                 static extern bool StartPagePrinter(IntPtr h);
    [DllImport("winspool.Drv",EntryPoint="EndPagePrinter")]                   static extern bool EndPagePrinter(IntPtr h);
    [DllImport("winspool.Drv",EntryPoint="WritePrinter",  SetLastError=true)] static extern bool WritePrinter(IntPtr h,IntPtr buf,int n,out int w);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Ansi)]
    struct DOCINFOA { [MarshalAs(UnmanagedType.LPStr)] public string pDocName; [MarshalAs(UnmanagedType.LPStr)] public string pOutputFile; [MarshalAs(UnmanagedType.LPStr)] public string pDataType; }

    // ── Automacao de UI (ferramentas de fabricante sem protocolo nativo) ──────
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd,int Msg,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll",EntryPoint="SendMessage")] static extern IntPtr SendMessageLV(IntPtr hWnd,int Msg,IntPtr wParam,ref LVITEM lParam);
    [DllImport("user32.dll",CharSet=CharSet.Auto)] static extern int GetWindowText(IntPtr hWnd,System.Text.StringBuilder text,int count);
    [DllImport("user32.dll",CharSet=CharSet.Auto)] static extern int GetClassNameW(IntPtr hWnd,System.Text.StringBuilder text,int count);
    [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr hWndParent,EnumChildProc lpEnumFunc,IntPtr lParam);
    delegate bool EnumChildProc(IntPtr hWnd,IntPtr lParam);
    const int BM_CLICK=0x00F5;
    const int WM_GETTEXT=0x000D, WM_SETTEXT=0x000C;
    const int LVM_GETITEMCOUNT=0x1004, LVM_SETITEMSTATE=0x102B;
    const int LVIF_STATE=0x0008, LVIS_SELECTED=0x0002, LVIS_FOCUSED=0x0001;
    const int IPM_SETADDRESS=0x0466, IPM_GETADDRESS=0x0467;
    [StructLayout(LayoutKind.Sequential)]
    struct LVITEM{ public int mask,iItem,iSubItem,state,stateMask; public IntPtr pszText; public int cchTextMax,iImage; public IntPtr lParam; public int iIndent; }

    struct WinInfo{ public IntPtr Handle,Parent; public string ClassName,Text; public int Id; }

    // Enumera TODA a arvore de janelas-filhas (recursivo) — usado em vez de FindWindowEx por
    // titulo, que se mostrou pouco confiavel contra dialogos MFC antigos (paginas de aba
    // reaproveitam a mesma classe "#32770" varias vezes e o titulo exato as vezes nao bate).
    List<WinInfo> EnumAllChildren(IntPtr root){
        var list=new List<WinInfo>();
        EnumChildWindows(root,(h,l)=>{
            var cls=new System.Text.StringBuilder(256); GetClassNameW(h,cls,256);
            var txt=new System.Text.StringBuilder(256); GetWindowText(h,txt,256);
            list.Add(new WinInfo{Handle=h,Parent=GetParent(h),ClassName=cls.ToString(),Text=txt.ToString(),Id=GetDlgCtrlID(h)});
            return true;
        },IntPtr.Zero);
        return list;
    }
    IntPtr FindByText(List<WinInfo> all,string text){ foreach(var w in all) if(w.Text==text) return w.Handle; return IntPtr.Zero; }
    IntPtr FindByIdParent(List<WinInfo> all,IntPtr parent,int id){ foreach(var w in all) if(w.Parent==parent&&w.Id==id) return w.Handle; return IntPtr.Zero; }
    string GetCtrlText(IntPtr h){ var sb=new System.Text.StringBuilder(256); GetWindowText(h,sb,256); return sb.ToString(); }
    void ClickCtrl(IntPtr h){ SendMessage(h,BM_CLICK,IntPtr.Zero,IntPtr.Zero); }
    void SetIpControl(IntPtr h,string ip){
        var parts=ip.Split('.'); if(parts.Length!=4) return;
        byte b1,b2,b3,b4;
        byte.TryParse(parts[0],out b1); byte.TryParse(parts[1],out b2); byte.TryParse(parts[2],out b3); byte.TryParse(parts[3],out b4);
        int packed=(b1<<24)|(b2<<16)|(b3<<8)|b4;
        SendMessage(h,IPM_SETADDRESS,IntPtr.Zero,(IntPtr)packed);
    }
    void SelectFirstListItem(IntPtr hList){
        var it=new LVITEM{mask=LVIF_STATE,iItem=0,iSubItem=0,state=LVIS_SELECTED|LVIS_FOCUSED,stateMask=LVIS_SELECTED|LVIS_FOCUSED};
        SendMessageLV(hList,LVM_SETITEMSTATE,(IntPtr)0,ref it);
    }

    // ── Layout ───────────────────────────────────────────────
    const string APP_VERSION     = "2.12.0";
    const string VERSION_URL     = "https://drive.google.com/uc?export=download&id=1PF2Ck2yDEUHwPl7H2BCR5pZjFqde_6Ug";
    const string DOWNLOAD_URL    = "https://drive.google.com/uc?export=download&id=1dbwNxN2R81TCHz1N-tcT4vS2-ohvqFs7";

    const int FW=1300,FH=800,SW=240,CX=242,CW=1058,CM=20,CG=12;
    static int CRD { get { return (CW-CM*2-CG*2)/3; } }

    // ── Colors ───────────────────────────────────────────────
    static readonly Color Cside  =Color.FromArgb(20,22,28);
    static readonly Color CsideH =Color.FromArgb(35,38,48);
    static readonly Color CsideT =Color.FromArgb(150,160,175);
    static readonly Color Cacc   =Color.FromArgb(52,168,83);
    static readonly Color Cbg    =Color.FromArgb(245,247,250);
    static readonly Color Ccard  =Color.White;
    static readonly Color Cbord  =Color.FromArgb(218,220,226);
    static readonly Color Ctxt   =Color.FromArgb(32,33,36);
    static readonly Color Csub   =Color.FromArgb(95,99,104);
    static readonly Color Cerr   =Color.FromArgb(220,53,69);
    static readonly Color Cblue  =Color.FromArgb(26,115,232);
    static readonly Color Corange=Color.FromArgb(180,100,0);
    static readonly Color Cpurple=Color.FromArgb(110,60,160);

    // Usado so para sugerir um nome ao detectar via USB (VID:PID) — instalacao e sempre com driver generico.
    static readonly Dictionary<string,string> VidPidMap=new Dictionary<string,string>{
        {"04B8:0202","Epson TM-T88V"}, {"04B8:0E28","Epson TM-T20"},
        {"04B8:0E15","Epson TM-T20"},  {"04B8:0007","Epson TM-U220"},
        {"0DD4:0003","Bematech MP-4200 TH"},
    };

    // ── Classificacao de dispositivo USB (portado do FudoPrintDoctor) ──────────
    // Decide se um dispositivo USB e realmente uma impressora, com nivel de certeza,
    // pra evitar o classico falso-positivo de confundir mouse/hub/webcam com impressora.
    static readonly HashSet<string> PrinterVids=new HashSet<string>(StringComparer.OrdinalIgnoreCase){
        "04B8","1504","0519","2730","0A5F","0DD4","03F0","04A9","04F9","0924","043D","04E8"
    };
    static readonly Regex PrinterWordRx=new Regex(@"\b(printer|impressora|thermal|termica|receipt|ticket|comandera|usbprint|escpos|esc/pos)\b|\bPOS\b|\bPOS-?\d|\b(xp-?\d{2,3}|srp-?\d{2,3}|rpt-?\d{2,3}|tm-?[tu]?\d{2,3}|5890|80c|58mm|80mm)\b",RegexOptions.IgnoreCase);
    static readonly Regex NonPrinterWordRx=new Regex(@"\b(mouse|mice|keyboard|teclado|hub|composite|compuesto|camera|webcam|audio|speaker|headset|micro[fp]ono|mass storage|armazenamento|disk|disco|flash|bluetooth|wireless receiver|receptor|hid|human interface|joystick|gamepad|scanner|escaner|network|ethernet|wi-?fi|modem|card reader|leitor de cartao|fingerprint|monitor|display|touch|graphics|serial converter|root hub|host controller)\b",RegexOptions.IgnoreCase);

    struct PrinterCertainty{ public bool IsPrinter; public string Confidence,Reason; }
    PrinterCertainty ClassifyUsbDevice(string name,string instanceId,string pnpClass,string service,string compatibleIds){
        name=name??""; instanceId=instanceId??""; pnpClass=pnpClass??""; service=service??""; compatibleIds=compatibleIds??"";
        if(Regex.IsMatch(instanceId,@"^USBPRINT\\",RegexOptions.IgnoreCase)) return new PrinterCertainty{IsPrinter=true,Confidence="alta",Reason="interface USBPRINT (usbprint.sys)"};
        if(pnpClass.Equals("Printer",StringComparison.OrdinalIgnoreCase)) return new PrinterCertainty{IsPrinter=true,Confidence="alta",Reason="classe de dispositivo Printer"};
        if(Regex.IsMatch(service,@"^usbprint$",RegexOptions.IgnoreCase)) return new PrinterCertainty{IsPrinter=true,Confidence="alta",Reason="driver usbprint"};
        if(compatibleIds.IndexOf("USB\\Class_07",StringComparison.OrdinalIgnoreCase)>=0) return new PrinterCertainty{IsPrinter=true,Confidence="alta",Reason="classe USB 07h (Printer)"};

        string probe=name+" "+instanceId;
        if(NonPrinterWordRx.IsMatch(name)&&!PrinterWordRx.IsMatch(name)) return new PrinterCertainty{IsPrinter=false,Confidence="alta",Reason="o nome corresponde a outro tipo de dispositivo"};

        string vid=""; var mv=Regex.Match(instanceId,@"VID_([0-9A-F]{4})",RegexOptions.IgnoreCase);
        if(mv.Success) vid=mv.Groups[1].Value.ToUpper();
        if(vid.Length>0&&PrinterVids.Contains(vid)) return new PrinterCertainty{IsPrinter=true,Confidence="media",Reason="VID_"+vid+" e de um fabricante de impressoras"};
        if(PrinterWordRx.IsMatch(probe)) return new PrinterCertainty{IsPrinter=true,Confidence="baixa",Reason="o nome menciona impressora/POS"};
        return new PrinterCertainty{IsPrinter=false,Confidence="alta",Reason="sem nenhum sinal de impressora"};
    }
    struct UsbPrinterCandidate{ public string Name,InstanceId,Confidence,Reason; }
    List<UsbPrinterCandidate> ScanUsbPrinterCandidates(){
        var result=new List<UsbPrinterCandidate>();
        try{
            foreach(ManagementObject o in new ManagementObjectSearcher("SELECT * FROM Win32_PnPEntity").Get()){
                string devId=o["DeviceID"]!=null?o["DeviceID"].ToString():"";
                if(devId.IndexOf("USB",StringComparison.OrdinalIgnoreCase)<0) continue;
                string name=o["Name"]!=null?o["Name"].ToString():"";
                string pnpClass=o["PNPClass"]!=null?o["PNPClass"].ToString():"";
                string service=o["Service"]!=null?o["Service"].ToString():"";
                string compat="";
                try{ var ci=o["CompatibleID"] as string[]; if(ci!=null) compat=string.Join(" ",ci); }catch{}
                var c=ClassifyUsbDevice(name,devId,pnpClass,service,compat);
                if(c.IsPrinter) result.Add(new UsbPrinterCandidate{Name=name,InstanceId=devId,Confidence=c.Confidence,Reason=c.Reason});
            }
        }catch{}
        return result;
    }

    // ── Impressoras virtuais (portado do FudoPrintDoctor) ───────────────────────
    // PDF/XPS/Fax/OneNote e afins nao sao alvo real de diagnostico/instalacao.
    static readonly string[] VirtualNamePatterns=new string[]{
        "Microsoft Print to PDF","Microsoft XPS Document Writer","OneNote","Send To OneNote",
        "Impressora virtual protegida","Fax","Adobe PDF","PDF24","CutePDF","PDFCreator","Bullzip",
        "doPDF","Foxit.*PDF","Nitro.*PDF","novaPDF","PrimoPDF","Snagit","AnyDesk","TeamViewer",
        "WPS PDF","Print to Evernote","Salvar como PDF","Microsoft Shared Fax","Quicken PDF","ImagePrinter"
    };
    static readonly string[] VirtualDriverPatterns=new string[]{
        "Microsoft Print To PDF","Microsoft XPS Document Writer","Send to Microsoft OneNote",
        "Microsoft Shared Fax Driver","PDF","XPS"
    };
    static readonly string[] VirtualPortPatterns=new string[]{
        @"^PORTPROMPT:",@"^nul:?$",@"^NUL$",@"^SHRFAX:",@"^XPSPort:",@"^FILE:",@"^Microsoft\.Office\.OneNote",
        @"^PDF",@"^C:\\",@"^\\\\"
    };
    bool IsVirtualPrinter(string name,string driverName,string portName,out string reason){
        name=name??""; driverName=driverName??""; portName=portName??"";
        foreach(var pat in VirtualNamePatterns) if(Regex.IsMatch(name,pat,RegexOptions.IgnoreCase)){ reason="nome bate com impressora virtual ("+pat+")"; return true; }
        foreach(var pat in VirtualDriverPatterns) if(Regex.IsMatch(driverName,pat,RegexOptions.IgnoreCase)){ reason="driver virtual ("+driverName+")"; return true; }
        foreach(var pat in VirtualPortPatterns) if(Regex.IsMatch(portName,pat,RegexOptions.IgnoreCase)){ reason="porta nao fisica ("+portName+")"; return true; }
        reason=""; return false;
    }
    // Impressoras reais (exclui PDF/XPS/Fax/etc) — usado nos fluxos do chat, onde faz sentido
    // esconder impressoras virtuais das opcoes (testar, remover, gaveta...). GetPrinters() sem
    // filtro continua valendo pra backup/restauracao e pro inventario da tela manual.
    string[] GetRealPrinters(){
        var real=new List<string>();
        try{
            foreach(ManagementObject o in new ManagementObjectSearcher("SELECT * FROM Win32_Printer").Get()){
                string n=o["Name"]!=null?o["Name"].ToString():"";
                if(n.Length==0) continue;
                string drv=o["DriverName"]!=null?o["DriverName"].ToString():"";
                string pt=o["PortName"]!=null?o["PortName"].ToString():"";
                string reason;
                if(IsVirtualPrinter(n,drv,pt,out reason)) continue;
                real.Add(n);
            }
        }catch{ return GetPrinters(); }
        return real.ToArray();
    }

    // ── State ────────────────────────────────────────────────
    bool connUsb=true; int activePage=0;

    // ── Controls ─────────────────────────────────────────────
    Panel[]  pages,navItems; Label[] navLabels; int[] navOrder;
    bool manualModeUnlocked=false;
    const string TechModePassword="delitools"; // troque aqui se quiser outra senha do Modo Tecnico
    // Paleta propria do Assistente (chat) — deliberadamente diferente do resto do app,
    // pra marcar que e o modo principal de uso, mas ainda dentro do mesmo tom profissional.
    static readonly Color CaiBg    =Color.FromArgb(24,27,43);
    static readonly Color CaiAccent=Color.FromArgb(88,101,242);
    static readonly Color CaiBubble=Color.FromArgb(236,238,246);
    ListBox  lstInstalled,lstTest;
    Panel    pnlDetected,pnlUsbInfo,pnlNetInfo,pnlUsbDetect,pnlNetScan;
    Label    lblDetName,lblDetVid,lblNoDetect,lblNetScanStatus;
    ListBox  lstNetFound;
    Button   btnScanNet;
    RichTextBox portsBox,usbDevBox;
    RichTextBox logBox;
    Label    lblSpoolerDot,lblSpoolerTxt,lblQueueTxt,lblToolsSpooler,lblToolsQueue;
    Panel[]  stepCircles=new Panel[3];
    Button   btnInstall;
    ComboBox cmbDrawerPrinter;
    TextBox  txtPrinterName,txtIpAddress,txtPortNum,txtPingIp,txtPingPort;
    CheckBox chkSetDefault;
    System.Windows.Forms.Timer refreshTimer;
    string tempIpActive,tempIpAdapter; // IP secundario temporario (Config IP) ainda nao confirmado/removido
    Panel pnlChatLog; int chatY,chatWidth; // area de conversa do Assistente (chat)
    SerialPort scalePort;
    System.Text.StringBuilder scaleBuf=new System.Text.StringBuilder();
    readonly object scaleBufLock=new object();
    ComboBox cmbComPorts,cmbBaudRate,cmbScaleProtocol;
    Label lblWeight,lblWeightUnit,lblScaleStatus;
    RichTextBox scaleLog;
    Button btnScaleConnect;
    ListBox lstComDev;

    // ── Constructor ──────────────────────────────────────────
    public MainForm() {
        Text="Delitools"; ClientSize=new Size(FW,FH);
        StartPosition=FormStartPosition.CenterScreen;
        FormBorderStyle=FormBorderStyle.Sizable; MaximizeBox=true; MinimizeBox=true;
        BackColor=Cbg; Font=new Font("Segoe UI",9f);
        SuspendLayout(); Build(); ResumeLayout(false); PerformLayout();
        MinimumSize=Size; // nao deixa encolher abaixo do layout desenhado (evita cortar conteudo)
        FormClosing+=(s,e)=>{ if(scalePort!=null&&scalePort.IsOpen){scalePort.Close();scalePort.Dispose();} if(tempIpActive!=null){try{RemoveTempIp(tempIpAdapter,tempIpActive);}catch{}} };
        ShowPage(8); RefreshStatus(); Log("Delitools v2.12.0 iniciado."); // Assistente e a tela inicial
        refreshTimer=new System.Windows.Forms.Timer(); refreshTimer.Interval=8000;
        refreshTimer.Tick+=(s,e)=>RefreshStatus(); refreshTimer.Start();
        ThreadPool.QueueUserWorkItem(delegate(object state){
            DetRes? r=DoDetect();
            if(r!=null) BeginInvoke((Action)(()=>ApplyDetect(r)));
            BeginInvoke((Action)(()=>Log("Spooler: "+GetSpoolerStatus())));
        });
        ThreadPool.QueueUserWorkItem(delegate(object state){ CheckForUpdates(); });
    }

    void CheckForUpdates(){
        try{
            if(VERSION_URL.StartsWith("COLE")) return;
            string latest;
            using(var wc=new WebClient()){ wc.Headers["User-Agent"]="Delitools"; latest=wc.DownloadString(VERSION_URL).Trim(); }
            if(string.IsNullOrEmpty(latest)) return;
            if(new Version(latest)>new Version(APP_VERSION)){
                BeginInvoke((Action)(()=>{
                    Log("Nova versao disponivel: v"+latest);
                    var strip=new Panel{BackColor=Color.FromArgb(52,168,83),Dock=DockStyle.Top,Height=36,Cursor=Cursors.Hand};
                    var lbl=new Label{Text="  Nova versao disponivel: v"+latest+"  —  clique aqui para baixar",
                        Font=new Font("Segoe UI",9,FontStyle.Bold),ForeColor=Color.White,
                        Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft};
                    var btnX=new Button{Text="X",FlatStyle=FlatStyle.Flat,ForeColor=Color.White,BackColor=Color.Transparent,
                        Dock=DockStyle.Right,Width=36,Font=new Font("Segoe UI",9,FontStyle.Bold),Cursor=Cursors.Hand};
                    btnX.FlatAppearance.BorderSize=0;
                    btnX.Click+=(s,e)=>{ Controls.Remove(strip); };
                    strip.Click+=(s,e)=>{ try{Process.Start(DOWNLOAD_URL);}catch{} };
                    lbl.Click+=(s,e)=>{ try{Process.Start(DOWNLOAD_URL);}catch{} };
                    strip.Controls.Add(lbl); strip.Controls.Add(btnX);
                    Controls.Add(strip); strip.BringToFront();
                }));
            }
        }catch{ /* sem internet, ignora */ }
    }

    void Build() { BuildSidebar(); BuildPages(); }

    void BuildSidebar() {
        var sb=new Panel{Location=new Point(0,0),Size=new Size(SW,FH),BackColor=Cside,Anchor=AnchorStyles.Top|AnchorStyles.Bottom|AnchorStyles.Left}; Controls.Add(sb);
        var logo=new Panel{Location=new Point(0,0),Size=new Size(SW,108),BackColor=Cside};
        logo.Controls.Add(Lbl("Deli",   new Font("Segoe UI",14,FontStyle.Bold),Color.White, new Point(16,10),new Size(200,26)));
        logo.Controls.Add(Lbl("tools",  new Font("Segoe UI",14,FontStyle.Bold),Cacc,        new Point(58,10),new Size(200,26)));
        logo.Controls.Add(Lbl("v2.12.0", new Font("Segoe UI",7.5f),             CsideT,      new Point(16,40),new Size(70,14)));
        logo.Controls.Add(new Panel{Location=new Point(0,104),Size=new Size(SW,1),BackColor=Color.FromArgb(40,45,58)});
        sb.Controls.Add(logo);
        string[] lbl=new string[]{"Instalar Impressora","Impressoras Instaladas","Detectar Impressoras","Corrigir Impressao","Ferramentas","Imprimir Teste","Balancas","Config IP","Dely"};
        // Assistente (indice 8) vem primeiro no menu — e o jeito principal de usar o app —
        // sem reordenar pages[]/lbl[] (evita ter que caçar todo indice hardcoded no resto do
        // codigo). navOrder[posicao visual] = indice real da pagina.
        navOrder=new int[]{8,0,1,2,3,4,5,6,7};
        navItems=new Panel[navOrder.Length]; navLabels=new Label[navOrder.Length];
        int techToggleY=108+44;
        for(int pos=0;pos<navOrder.Length;pos++){
            int idx=navOrder[pos];
            bool isAssistant=idx==8;
            int itemY=pos==0?108:(techToggleY+34+(pos-1)*44);
            var acc=new Panel{Location=new Point(0,0),Size=new Size(4,44),BackColor=isAssistant?CaiAccent:Color.Transparent};
            var num=Lbl((pos+1).ToString(),new Font("Segoe UI",8,FontStyle.Bold),isAssistant?CaiAccent:Cacc,new Point(16,13),new Size(20,18));
            var tx =Lbl(lbl[idx],new Font("Segoe UI",9,isAssistant?FontStyle.Bold:FontStyle.Regular),isAssistant?Color.White:CsideT,new Point(44,13),new Size(188,18));
            var item=new Panel{Location=new Point(0,itemY),Size=new Size(SW,44),BackColor=Cside,Cursor=Cursors.Hand,Visible=isAssistant||manualModeUnlocked};
            item.Controls.AddRange(new Control[]{acc,num,tx});
            item.MouseEnter+=(s,e)=>{ if(activePage!=idx)item.BackColor=CsideH; };
            item.MouseLeave+=(s,e)=>{ if(activePage!=idx)item.BackColor=Cside; };
            item.Click+=(s,e)=>ShowPage(idx);
            foreach(Control c in item.Controls){ var cap=idx; c.MouseEnter+=(s,e)=>{ if(activePage!=cap)item.BackColor=CsideH; }; c.MouseLeave+=(s,e)=>{ if(activePage!=cap)item.BackColor=Cside; }; c.Click+=(s,e)=>ShowPage(cap); }
            navItems[pos]=item; navLabels[pos]=tx; sb.Controls.Add(item);
        }
        sb.Controls.Add(new Panel{Location=new Point(10,108+44),Size=new Size(SW-20,1),BackColor=Color.FromArgb(40,45,58)});
        // Modo Tecnico: as 8 telas manuais ficam escondidas por padrao — so a Dely aparece.
        // Um link discreto aqui pede senha e revela as telas manuais pra quem precisar delas.
        var lnkTech=new Label{Text=manualModeUnlocked?"Modo Tecnico (ativo)":"Modo Tecnico",Font=new Font("Segoe UI",8),ForeColor=CsideT,Location=new Point(16,techToggleY+9),Size=new Size(200,18),Cursor=Cursors.Hand};
        lnkTech.Click+=(s,e)=>ToggleManualMode(lnkTech);
        sb.Controls.Add(lnkTech);
        sb.Controls.Add(new Panel{Location=new Point(10,techToggleY+34),Size=new Size(SW-20,1),BackColor=Color.FromArgb(40,45,58)});
        int sy=FH-148;
        sb.Controls.Add(new Panel{Location=new Point(0,sy),Size=new Size(SW,1),BackColor=Color.FromArgb(40,45,58),Anchor=AnchorStyles.Bottom|AnchorStyles.Left});
        var sta=new Panel{Location=new Point(0,sy+1),Size=new Size(SW,147),BackColor=Cside,Anchor=AnchorStyles.Bottom|AnchorStyles.Left};
        sta.Controls.Add(Lbl("Status do Sistema",new Font("Segoe UI",8,FontStyle.Bold),CsideT,new Point(15,10),new Size(210,16)));
        lblSpoolerDot=Lbl("o",new Font("Segoe UI",10,FontStyle.Bold),Cacc,       new Point(15,32),new Size(16,18));
        lblSpoolerTxt=Lbl("Spooler: ...",new Font("Segoe UI",8.5f),Color.White,  new Point(34,32),new Size(195,18));
        lblQueueTxt  =Lbl("Fila: -",    new Font("Segoe UI",8.5f),CsideT,        new Point(34,54),new Size(195,16));
        var btnR=Btn("Atualizar",new Point(15,80),new Size(210,32),Color.FromArgb(40,45,58));
        btnR.FlatAppearance.BorderColor=Color.FromArgb(60,65,80); btnR.Click+=(s,e)=>RefreshStatus();
        sta.Controls.AddRange(new Control[]{lblSpoolerDot,lblSpoolerTxt,lblQueueTxt,btnR}); sb.Controls.Add(sta);
        Controls.Add(new Panel{Location=new Point(SW,0),Size=new Size(2,FH),BackColor=Color.FromArgb(0,80,160),Anchor=AnchorStyles.Top|AnchorStyles.Bottom|AnchorStyles.Left});
    }

    // Pede a senha do Modo Tecnico (dialogo simples, sem dependencia extra) e libera/esconde
    // as 8 telas manuais. Fica destravado ate o app fechar.
    void ToggleManualMode(Label lnk){
        if(manualModeUnlocked){
            manualModeUnlocked=false;
            for(int i=0;i<navItems.Length;i++) if(navOrder[i]!=8) navItems[i].Visible=false;
            lnk.Text="Modo Tecnico";
            if(activePage!=8) ShowPage(8);
            return;
        }
        string pw=PromptPassword("Modo Tecnico","Essa area e so pra quem sabe o que ta fazendo.\nDigite a senha do Modo Tecnico:");
        if(pw==null) return;
        if(pw!=TechModePassword){ MessageBox.Show("Senha incorreta.","Modo Tecnico",MessageBoxButtons.OK,MessageBoxIcon.Warning); return; }
        manualModeUnlocked=true;
        for(int i=0;i<navItems.Length;i++) if(navOrder[i]!=8) navItems[i].Visible=true;
        lnk.Text="Modo Tecnico (ativo)";
    }
    string PromptPassword(string title,string message){
        using(var f=new Form{Text=title,Width=340,Height=180,StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false,BackColor=Cbg,Font=new Font("Segoe UI",9)}){
            var lbl=new Label{Text=message,Location=new Point(16,14),Size=new Size(292,44),ForeColor=Ctxt};
            var tb=new TextBox{Location=new Point(16,62),Size=new Size(292,24),Font=new Font("Segoe UI",10),UseSystemPasswordChar=true};
            var btnOk=new Button{Text="Entrar",Location=new Point(150,100),Size=new Size(78,32),DialogResult=DialogResult.OK,BackColor=Cacc,ForeColor=Color.White,FlatStyle=FlatStyle.Flat};
            btnOk.FlatAppearance.BorderSize=0;
            var btnCancel=new Button{Text="Cancelar",Location=new Point(230,100),Size=new Size(78,32),DialogResult=DialogResult.Cancel,FlatStyle=FlatStyle.Flat};
            f.Controls.AddRange(new Control[]{lbl,tb,btnOk,btnCancel});
            f.AcceptButton=btnOk; f.CancelButton=btnCancel;
            tb.Focus();
            return f.ShowDialog(this)==DialogResult.OK?tb.Text:null;
        }
    }

    void SetActiveNav(int idx){
        activePage=idx;
        for(int i=0;i<navItems.Length;i++){
            bool on=navOrder[i]==idx; bool isAssistant=navOrder[i]==8;
            navItems[i].BackColor=on?CsideH:Cside;
            navLabels[i].ForeColor=on||isAssistant?Color.White:CsideT;
            navItems[i].Controls[0].BackColor=on?(isAssistant?CaiAccent:Cacc):(isAssistant?CaiAccent:Color.Transparent);
        }
    }
    void ShowPage(int idx){
        SetActiveNav(idx);
        for(int i=0;i<pages.Length;i++) pages[i].Visible=(i==idx);
        if(idx==1||idx==5) RefreshPrinterList(idx);
        if(idx==2) ThreadPool.QueueUserWorkItem(delegate(object st){ RefreshUsbPorts(); });
        if(idx==4) RefreshToolsPage();
        if(idx==6) RefreshScalePorts();
    }

    void BuildPages(){
        pages=new Panel[9];
        for(int i=0;i<9;i++){pages[i]=new Panel{Location=new Point(CX,0),Size=new Size(CW,FH),BackColor=Cbg,Visible=false,Anchor=AnchorStyles.Top|AnchorStyles.Bottom|AnchorStyles.Left|AnchorStyles.Right,AutoScroll=true}; Controls.Add(pages[i]);}
        BuildInstallPage(); BuildInstalledPage(); BuildDetectPage(); BuildFixPage();
        BuildToolsPage(); BuildTestPage(); BuildScalesPage(); BuildNetConfigPage();
        BuildAssistantPage();
    }

    // ════════════════════════════════════════════════════════
    //  PAGE 0 — INSTALAR
    // ════════════════════════════════════════════════════════
    void BuildInstallPage(){
        var p=pages[0];
        PageHeader(p,"Instalar Impressora","Defina o nome, a conexao e clique em Instalar.");
        var sp=BuildSteps(); sp.Location=new Point(CW-390,12); p.Controls.Add(sp);
        int y1=92,h1=215,y2=y1+h1+CG+40+CG,h2=165,logY=y2+h2+CG;
        int halfW=(CW-CM*2-CG)/2;
        // Row 1
        var c1=Card(CM,y1,halfW,h1);         BuildConnCard(c1);
        var c2=Card(CM+halfW+CG,y1,halfW,h1);BuildDetectedCard(c2);
        // Name/default strip
        var strip=new Panel{Location=new Point(CM,y1+h1+CG),Size=new Size(CW-CM*2,40),BackColor=Cbg};
        strip.Controls.Add(Lbl("Nome:",new Font("Segoe UI",8,FontStyle.Bold),Csub,new Point(0,11),new Size(48,18)));
        txtPrinterName=new TextBox{Location=new Point(52,8),Size=new Size(350,24),Font=new Font("Segoe UI",9)};
        strip.Controls.Add(txtPrinterName);
        strip.Controls.Add(Lbl("Definir como padrao:",new Font("Segoe UI",8),Csub,new Point(420,11),new Size(148,18)));
        chkSetDefault=new CheckBox{Location=new Point(572,9),Size=new Size(20,20)};
        strip.Controls.Add(chkSetDefault);
        // Row 2
        var c3=Card(CM,y2,halfW,h2); BuildActionsCard(c3);
        // Log
        var ls=new Panel{Location=new Point(CM,logY),Size=new Size(CW-CM*2,FH-logY-12),BackColor=Cbg};
        BuildLogSection(ls);
        p.Controls.AddRange(new Control[]{c1,c2,strip,c3,ls});
    }

    Panel BuildSteps(){
        var p=new Panel{Size=new Size(370,55),BackColor=Cbg};
        string[] lbl=new string[]{"Selecionar","Instalar","Concluir"}; int[] xs=new int[]{0,125,250};
        for(int i=0;i<3;i++){
            var circ=new Panel{Size=new Size(34,34),Location=new Point(xs[i]+28,0),BackColor=i==0?Cacc:Color.FromArgb(200,205,215)};
            circ.Region=Region.FromHrgn(CreateRoundRectRgn(0,0,34,34,17,17));
            circ.Controls.Add(new Label{Text=(i+1).ToString(),Font=new Font("Segoe UI",10,FontStyle.Bold),ForeColor=Color.White,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleCenter});
            p.Controls.Add(circ); stepCircles[i]=circ;
            p.Controls.Add(Lbl(lbl[i],new Font("Segoe UI",8),i==0?Ctxt:Csub,new Point(xs[i],40),new Size(90,14)));
            if(i<2) p.Controls.Add(new Panel{Location=new Point(xs[i]+64,15),Size=new Size(58,2),BackColor=Cbord});
        }
        return p;
    }

    void BuildConnCard(Panel c){
        CardHdr(c,"Tipo de Conexao");
        var rbUsb=new RadioButton{Text=" USB (Recomendado)",Font=new Font("Segoe UI",9,FontStyle.Bold),ForeColor=Cacc,Location=new Point(8,8),Size=new Size(c.Width-16,20),Checked=true};
        var pUsb=new Panel{Location=new Point(10,40),Size=new Size(c.Width-20,36),BackColor=Color.FromArgb(240,249,244),BorderStyle=BorderStyle.FixedSingle};
        pUsb.Controls.Add(rbUsb); pUsb.Click+=(s,e)=>rbUsb.Checked=true;
        var rbNet=new RadioButton{Text=" Rede / TCP-IP",Font=new Font("Segoe UI",9),ForeColor=Ctxt,Location=new Point(8,8),Size=new Size(c.Width-16,20)};
        var pNet=new Panel{Location=new Point(10,82),Size=new Size(c.Width-20,36),BackColor=Cbg,BorderStyle=BorderStyle.FixedSingle};
        pNet.Controls.Add(rbNet); pNet.Click+=(s,e)=>rbNet.Checked=true;
        // USB info panel
        pnlUsbInfo=new Panel{Location=new Point(8,124),Size=new Size(c.Width-16,86),BackColor=Color.Transparent};
        pnlUsbInfo.Controls.Add(Lbl("PC ====USB==== Impressora",new Font("Segoe UI",8),Csub,new Point(0,4),new Size(c.Width-16,16)));
        pnlUsbInfo.Controls.Add(new Label{Text="  Conecte a impressora via USB\n  antes de clicar em Instalar.",Font=new Font("Segoe UI",8),ForeColor=Cblue,Location=new Point(0,24),Size=new Size(c.Width-16,40),BackColor=Color.FromArgb(232,240,254)});
        // Network info panel
        pnlNetInfo=new Panel{Location=new Point(8,124),Size=new Size(c.Width-16,86),BackColor=Color.Transparent,Visible=false};
        pnlNetInfo.Controls.Add(Lbl("IP da Impressora:",new Font("Segoe UI",8,FontStyle.Bold),Csub,new Point(0,2),new Size(120,16)));
        txtIpAddress=new TextBox{Location=new Point(0,20),Size=new Size(c.Width-96,22),Font=new Font("Segoe UI",9),Text="192.168.1.100"};
        pnlNetInfo.Controls.Add(txtIpAddress);
        pnlNetInfo.Controls.Add(Lbl("Porta:",new Font("Segoe UI",8),Csub,new Point(c.Width-88,20),new Size(36,22)));
        txtPortNum=new TextBox{Location=new Point(c.Width-50,20),Size=new Size(32,22),Font=new Font("Segoe UI",9),Text="9100"};
        pnlNetInfo.Controls.Add(txtPortNum);
        pnlNetInfo.Controls.Add(Lbl("Protocolo: RAW (porta 9100 padrao)",new Font("Segoe UI",7.5f),Csub,new Point(0,50),new Size(c.Width-16,14)));
        rbUsb.CheckedChanged+=(s,e)=>{ if(rbUsb.Checked){ rbNet.Checked=false; connUsb=true; pUsb.BackColor=Color.FromArgb(240,249,244); pNet.BackColor=Cbg; pnlUsbInfo.Visible=true; pnlNetInfo.Visible=false; pnlUsbDetect.Visible=true; pnlNetScan.Visible=false; } };
        rbNet.CheckedChanged+=(s,e)=>{ if(rbNet.Checked){ rbUsb.Checked=false; connUsb=false; pNet.BackColor=Color.FromArgb(232,240,254); pUsb.BackColor=Cbg; pnlUsbInfo.Visible=false; pnlNetInfo.Visible=true; pnlUsbDetect.Visible=false; pnlNetScan.Visible=true; } };
        c.Controls.AddRange(new Control[]{pUsb,pNet,pnlUsbInfo,pnlNetInfo});
    }

    void BuildDetectedCard(Panel c){
        CardHdr(c,"Impressoras Detectadas");
        var btnR=new Button{Text="R",FlatStyle=FlatStyle.Flat,Font=new Font("Segoe UI",10,FontStyle.Bold),ForeColor=Cblue,BackColor=Color.Transparent,Location=new Point(c.Width-30,8),Size=new Size(22,22),Cursor=Cursors.Hand};
        btnR.FlatAppearance.BorderSize=0;
        btnR.Click+=(s,e)=>{ if(connUsb){ Log("Detectando..."); ThreadPool.QueueUserWorkItem(delegate(object st){ DetRes? r=DoDetect(); BeginInvoke((Action)(()=>ApplyDetect(r))); }); } else ScanNetworkAsync(); };
        c.Controls.Add(btnR);
        // --- USB: dispositivo plugado, detectado por VID/PID ---
        pnlUsbDetect=new Panel{Location=new Point(0,0),Size=new Size(c.Width,c.Height),BackColor=Color.Transparent};
        lblNoDetect=Lbl("Nenhuma impressora USB detectada.",new Font("Segoe UI",8.5f),Csub,new Point(10,48),new Size(c.Width-20,18));
        pnlDetected=new Panel{Location=new Point(10,38),Size=new Size(c.Width-20,100),BackColor=Color.FromArgb(240,249,244),BorderStyle=BorderStyle.FixedSingle,Visible=false};
        pnlDetected.Controls.Add(Lbl("Impressora encontrada!",new Font("Segoe UI",8,FontStyle.Bold),Cacc,new Point(8,8),new Size(pnlDetected.Width-16,16)));
        lblDetName=Lbl("",new Font("Segoe UI",9,FontStyle.Bold),Ctxt,new Point(8,28),new Size(pnlDetected.Width-16,18));
        lblDetVid =Lbl("",new Font("Segoe UI",7.5f),Csub,new Point(8,48),new Size(pnlDetected.Width-16,14));
        pnlDetected.Controls.AddRange(new Control[]{lblDetName,lblDetVid});
        pnlUsbDetect.Controls.AddRange(new Control[]{lblNoDetect,pnlDetected});
        c.Controls.Add(pnlUsbDetect);
        // --- Rede: varredura da sub-rede local na porta 9100 (RAW/JetDirect) ---
        pnlNetScan=new Panel{Location=new Point(0,0),Size=new Size(c.Width,c.Height),BackColor=Color.Transparent,Visible=false};
        btnScanNet=Btn("Buscar na Rede",new Point(10,38),new Size(140,26),Cblue);
        btnScanNet.Click+=(s,e)=>ScanNetworkAsync();
        lblNetScanStatus=Lbl("Busca impressoras na porta 9100 da sua rede local.",new Font("Segoe UI",7.5f),Csub,new Point(158,44),new Size(c.Width-168,30));
        lstNetFound=new ListBox{Location=new Point(10,72),Size=new Size(c.Width-20,c.Height-82),Font=new Font("Segoe UI",8.5f),BackColor=Color.White,BorderStyle=BorderStyle.FixedSingle,IntegralHeight=false};
        lstNetFound.SelectedIndexChanged+=(s,e)=>{
            if(lstNetFound.SelectedItem==null) return;
            var mm=Regex.Match(lstNetFound.SelectedItem.ToString(),@"(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})");
            if(mm.Success&&txtIpAddress!=null){ txtIpAddress.Text=mm.Groups[1].Value; Log("IP selecionado: "+mm.Groups[1].Value); }
        };
        pnlNetScan.Controls.AddRange(new Control[]{btnScanNet,lblNetScanStatus,lstNetFound});
        c.Controls.Add(pnlNetScan);
    }

    void ScanNetworkAsync(){
        if(lstNetFound==null||btnScanNet==null) return;
        btnScanNet.Enabled=false; lstNetFound.Items.Clear();
        lblNetScanStatus.Text="Buscando na rede... (ate 5s)"; lblNetScanStatus.ForeColor=Csub;
        Log("Buscando impressoras na rede (porta 9100)...");
        ThreadPool.QueueUserWorkItem(delegate(object st){
            var results=ScanNetworkPrinters();
            BeginInvoke((Action)(()=>{
                lstNetFound.Items.Clear();
                foreach(var r in results) lstNetFound.Items.Add(r);
                lblNetScanStatus.Text=results.Count>0?(results.Count+" impressora(s) encontrada(s). Selecione uma para usar o IP."):"Nenhuma impressora encontrada na rede local.";
                lblNetScanStatus.ForeColor=results.Count>0?Cacc:Cerr;
                btnScanNet.Enabled=true;
                Log("Busca de rede: "+results.Count+" impressora(s) encontrada(s).");
            }));
        });
    }

    // Varre a sub-rede /24 do adaptador local (host.1-254) procurando a porta 9100 aberta (RAW/JetDirect).
    List<string> ScanNetworkPrinters(){
        var found=new List<string>();
        string localIp=GetLocalIp("");
        var m=Regex.Match(localIp,@"^(\d{1,3}\.\d{1,3}\.\d{1,3})\.\d{1,3}$");
        if(!m.Success){ UILog("Nao foi possivel determinar a rede local."); return found; }
        string prefix=m.Groups[1].Value+".";
        var names=new string[255];
        var done=new System.Threading.CountdownEvent(254);
        for(int host=1;host<=254;host++){
            int h=host;
            ThreadPool.QueueUserWorkItem(delegate(object st){
                try{
                    string ip=prefix+h;
                    using(var sk=new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,System.Net.Sockets.SocketType.Stream,System.Net.Sockets.ProtocolType.Tcp)){
                        var ar=sk.BeginConnect(ip,9100,null,null);
                        if(ar.AsyncWaitHandle.WaitOne(500)){
                            try{ sk.EndConnect(ar); names[h]=ip; }catch{}
                        }
                    }
                }catch{}
                finally{ done.Signal(); }
            });
        }
        done.Wait(8000);
        for(int host=1;host<=254;host++) if(names[host]!=null){
            string ip=names[host]; string hn=TryReverseDns(ip);
            bool escPos=TestIsEscPosDevice(ip,9100,1200);
            string tipo=escPos?"impressora termica confirmada (responde ESC/POS)":"porta 9100 aberta (nao confirmado que e impressora)";
            found.Add(hn!=null?(ip+"  ("+hn+")  —  "+tipo):(ip+"  —  "+tipo));
        }
        return found;
    }

    // Confirma que o que esta escutando naquela porta e uma impressora ESC/POS de verdade,
    // nao so qualquer coisa com a porta aberta (portado do FudoPrintDoctor): manda DLE EOT 1
    // (pedido de status em tempo real) — uma termica responde pelo menos 1 byte.
    bool TestIsEscPosDevice(string ip,int port,int timeoutMs){
        try{
            using(var client=new System.Net.Sockets.TcpClient()){
                var iar=client.BeginConnect(ip,port,null,null);
                if(!iar.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
                client.EndConnect(iar);
                var stream=client.GetStream();
                stream.WriteTimeout=timeoutMs; stream.ReadTimeout=timeoutMs;
                byte[] probe=new byte[]{0x10,0x04,0x01};
                stream.Write(probe,0,probe.Length); stream.Flush();
                System.Threading.Thread.Sleep(250);
                var buf=new byte[4];
                try{ return stream.Read(buf,0,4)>0; }catch{ return false; }
            }
        }catch{ return false; }
    }

    // Resolve o nome do host (NetBIOS/DNS) de um IP, se houver, sem travar muito tempo.
    string TryReverseDns(string ip){
        try{
            var ar=System.Net.Dns.BeginGetHostEntry(ip,null,null);
            if(ar.AsyncWaitHandle.WaitOne(800)){
                var he=System.Net.Dns.EndGetHostEntry(ar);
                if(he!=null&&!string.IsNullOrEmpty(he.HostName)&&!he.HostName.Equals(ip)) return he.HostName;
            }
        }catch{}
        return null;
    }

    void BuildActionsCard(Panel c){
        CardHdr(c,"Acoes");
        btnInstall=new Button{Text="Instalar Impressora",Font=new Font("Segoe UI",9.5f,FontStyle.Bold),ForeColor=Color.White,BackColor=Cacc,FlatStyle=FlatStyle.Flat,Location=new Point(10,38),Size=new Size(c.Width-20,30),Cursor=Cursors.Hand,TextAlign=ContentAlignment.MiddleCenter};
        btnInstall.FlatAppearance.BorderSize=0; btnInstall.Click+=OnInstall;
        string[] sec=new string[]{"Imprimir Teste","Abrir Propriedades","Remover Impressora"};
        Action[] acts=new Action[]{()=>OnTestPage(),()=>OnOpenProps(),()=>OnRemove()};
        for(int i=0;i<3;i++){ int ci=i;
            var b=new Button{Text=sec[i],Font=new Font("Segoe UI",9f),ForeColor=Ctxt,BackColor=Ccard,FlatStyle=FlatStyle.Flat,Location=new Point(10,76+i*28),Size=new Size(c.Width-20,26),Cursor=Cursors.Hand,TextAlign=ContentAlignment.MiddleLeft,Padding=new Padding(4,0,0,0)};
            b.FlatAppearance.BorderColor=Cbord; b.Click+=(s,e)=>acts[ci](); c.Controls.Add(b);
        }
        c.Controls.Add(btnInstall);
    }

    void BuildLogSection(Panel s){
        s.Controls.Add(Lbl("Log",new Font("Segoe UI",9,FontStyle.Bold),Ctxt,new Point(0,0),new Size(80,20)));
        var clr=new Button{Text="Limpar",FlatStyle=FlatStyle.Flat,Font=new Font("Segoe UI",8),ForeColor=Csub,BackColor=Cbg,Location=new Point(s.Width-72,0),Size=new Size(70,20),Cursor=Cursors.Hand};
        clr.FlatAppearance.BorderSize=0; clr.Click+=(a,b)=>{ if(logBox!=null)logBox.Clear(); }; s.Controls.Add(clr);
        logBox=new RichTextBox{Location=new Point(0,22),Size=new Size(s.Width,s.Height-22),ReadOnly=true,BackColor=Color.FromArgb(248,249,250),Font=new Font("Consolas",8.5f),ForeColor=Csub,BorderStyle=BorderStyle.None,ScrollBars=RichTextBoxScrollBars.Vertical};
        s.Controls.Add(logBox);
    }

    // ════════════════════════════════════════════════════════
    //  OTHER PAGES
    // ════════════════════════════════════════════════════════
    void BuildInstalledPage(){
        var pg=pages[1]; PageHeader(pg,"Impressoras Instaladas","Lista de impressoras instaladas no sistema.");
        lstInstalled=new ListBox{Location=new Point(CM,95),Size=new Size(CW-CM*2,FH-210),Font=new Font("Segoe UI",10),BorderStyle=BorderStyle.FixedSingle,BackColor=Ccard};
        var bRef =Btn("Atualizar",    new Point(CM,FH-95),     new Size(140,32),Cacc);
        var bTest=Btn("Imprimir Teste",new Point(CM+150,FH-95), new Size(150,32),Cblue);
        var bProp=Btn("Propriedades", new Point(CM+310,FH-95), new Size(140,32),Color.FromArgb(80,80,80));
        var bDef =Btn("Def. Padrao",  new Point(CM+460,FH-95), new Size(130,32),Cpurple);
        var bRem =Btn("Remover",      new Point(CM+600,FH-95), new Size(110,32),Cerr);
        bRef.Click +=(s,e)=>RefreshPrinterList(1);
        bTest.Click+=(s,e)=>{ if(lstInstalled.SelectedItem!=null){var n=lstInstalled.SelectedItem.ToString().Split('|')[0].Trim(); Log("Teste: "+n); DoTestPage(n);} };
        bProp.Click+=(s,e)=>{ if(lstInstalled.SelectedItem!=null){var n=lstInstalled.SelectedItem.ToString().Split('|')[0].Trim(); try{Process.Start("rundll32.exe","printui.dll,PrintUIEntry /p /n \""+n+"\"");}catch(Exception ex){Log("Erro: "+ex.Message);}} };
        bDef.Click +=(s,e)=>{ if(lstInstalled.SelectedItem!=null){var n=lstInstalled.SelectedItem.ToString().Split('|')[0].Trim(); ThreadPool.QueueUserWorkItem(delegate(object st){ SetDefaultPrinter(n); BeginInvoke((Action)(()=>RefreshPrinterList(1))); });} };
        bRem.Click +=(s,e)=>{ if(lstInstalled.SelectedItem!=null){var n=lstInstalled.SelectedItem.ToString().Split('|')[0].Trim(); if(MessageBox.Show("Remover: "+n+"?","Confirmar",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)==DialogResult.Yes) OnRemoveName(n);} };
        pg.Controls.AddRange(new Control[]{lstInstalled,bRef,bTest,bProp,bDef,bRem});
    }

    void BuildDetectPage(){
        var pg=pages[2]; PageHeader(pg,"Portas & Dispositivos USB","Portas de impressora em uso e dispositivos USB conectados.");
        int contentH=FH-107; int leftW=460; int rightW=CW-CM*2-leftW-CG;

        // ── Left: Printer Ports ──────────────────────────────
        var cPorts=Card(CM,95,leftW,contentH); pg.Controls.Add(cPorts);
        CardHdr(cPorts,"Portas de Impressora Registradas");
        portsBox=new RichTextBox{Location=new Point(8,40),Size=new Size(leftW-16,contentH-90),ReadOnly=true,BackColor=Ccard,Font=new Font("Consolas",8.5f),ForeColor=Ctxt,BorderStyle=BorderStyle.None,ScrollBars=RichTextBoxScrollBars.Vertical};
        var bRefPorts=Btn("Atualizar Portas",new Point(8,contentH-44),new Size(160,30),Cacc);
        bRefPorts.Click+=(s,e)=>{ Log("Atualizando portas..."); ThreadPool.QueueUserWorkItem(delegate(object st){ RefreshUsbPorts(); BeginInvoke((Action)(()=>Log("Portas atualizadas."))); }); };
        cPorts.Controls.AddRange(new Control[]{portsBox,bRefPorts});

        // ── Right: USB Devices ────────────────────────────────
        var cDevs=Card(CM+leftW+CG,95,rightW,contentH); pg.Controls.Add(cDevs);
        CardHdr(cDevs,"Dispositivos USB Detectados (VID/PID)");
        usbDevBox=new RichTextBox{Location=new Point(8,40),Size=new Size(rightW-16,contentH-90),ReadOnly=true,BackColor=Ccard,Font=new Font("Consolas",8.5f),ForeColor=Ctxt,BorderStyle=BorderStyle.None,ScrollBars=RichTextBoxScrollBars.Vertical};
        usbDevBox.Text="Clique em Detectar para buscar dispositivos USB.\n";
        var bDet=Btn("Detectar Dispositivos",new Point(8,contentH-44),new Size(180,30),Cblue);
        bDet.Click+=(s,e)=>{
            usbDevBox.Clear(); usbDevBox.AppendText("Detectando...\n"); bDet.Enabled=false;
            ThreadPool.QueueUserWorkItem(delegate(object state){
                var sb2=new System.Text.StringBuilder(); int found=0;
                try{ foreach(ManagementObject o in new ManagementObjectSearcher("SELECT * FROM Win32_PnPEntity").Get()){
                    var did=o["DeviceID"]!=null?o["DeviceID"].ToString():"";
                    var mm=Regex.Match(did,@"USB\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})",RegexOptions.IgnoreCase);
                    if(mm.Success){var nm=o["Name"]!=null?o["Name"].ToString():"?"; sb2.AppendLine("VID="+mm.Groups[1].Value+"  PID="+mm.Groups[2].Value+"  |  "+nm); found++;}
                }}catch(Exception ex){sb2.AppendLine("Erro: "+ex.Message);}
                int fc=found; string txt=sb2.ToString();
                BeginInvoke((Action)(()=>{ usbDevBox.Clear(); usbDevBox.AppendText(fc==0?"Nenhum dispositivo USB reconhecido.":"["+fc+" dispositivo(s) encontrado(s)]\n\n"+txt); bDet.Enabled=true; Log("Deteccao USB: "+fc+" encontrado(s)."); }));
            });
        };
        cDevs.Controls.AddRange(new Control[]{usbDevBox,bDet});
    }

    void RefreshUsbPorts(){
        if(portsBox==null) return;
        var portPrinter=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        try{ foreach(ManagementObject o in new ManagementObjectSearcher("SELECT * FROM Win32_Printer").Get()){
            var pn=o["PortName"]!=null?o["PortName"].ToString():"";
            var nm=o["Name"]!=null?o["Name"].ToString():"";
            if(pn.Length>0&&nm.Length>0) portPrinter[pn]=nm;
        }}catch{}
        var lines=new List<string[]>(); // [portName, type, printer]
        var seenPorts=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try{ foreach(ManagementObject o in new ManagementObjectSearcher("SELECT * FROM Win32_PrinterPort").Get()){
            var name=o["Name"]!=null?o["Name"].ToString():"";
            bool local=o["LocalPort"]!=null&&(bool)o["LocalPort"];
            string ptype=name.StartsWith("USB",StringComparison.OrdinalIgnoreCase)?"USB":
                         name.StartsWith("COM",StringComparison.OrdinalIgnoreCase)?"COM":
                         name.StartsWith("LPT",StringComparison.OrdinalIgnoreCase)?"LPT":
                         name.StartsWith("IP_",StringComparison.OrdinalIgnoreCase)||name.Contains(".")?"TCP/IP":"Local";
            string printer=portPrinter.ContainsKey(name)?portPrinter[name]:"(sem impressora)";
            lines.Add(new string[]{name,ptype,printer}); seenPorts.Add(name);
        }}catch{}
        // O Spooler (Win32_PrinterPort) so lista portas de uma fila ja criada. Uma impressora USB
        // plugada mas SEM driver/fila ainda nao aparece ali — completa com o registro USBPRINT,
        // que o Windows preenche assim que reconhece o dispositivo como impressora USB.
        foreach(var rp in GetUsbPrintRegistryPorts()){
            if(seenPorts.Contains(rp[0])) continue;
            string printer=portPrinter.ContainsKey(rp[0])?portPrinter[rp[0]]:"(sem driver instalado — "+rp[1]+")";
            lines.Add(new string[]{rp[0],"USB",printer}); seenPorts.Add(rp[0]);
        }
        // Sort: USB first, then COM, TCP/IP, others
        lines.Sort((a,b2)=>{int r=string.Compare(a[1],b2[1]); return r!=0?r:string.Compare(a[0],b2[0]);});
        Action update=()=>{
            if(portsBox==null) return;
            portsBox.Clear();
            portsBox.SelectionFont=new Font("Consolas",8.5f,FontStyle.Bold);
            portsBox.SelectionColor=Csub;
            portsBox.AppendText(string.Format("{0,-14}{1,-8}{2}\n","PORTA","TIPO","IMPRESSORA"));
            portsBox.SelectionColor=Color.FromArgb(200,205,215);
            portsBox.AppendText(new string('-',60)+"\n");
            foreach(var row in lines){
                bool isUsb=row[1]=="USB"; bool hasPrinter=!row[2].StartsWith("(");
                portsBox.SelectionFont=new Font("Consolas",8.5f,FontStyle.Regular);
                portsBox.SelectionColor=isUsb?(hasPrinter?Cacc:Cblue):Csub;
                portsBox.AppendText(string.Format("{0,-14}{1,-8}{2}\n",row[0],row[1],row[2]));
            }
            if(lines.Count==0){ portsBox.SelectionColor=Csub; portsBox.AppendText("Nenhuma porta encontrada.\n"); }
        };
        if(portsBox.InvokeRequired) portsBox.BeginInvoke(update); else update();
    }

    void BuildFixPage(){
        var pg=pages[3]; PageHeader(pg,"Corrigir Impressao","Ferramentas para resolver problemas de impressao.");
        string[] t=new string[]{"Reiniciar Spooler","Limpar Fila de Impressao","Abrir Gerenc. de Dispositivos"};
        string[] d=new string[]{"Para e reinicia o servico Print Spooler.","Remove todos os trabalhos pendentes na fila.","Abre o Gerenciador de Dispositivos do Windows."};
        Color[]  co=new Color[]{Cpurple,Corange,Cblue};
        for(int i=0;i<3;i++){ int ci=i;
            var card=Card(CM,95+i*115,CW-CM*2,100);
            card.Controls.Add(Lbl(t[i],new Font("Segoe UI",11,FontStyle.Bold),Ctxt,new Point(15,12),new Size(600,22)));
            card.Controls.Add(Lbl(d[i],new Font("Segoe UI",9),Csub,new Point(15,38),new Size(card.Width-170,18)));
            var b=Btn(t[i],new Point(card.Width-155,28),new Size(140,34),co[i]);
            if(ci==0) b.Click+=(s,e)=>{ Log("Reiniciando Spooler..."); ThreadPool.QueueUserWorkItem(delegate(object st){ RestartSpooler(false); BeginInvoke((Action)(()=>{ Log("Spooler reiniciado."); RefreshStatus(); })); }); };
            if(ci==1) b.Click+=(s,e)=>{ if(MessageBox.Show("Limpar toda a fila?","Confirmar",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)==DialogResult.Yes){ Log("Limpando fila..."); ThreadPool.QueueUserWorkItem(delegate(object st){ RestartSpooler(true); BeginInvoke((Action)(()=>{ Log("Fila limpa."); RefreshStatus(); })); }); } };
            if(ci==2) b.Click+=(s,e)=>{ try{Process.Start("devmgmt.msc");}catch{} };
            card.Controls.Add(b); pg.Controls.Add(card);
        }
    }

    void BuildToolsPage(){
        var pg=pages[4]; PageHeader(pg,"Ferramentas","Diagnostico, gaveta de dinheiro e backup de impressoras.");
        int y=95, cw2=CRD*2+CG;
        // Row 1: Spooler | Ping
        var cSpool=Card(CM,y,CRD,160); BuildSpoolerCard(cSpool);
        var cPing =Card(CM+CRD+CG,y,CRD,160); BuildPingCard(cPing);
        // Row 2: Cash Drawer | (space)
        var cDraw =Card(CM,y+160+CG,CRD,160); BuildDrawerCard(cDraw);
        // Row 3: Backup full width
        var cBak  =Card(CM,y+160+CG+160+CG,CW-CM*2,170); BuildBackupCard(cBak);
        pg.Controls.AddRange(new Control[]{cSpool,cPing,cDraw,cBak});
    }

    void BuildSpoolerCard(Panel c){
        CardHdr(c,"Status do Spooler");
        lblToolsSpooler=Lbl("...",new Font("Segoe UI",10),Cacc,new Point(15,40),new Size(c.Width-30,20));
        lblToolsQueue  =Lbl("...",new Font("Segoe UI",9), Csub,new Point(15,64),new Size(c.Width-30,18));
        var bR=Btn("Reiniciar",  new Point(15,90), new Size(c.Width/2-20,30),Cpurple);
        var bC=Btn("Limpar Fila",new Point(c.Width/2+4,90),new Size(c.Width/2-19,30),Corange);
        bR.Click+=(s,e)=>{ Log("Reiniciando Spooler..."); ThreadPool.QueueUserWorkItem(delegate(object st){ RestartSpooler(false); BeginInvoke((Action)(()=>{ Log("OK."); RefreshStatus(); RefreshToolsPage(); })); }); };
        bC.Click+=(s,e)=>{ if(MessageBox.Show("Limpar fila?","Confirmar",MessageBoxButtons.YesNo)==DialogResult.Yes){ Log("Limpando..."); ThreadPool.QueueUserWorkItem(delegate(object st){ RestartSpooler(true); BeginInvoke((Action)(()=>{ Log("Fila limpa."); RefreshStatus(); RefreshToolsPage(); })); }); } };
        c.Controls.AddRange(new Control[]{lblToolsSpooler,lblToolsQueue,bR,bC});
    }

    void BuildPingCard(Panel c){
        CardHdr(c,"Diagnostico de Rede");
        c.Controls.Add(Lbl("IP:",new Font("Segoe UI",8,FontStyle.Bold),Csub,new Point(10,42),new Size(22,18)));
        txtPingIp=new TextBox{Location=new Point(36,40),Size=new Size(c.Width-118,22),Font=new Font("Segoe UI",9),Text="192.168.1.100"};
        c.Controls.Add(txtPingIp);
        c.Controls.Add(Lbl("Porta:",new Font("Segoe UI",8,FontStyle.Bold),Csub,new Point(c.Width-78,42),new Size(36,18)));
        txtPingPort=new TextBox{Location=new Point(c.Width-40,40),Size=new Size(38,22),Font=new Font("Segoe UI",8),Text="9100"};
        c.Controls.Add(txtPingPort);
        var lblRes=Lbl("",new Font("Segoe UI",9,FontStyle.Bold),Csub,new Point(10,90),new Size(c.Width-20,20));
        var bPing=Btn("Ping",new Point(10,68),new Size((c.Width-30)/2,22),Cblue);
        var bPort=Btn("Testar Porta",new Point(10+(c.Width-30)/2+10,68),new Size((c.Width-30)/2,22),Cblue);
        bPing.Click+=(s,e)=>{
            string ip=txtPingIp.Text.Trim(); if(ip.Length==0) return;
            lblRes.Text="Testando..."; lblRes.ForeColor=Csub; bPing.Enabled=false;
            ThreadPool.QueueUserWorkItem(delegate(object st){
                bool ok=PingAddress(ip); string msg=ok?"Ping OK - "+ip+" responde!":"Sem resposta de "+ip;
                BeginInvoke((Action)(()=>{ lblRes.Text=msg; lblRes.ForeColor=ok?Cacc:Cerr; bPing.Enabled=true; Log(msg); }));
            });
        };
        bPort.Click+=(s,e)=>{
            string ip=txtPingIp.Text.Trim(); int port=9100;
            if(txtPingPort!=null&&txtPingPort.Text.Length>0) int.TryParse(txtPingPort.Text,out port);
            if(ip.Length==0) return;
            lblRes.Text="Testando porta..."; lblRes.ForeColor=Csub; bPort.Enabled=false;
            ThreadPool.QueueUserWorkItem(delegate(object st){
                bool ok=TestTcpPort(ip,port); string msg=ok?"Porta "+port+" aberta em "+ip:"Porta "+port+" fechada em "+ip;
                BeginInvoke((Action)(()=>{ lblRes.Text=msg; lblRes.ForeColor=ok?Cacc:Cerr; bPort.Enabled=true; Log(msg); }));
            });
        };
        c.Controls.AddRange(new Control[]{bPing,bPort,lblRes});
    }

    void BuildDrawerCard(Panel c){
        CardHdr(c,"Gaveta de Dinheiro (ESC/POS)");
        c.Controls.Add(Lbl("Impressora:",new Font("Segoe UI",8,FontStyle.Bold),Csub,new Point(10,42),new Size(78,18)));
        cmbDrawerPrinter=new ComboBox{Location=new Point(92,40),Size=new Size(c.Width-102,22),DropDownStyle=ComboBoxStyle.DropDownList,Font=new Font("Segoe UI",8)};
        c.Controls.Add(cmbDrawerPrinter);
        var bRef=new Button{Text="R",FlatStyle=FlatStyle.Flat,Font=new Font("Segoe UI",8,FontStyle.Bold),ForeColor=Cblue,BackColor=Color.Transparent,Location=new Point(c.Width-16,40),Size=new Size(16,22)};
        bRef.FlatAppearance.BorderSize=0; bRef.Click+=(s,e)=>RefreshDrawerPrinters(); c.Controls.Add(bRef);
        var b1=Btn("Abrir Gaveta 1 (Pino 2)",new Point(10,72),new Size(c.Width-20,28),Corange);
        var b2=Btn("Abrir Gaveta 2 (Pino 5)",new Point(10,106),new Size(c.Width-20,28),Color.FromArgb(140,80,20));
        b1.Click+=(s,e)=>{ if(cmbDrawerPrinter.SelectedItem!=null){ string n=cmbDrawerPrinter.SelectedItem.ToString(); Log("Abrindo gaveta 1: "+n); bool ok=SendRawBytes(n,new byte[]{0x1B,0x70,0x00,25,(byte)250}); Log(ok?"Sinal enviado.":"Erro ao enviar sinal."); } else MessageBox.Show("Selecione a impressora."); };
        b2.Click+=(s,e)=>{ if(cmbDrawerPrinter.SelectedItem!=null){ string n=cmbDrawerPrinter.SelectedItem.ToString(); Log("Abrindo gaveta 2: "+n); bool ok=SendRawBytes(n,new byte[]{0x1B,0x70,0x01,25,(byte)250}); Log(ok?"Sinal enviado.":"Erro ao enviar sinal."); } else MessageBox.Show("Selecione a impressora."); };
        c.Controls.AddRange(new Control[]{b1,b2});
        RefreshDrawerPrinters();
    }

    void RefreshDrawerPrinters(){
        if(cmbDrawerPrinter==null) return;
        cmbDrawerPrinter.Items.Clear();
        foreach(var p in GetPrinters()) cmbDrawerPrinter.Items.Add(p);
        if(cmbDrawerPrinter.Items.Count>0) cmbDrawerPrinter.SelectedIndex=0;
    }

    void BuildBackupCard(Panel c){
        CardHdr(c,"Backup e Restauracao de Impressoras");
        c.Controls.Add(Lbl("Salva lista de impressoras instaladas e restaura em outro PC.",new Font("Segoe UI",8.5f),Csub,new Point(10,38),new Size(c.Width-20,18)));
        var bBak=Btn("Fazer Backup",   new Point(10,62), new Size(160,32),Cacc);
        var bRes=Btn("Restaurar Backup",new Point(180,62),new Size(160,32),Cblue);
        var lblStatus=Lbl("",new Font("Segoe UI",8),Csub,new Point(360,70),new Size(c.Width-370,18));
        bBak.Click+=(s,e)=>{
            ThreadPool.QueueUserWorkItem(delegate(object st){
                string file=BackupPrinters();
                bool failed=file.StartsWith("(falhou");
                BeginInvoke((Action)(()=>{ lblStatus.Text=failed?"Erro no backup: "+file:"Backup salvo: "+file; lblStatus.ForeColor=failed?Cerr:Cacc; Log(failed?"Backup falhou: "+file:"Backup: "+file); }));
            });
        };
        bRes.Click+=(s,e)=>{
            var dlg=new OpenFileDialog{Title="Selecionar Backup",Filter="Backup|*.txt|Todos|*.*",FileName="PrinterBackup.txt"};
            if(dlg.ShowDialog()==DialogResult.OK){
                string file=dlg.FileName; lblStatus.Text="Restaurando...";
                ThreadPool.QueueUserWorkItem(delegate(object st){ RestorePrinters(file); BeginInvoke((Action)(()=>{ lblStatus.Text="Restauracao concluida."; lblStatus.ForeColor=Cacc; RefreshPrinterList(1); })); });
            }
        };
        c.Controls.AddRange(new Control[]{bBak,bRes,lblStatus});
    }

    void BuildTestPage(){
        var pg=pages[5]; PageHeader(pg,"Imprimir Teste","Selecione a impressora e envie uma pagina de teste.");
        lstTest=new ListBox{Location=new Point(CM,95),Size=new Size(CW-CM*2,FH-230),Font=new Font("Segoe UI",10),BorderStyle=BorderStyle.FixedSingle,BackColor=Ccard};
        var bR=Btn("Atualizar",               new Point(CM,FH-115),     new Size(140,36),Cblue);
        var bT=Btn("Imprimir Pagina de Teste", new Point(CM+150,FH-115), new Size(210,36),Cacc);
        bR.Click+=(s,e)=>RefreshPrinterList(5);
        bT.Click+=(s,e)=>{ if(lstTest.SelectedItem!=null){var n=lstTest.SelectedItem.ToString().Split('|')[0].Trim(); Log("Teste: "+n); DoTestPage(n);}else MessageBox.Show("Selecione uma impressora.","Atencao",MessageBoxButtons.OK,MessageBoxIcon.Warning); };
        pg.Controls.AddRange(new Control[]{lstTest,bR,bT});
    }

    void BuildScalesPage(){
        var pg=pages[6]; PageHeader(pg,"Balancas","Leitura de peso via porta COM (RS-232 / USB-Serial).");
        // Config strip
        var strip=new Panel{Location=new Point(CM,95),Size=new Size(CW-CM*2,48),BackColor=Cbg};
        strip.Controls.Add(Lbl("Porta:",new Font("Segoe UI",8,FontStyle.Bold),Csub,new Point(0,15),new Size(44,18)));
        cmbComPorts=new ComboBox{Location=new Point(46,11),Size=new Size(100,24),DropDownStyle=ComboBoxStyle.DropDownList,Font=new Font("Segoe UI",9)};
        strip.Controls.Add(cmbComPorts);
        strip.Controls.Add(Lbl("Baud:",new Font("Segoe UI",8,FontStyle.Bold),Csub,new Point(154,15),new Size(40,18)));
        cmbBaudRate=new ComboBox{Location=new Point(196,11),Size=new Size(88,24),DropDownStyle=ComboBoxStyle.DropDownList,Font=new Font("Segoe UI",9)};
        foreach(var b in new string[]{"9600","4800","19200","2400","38400"}) cmbBaudRate.Items.Add(b);
        cmbBaudRate.SelectedIndex=0; strip.Controls.Add(cmbBaudRate);
        strip.Controls.Add(Lbl("Modelo:",new Font("Segoe UI",8,FontStyle.Bold),Csub,new Point(294,15),new Size(56,18)));
        cmbScaleProtocol=new ComboBox{Location=new Point(352,11),Size=new Size(148,24),DropDownStyle=ComboBoxStyle.DropDownList,Font=new Font("Segoe UI",9)};
        foreach(var p in new string[]{"Prix 3 Fit (Toledo)","Prix 3 Plus (Toledo)","Toledo Generico","Filizola","Urano","Elgin DP","Auto / Generico"}) cmbScaleProtocol.Items.Add(p);
        cmbScaleProtocol.SelectedIndex=0;
        cmbScaleProtocol.SelectedIndexChanged+=(s,e)=>{
            if(cmbBaudRate==null||cmbScaleProtocol.SelectedItem==null) return;
            if(cmbBaudRate.Items.Contains("9600")) cmbBaudRate.SelectedItem="9600";
        };
        strip.Controls.Add(cmbScaleProtocol);
        btnScaleConnect=Btn("Conectar",new Point(510,8),new Size(110,32),Cacc);
        btnScaleConnect.Click+=OnScaleConnect; strip.Controls.Add(btnScaleConnect);
        var btnRef=new Button{Text="↺",FlatStyle=FlatStyle.Flat,Font=new Font("Segoe UI",11),ForeColor=Cblue,BackColor=Color.Transparent,Location=new Point(626,8),Size=new Size(32,32),Cursor=Cursors.Hand};
        btnRef.FlatAppearance.BorderSize=0; btnRef.Click+=(s,e)=>{RefreshScalePorts(); RefreshComDevices();}; strip.Controls.Add(btnRef);
        var btnReq=Btn("Solicitar Peso",new Point(664,8),new Size(130,32),Cpurple);
        btnReq.Click+=OnScaleRequest; strip.Controls.Add(btnReq);
        pg.Controls.Add(strip);
        // Weight card
        int wy=155,ww=380,wh=200;
        var cW=Card(CM,wy,ww,wh); pg.Controls.Add(cW);
        CardHdr(cW,"Peso Atual");
        lblWeight=Lbl("-.---",new Font("Segoe UI",40,FontStyle.Bold),Ctxt,new Point(0,38),new Size(ww,72));
        lblWeight.TextAlign=ContentAlignment.MiddleCenter;
        lblWeightUnit=Lbl("kg",new Font("Segoe UI",16),Csub,new Point(0,112),new Size(ww,28));
        lblWeightUnit.TextAlign=ContentAlignment.MiddleCenter;
        lblScaleStatus=Lbl("Desconectado",new Font("Segoe UI",8),Csub,new Point(8,wh-26),new Size(ww-16,18));
        lblScaleStatus.TextAlign=ContentAlignment.MiddleCenter;
        cW.Controls.AddRange(new Control[]{lblWeight,lblWeightUnit,lblScaleStatus});
        // Detection card
        int iw=CW-CM*2-ww-CG;
        var cI=Card(CM+ww+CG,wy,iw,wh); pg.Controls.Add(cI);
        CardHdr(cI,"Dispositivos nas Portas COM");
        lstComDev=new ListBox{Location=new Point(8,38),Size=new Size(iw-16,wh-90),Font=new Font("Segoe UI",8.5f),ForeColor=Ctxt,BackColor=Ccard,BorderStyle=BorderStyle.None,IntegralHeight=false};
        lstComDev.SelectedIndexChanged+=(s,e)=>{
            if(lstComDev.SelectedItem==null) return;
            var m2=Regex.Match(lstComDev.SelectedItem.ToString(),@"^(COM\d+)",RegexOptions.IgnoreCase);
            if(m2.Success&&cmbComPorts.Items.Contains(m2.Groups[1].Value)) cmbComPorts.SelectedItem=m2.Groups[1].Value;
        };
        var btnDetCom=Btn("Detectar",new Point(8,wh-46),new Size(100,30),Cblue);
        btnDetCom.Click+=(s,e)=>RefreshComDevices();
        cI.Controls.AddRange(new Control[]{lstComDev,btnDetCom});
        // Log
        int ly=wy+wh+CG;
        var cLog=Card(CM,ly,CW-CM*2,FH-ly-12); pg.Controls.Add(cLog);
        CardHdr(cLog,"Dados Recebidos (RAW)");
        scaleLog=new RichTextBox{Location=new Point(8,40),Size=new Size(cLog.Width-16,cLog.Height-88),ReadOnly=true,BackColor=Ccard,Font=new Font("Consolas",9),ForeColor=Ctxt,BorderStyle=BorderStyle.None,ScrollBars=RichTextBoxScrollBars.Vertical};
        var btnClr=Btn("Limpar",new Point(8,cLog.Height-44),new Size(100,30),Color.FromArgb(80,80,80));
        btnClr.Click+=(s,e)=>{ if(scaleLog!=null)scaleLog.Clear(); };
        cLog.Controls.AddRange(new Control[]{scaleLog,btnClr});
        RefreshScalePorts();
        RefreshComDevices();
    }

    void RefreshComDevices(){
        if(lstComDev==null) return;
        ThreadPool.QueueUserWorkItem(delegate(object st){
            var dict=new System.Collections.Generic.SortedDictionary<int,string>();
            try{
                foreach(ManagementObject o in new ManagementObjectSearcher("SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%)'").Get()){
                    var nm=o["Name"]!=null?o["Name"].ToString():"";
                    var mm=Regex.Match(nm,@"\(COM(\d+)\)");
                    if(mm.Success){int idx=int.Parse(mm.Groups[1].Value); if(!dict.ContainsKey(idx))dict[idx]="COM"+idx+" — "+Regex.Replace(nm,@"\s*\(COM\d+\)","").Trim();}
                }
            }catch{}
            foreach(var p in SerialPort.GetPortNames()){
                var mm=Regex.Match(p,@"COM(\d+)",RegexOptions.IgnoreCase);
                if(mm.Success){int idx=int.Parse(mm.Groups[1].Value); if(!dict.ContainsKey(idx))dict[idx]=p+" — dispositivo nao identificado";}
            }
            var lines=new List<string>(); foreach(var kv in dict)lines.Add(kv.Value);
            if(!IsHandleCreated) return;
            try{ BeginInvoke((Action)(()=>{
                if(lstComDev==null||lstComDev.IsDisposed) return;
                lstComDev.Items.Clear();
                if(lines.Count==0){lstComDev.Items.Add("Nenhuma porta COM detectada."); return;}
                foreach(var l in lines) lstComDev.Items.Add(l);
            })); }catch{}
        });
    }

    void RefreshScalePorts(){
        if(cmbComPorts==null) return;
        var ports=SerialPort.GetPortNames();
        Array.Sort(ports);
        Action upd=()=>{
            string sel=cmbComPorts.SelectedItem!=null?cmbComPorts.SelectedItem.ToString():null;
            cmbComPorts.Items.Clear();
            foreach(var p in ports) cmbComPorts.Items.Add(p);
            if(sel!=null&&cmbComPorts.Items.Contains(sel)) cmbComPorts.SelectedItem=sel;
            else if(cmbComPorts.Items.Count>0) cmbComPorts.SelectedIndex=0;
        };
        if(cmbComPorts.InvokeRequired) cmbComPorts.BeginInvoke(upd); else upd();
    }

    void OnScaleConnect(object s,EventArgs e){
        if(scalePort!=null&&scalePort.IsOpen){
            try{scalePort.Close(); scalePort.Dispose();}catch{}
            scalePort=null;
            lock(scaleBufLock){ scaleBuf.Clear(); }
            btnScaleConnect.Text="Conectar"; btnScaleConnect.BackColor=Cacc;
            lblWeight.Text="-.---"; lblScaleStatus.Text="Desconectado";
            AppendScaleLog("Desconectado.");
            return;
        }
        if(cmbComPorts.SelectedItem==null){MessageBox.Show("Selecione uma porta COM.","Atencao",MessageBoxButtons.OK,MessageBoxIcon.Warning); return;}
        string port=cmbComPorts.SelectedItem.ToString();
        int baud=9600; if(cmbBaudRate.SelectedItem!=null)int.TryParse(cmbBaudRate.SelectedItem.ToString(),out baud);
        try{
            lock(scaleBufLock){ scaleBuf.Clear(); }
            scalePort=new SerialPort(port,baud,Parity.None,8,StopBits.One);
            scalePort.NewLine="\r\n"; scalePort.ReadTimeout=200;
            scalePort.DataReceived+=OnScaleData;
            scalePort.Open();
            btnScaleConnect.Text="Desconectar"; btnScaleConnect.BackColor=Cerr;
            lblScaleStatus.Text="Conectado: "+port+" @ "+baud+" baud";
            AppendScaleLog("Conectado em "+port+" @ "+baud+" baud");
        }catch(Exception ex){
            scalePort=null;
            MessageBox.Show("Erro ao abrir "+port+":\n"+ex.Message,"Erro",MessageBoxButtons.OK,MessageBoxIcon.Error);
        }
    }

    void OnScaleRequest(object s,EventArgs e){
        if(scalePort==null||!scalePort.IsOpen){MessageBox.Show("Conecte a balanca primeiro.","Atencao",MessageBoxButtons.OK,MessageBoxIcon.Warning); return;}
        try{
            string proto=cmbScaleProtocol.SelectedItem!=null?cmbScaleProtocol.SelectedItem.ToString():"";
            if(proto.IndexOf("Filizola",StringComparison.OrdinalIgnoreCase)>=0)
                scalePort.Write(new byte[]{0x02,0x50,0x03},0,3); // STX + 'P' + ETX
            else
                scalePort.Write(new byte[]{0x05},0,1); // ENQ (Toledo / generico)
            AppendScaleLog("Comando enviado ("+proto+")");
        }catch(Exception ex){AppendScaleLog("Erro ao enviar: "+ex.Message);}
    }

    void OnScaleData(object sender,SerialDataReceivedEventArgs e){
        try{
            if(scalePort==null||!scalePort.IsOpen) return;
            string data=scalePort.ReadExisting();
            if(string.IsNullOrEmpty(data)) return;
            List<string> frames;
            lock(scaleBufLock){
                scaleBuf.Append(data);
                if(scaleBuf.Length>8192) scaleBuf.Clear(); // protocolo desconhecido/lixo — evita crescer sem fim
                frames=ExtractScaleFrames(scaleBuf);
            }
            BeginInvoke((Action)(()=>{
                // Mostra sempre o que chegou, mesmo que ainda nao feche um frame — assim da pra ver
                // no log se a balanca esta respondendo ou nao, mesmo com protocolo desconhecido.
                string cleanRaw=data.Replace("\r"," ").Replace("\n"," ").Trim();
                if(cleanRaw.Length>0) AppendScaleLog(cleanRaw);
                bool gotWeight=false;
                foreach(var f in frames){
                    string w=ParseScaleWeight(f);
                    if(w!=null){
                        lblWeight.Text=w; lblWeight.ForeColor=Ctxt; gotWeight=true;
                        if(lblScaleStatus!=null&&!lblScaleStatus.Text.StartsWith("Peso"))
                            lblScaleStatus.Text="Ultima leitura: "+DateTime.Now.ToString("HH:mm:ss");
                    }
                }
                // Fallback: tenta ler o peso direto do pedaco cru — cobre protocolos sem STX/ETX
                // e sem \r\n (ex.: frame de tamanho fixo sem delimitador nenhum).
                if(!gotWeight){
                    string w2=ParseScaleWeight(data);
                    if(w2!=null){
                        lblWeight.Text=w2; lblWeight.ForeColor=Ctxt;
                        if(lblScaleStatus!=null&&!lblScaleStatus.Text.StartsWith("Peso"))
                            lblScaleStatus.Text="Ultima leitura: "+DateTime.Now.ToString("HH:mm:ss");
                    }
                }
            }));
        }catch{}
    }

    // O SerialPort entrega os bytes conforme chegam, nao por telegrama — ReadExisting() pode
    // devolver um frame cortado ao meio. Aqui acumula-se tudo e so se extrai o que estiver
    // completo (STX..ETX para protocolos tipo Toledo, ou linha terminada em \r/\n para os demais),
    // deixando qualquer sobra parcial no buffer para ser completada no proximo evento.
    List<string> ExtractScaleFrames(System.Text.StringBuilder buf){
        var outList=new List<string>();
        string s=buf.ToString();
        if(s.IndexOf('\x02')>=0){
            int consumed=0,stx;
            while((stx=s.IndexOf('\x02',consumed))>=0){
                int etx=s.IndexOf('\x03',stx+1);
                if(etx<0) break; // frame incompleto — aguarda mais dados
                outList.Add(s.Substring(stx,etx-stx+1));
                consumed=etx+1;
            }
            if(consumed>0) buf.Remove(0,consumed);
            return outList;
        }
        {
            int consumed=0,nl;
            while((nl=s.IndexOfAny(new[]{'\r','\n'},consumed))>=0){
                string line=s.Substring(consumed,nl-consumed);
                if(line.Trim().Length>0) outList.Add(line);
                consumed=nl+1;
            }
            if(consumed>0) buf.Remove(0,consumed);
            return outList;
        }
    }

    string ParseScaleWeight(string data){
        // Toledo Prix 3 Fit/Plus: STX + P/I + sign + weight + Kg + ETX
        // e.g. "\x02P+  0.500Kg\r\x03" or "\x02I   1.234Kg\r\x03"
        var mToledo=Regex.Match(data,@"[PI]([\+\-\s])\s*(\d{1,5}[.,]\d{2,3})\s*[Kk][Gg]");
        if(mToledo.Success){
            string v=mToledo.Groups[2].Value.Replace(",",".");
            bool stable=data.IndexOf('P')>=0&&mToledo.Value.StartsWith("P");
            if(lblWeightUnit!=null) lblWeightUnit.Text="kg";
            if(lblScaleStatus!=null&&scalePort!=null&&scalePort.IsOpen)
                lblScaleStatus.Text=(stable?"Peso estavel":"Peso instavel")+" — "+DateTime.Now.ToString("HH:mm:ss");
            return v;
        }
        // Filizola / generico: qualquer numero com . ou ,
        var m=Regex.Match(data,@"[\+\-]?\s*(\d{1,5}[.,]\d{2,3})\s*[kKgG]{0,2}");
        if(m.Success){
            string v=m.Groups[1].Value.Replace(",",".");
            if(data.IndexOf(" g",StringComparison.OrdinalIgnoreCase)>=0&&data.IndexOf("kg",StringComparison.OrdinalIgnoreCase)<0){
                if(lblWeightUnit!=null) lblWeightUnit.Text="g";
            } else {
                if(lblWeightUnit!=null) lblWeightUnit.Text="kg";
            }
            return v;
        }
        return null;
    }

    void AppendScaleLog(string msg){
        if(scaleLog==null||scaleLog.IsDisposed) return;
        if(scaleLog.InvokeRequired){scaleLog.BeginInvoke((Action)(()=>AppendScaleLog(msg))); return;}
        if(scaleLog.TextLength>50000) scaleLog.Text=scaleLog.Text.Substring(scaleLog.TextLength-20000); // balanca em modo continuo nao trava a UI
        scaleLog.SelectionStart=scaleLog.TextLength;
        scaleLog.SelectionColor=Csub;
        scaleLog.AppendText("["+DateTime.Now.ToString("HH:mm:ss")+"]  "+msg+"\n");
        scaleLog.ScrollToCaret();
    }

    void BuildNetConfigPage(){
        var pg=pages[7]; PageHeader(pg,"Config IP","Configure o IP de impressoras XPrinter, Epson, Bematech e Elgin (e clones da mesma placa de rede) via Ethernet ou USB. Outras marcas podem nao responder ao comando.");
        int cw=640; int lx=16, vx=148, tw=182, bw=88;
        int cardH=452;
        var cNet=Card(CM,95,cw,cardH); pg.Controls.Add(cNet);
        CardHdr(cNet,"Configuracao de Rede (NET)");
        int cy=46;
        // --- mode radio buttons ---
        var rbEth=new RadioButton{Text="Via Ethernet (cabo direto)",Location=new Point(lx,cy),Size=new Size(210,20),Font=new Font("Segoe UI",8.5f),ForeColor=Ctxt,Checked=true};
        var rbUsb=new RadioButton{Text="Via USB",Location=new Point(lx+216,cy),Size=new Size(110,20),Font=new Font("Segoe UI",8.5f),ForeColor=Ctxt};
        cNet.Controls.AddRange(new Control[]{rbEth,rbUsb}); cy+=28;
        cNet.Controls.Add(new Panel{Location=new Point(0,cy),Size=new Size(cw,1),BackColor=Cbord}); cy+=10;
        // === pEth and pUsb at SAME y — only one visible at a time ===
        int modeY=cy;
        // ETHERNET panel: IP Local + IP Impressora (height=58)
        var pEth=new Panel{Location=new Point(0,modeY),Size=new Size(cw,58),BackColor=Color.Transparent};
        cNet.Controls.Add(pEth);
        int lw2=vx-lx-6;
        pEth.Controls.Add(new Label{Text="IP Local (PC):",Font=new Font("Segoe UI",8,FontStyle.Bold),ForeColor=Csub,BackColor=Color.Transparent,Location=new Point(lx,3),Size=new Size(lw2,16),AutoSize=false});
        var txtLoc=new TextBox{Location=new Point(vx,0),Size=new Size(tw,22),Font=new Font("Segoe UI",9),BackColor=Color.FromArgb(30,30,30),ForeColor=Csub,BorderStyle=BorderStyle.FixedSingle,ReadOnly=true,Text=GetLocalIp("")};
        pEth.Controls.Add(txtLoc);
        pEth.Controls.Add(new Label{Text="IP da Impressora:",Font=new Font("Segoe UI",8,FontStyle.Bold),ForeColor=Csub,BackColor=Color.Transparent,Location=new Point(lx,31),Size=new Size(lw2,16),AutoSize=false});
        var txtCI=new TextBox{Location=new Point(vx,28),Size=new Size(tw,22),Font=new Font("Segoe UI",9),BackColor=Ccard,ForeColor=Ctxt,BorderStyle=BorderStyle.FixedSingle,Text="192.168.123.100"};
        var btnDisc=Btn("Descobrir",new Point(vx+tw+6,27),new Size(bw,24),Cblue);
        var btnPing=Btn("Ping",new Point(vx+tw+6+bw+4,27),new Size(50,24),Color.FromArgb(80,80,80));
        pEth.Controls.AddRange(new Control[]{txtCI,btnDisc,btnPing});
        // USB panel: impressora combo + hint (same height=58)
        var pUsb=new Panel{Location=new Point(0,modeY),Size=new Size(cw,58),BackColor=Color.Transparent,Visible=false};
        cNet.Controls.Add(pUsb);
        pUsb.Controls.Add(new Label{Text="Impressora USB:",Font=new Font("Segoe UI",8,FontStyle.Bold),ForeColor=Csub,BackColor=Color.Transparent,Location=new Point(lx,3),Size=new Size(lw2,16),AutoSize=false});
        var cmbUP=new ComboBox{Location=new Point(vx,0),Size=new Size(cw-vx-16,22),Font=new Font("Segoe UI",8.5f),DropDownStyle=ComboBoxStyle.DropDownList,BackColor=Ccard,ForeColor=Ctxt};
        pUsb.Controls.Add(cmbUP);
        pUsb.Controls.Add(new Label{Text="Coloque a impressora em modo de config de rede antes de aplicar\n(desligue, segure FEED, ligue).",
            Font=new Font("Segoe UI",7.5f,FontStyle.Italic),ForeColor=Csub,Location=new Point(vx,28),Size=new Size(cw-vx-16,30),AutoSize=false});
        cy=modeY+62;
        // === Common: nova configuracao ===
        cNet.Controls.Add(new Panel{Location=new Point(0,cy),Size=new Size(cw,1),BackColor=Cbord}); cy+=8;
        cNet.Controls.Add(Lbl("Nova configuracao:",new Font("Segoe UI",8,FontStyle.Bold),Ctxt,new Point(lx,cy),new Size(cw-20,16))); cy+=24;
        var chkDhcp=new CheckBox{Text="DHCP (IP automatico — nao preencher os campos abaixo)",Location=new Point(vx,cy),Size=new Size(cw-vx-16,20),Font=new Font("Segoe UI",8.5f),ForeColor=Ctxt};
        cNet.Controls.Add(chkDhcp); cy+=28;
        var lblNI=Lbl("Novo IP:",new Font("Segoe UI",8,FontStyle.Bold),Csub,new Point(lx,cy+3),new Size(lw2,16));
        var txtNI=new TextBox{Location=new Point(vx,cy),Size=new Size(tw,22),Font=new Font("Segoe UI",9),BackColor=Ccard,ForeColor=Ctxt,BorderStyle=BorderStyle.FixedSingle};
        cNet.Controls.AddRange(new Control[]{lblNI,txtNI}); cy+=28;
        var lblMk=Lbl("Mascara:",new Font("Segoe UI",8,FontStyle.Bold),Csub,new Point(lx,cy+3),new Size(lw2,16));
        var txtMk=new TextBox{Location=new Point(vx,cy),Size=new Size(tw,22),Font=new Font("Segoe UI",9),BackColor=Ccard,ForeColor=Ctxt,BorderStyle=BorderStyle.FixedSingle,Text="255.255.255.0"};
        cNet.Controls.AddRange(new Control[]{lblMk,txtMk}); cy+=28;
        var lblGw=Lbl("Gateway:",new Font("Segoe UI",8,FontStyle.Bold),Csub,new Point(lx,cy+3),new Size(lw2,16));
        var txtGw=new TextBox{Location=new Point(vx,cy),Size=new Size(tw,22),Font=new Font("Segoe UI",9),BackColor=Ccard,ForeColor=Ctxt,BorderStyle=BorderStyle.FixedSingle};
        cNet.Controls.AddRange(new Control[]{lblGw,txtGw}); cy+=36;
        // === Status + Apply ===
        cNet.Controls.Add(new Panel{Location=new Point(0,cy),Size=new Size(cw,1),BackColor=Cbord}); cy+=10;
        cNet.Controls.Add(new Label{Text="IPs padrao — XPrinter: 192.168.123.100   Epson: 192.168.192.168   Bematech/Elgin: 192.168.0.1",
            Font=new Font("Segoe UI",7.5f),ForeColor=Csub,Location=new Point(lx,cy),Size=new Size(cw-20,16),AutoSize=false}); cy+=22;
        var lblSt=new Label{Text="",Font=new Font("Segoe UI",8.5f),ForeColor=Csub,Location=new Point(lx,cy),Size=new Size(cw-20,32),AutoSize=false};
        cNet.Controls.Add(lblSt); cy+=38;
        var btnAp=Btn("Aplicar (Set New IP)",new Point(lx,cy),new Size(210,34),Cacc);
        var btnVerifyTemp=Btn("Verificar e Remover IP Temporario",new Point(lx+220,cy),new Size(260,34),Corange);
        btnVerifyTemp.Visible=false;
        var lblTempIpInfo=new Label{Text="",Font=new Font("Segoe UI",7.5f),ForeColor=Corange,Location=new Point(lx,cy+38),Size=new Size(cw-20,16),AutoSize=false,Visible=false};
        cNet.Controls.AddRange(new Control[]{btnAp,btnVerifyTemp,lblTempIpInfo});
        // --- populate USB printers ---
        try{foreach(ManagementObject o in new ManagementObjectSearcher("SELECT * FROM Win32_Printer").Get()){
            var pn=o["PortName"]!=null?o["PortName"].ToString():""; var nm=o["Name"]!=null?o["Name"].ToString():"";
            if(pn.StartsWith("USB",StringComparison.OrdinalIgnoreCase)) cmbUP.Items.Add(nm+" ("+pn+")");
        }}catch{}
        if(cmbUP.Items.Count==0) cmbUP.Items.Add("(nenhuma impressora USB instalada)");
        cmbUP.SelectedIndex=0;
        // --- wire events ---
        rbEth.CheckedChanged+=(s,e)=>{if(rbEth.Checked){pEth.Visible=true; pUsb.Visible=false;}};
        rbUsb.CheckedChanged+=(s,e)=>{if(rbUsb.Checked){pEth.Visible=false; pUsb.Visible=true;}};
        chkDhcp.CheckedChanged+=(s,e)=>{bool m=!chkDhcp.Checked; txtNI.Enabled=m; txtMk.Enabled=m; txtGw.Enabled=m;};
        txtCI.TextChanged+=(s,e)=>{string loc=GetLocalIp(txtCI.Text.Trim()); if(loc.Length>6) txtLoc.Text=loc;};
        // Discover
        btnDisc.Click+=(s,e)=>{
            btnDisc.Enabled=false; lblSt.Text="Descobrindo..."; lblSt.ForeColor=Csub;
            ThreadPool.QueueUserWorkItem(delegate(object st2){
                var found=new List<string>();
                try{
                    using(var udp=new System.Net.Sockets.UdpClient()){
                        udp.EnableBroadcast=true; udp.Client.ReceiveTimeout=2000;
                        byte[] probe=new byte[]{0x00,0x04,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00};
                        udp.Send(probe,probe.Length,new System.Net.IPEndPoint(System.Net.IPAddress.Broadcast,3000));
                        var recv=new System.Net.IPEndPoint(System.Net.IPAddress.Any,0);
                        long dl=Environment.TickCount+2000;
                        while(Environment.TickCount<dl){
                            try{
                                byte[] resp=udp.Receive(ref recv);
                                string rip=recv.Address.ToString();
                                if(!rip.StartsWith("127")&&!found.Exists(x=>x.StartsWith(rip))){
                                    string mdl="XPrinter";
                                    if(resp.Length>12){int si=8; while(si<resp.Length&&resp[si]!=0)si++; if(si>8)try{mdl=System.Text.Encoding.ASCII.GetString(resp,8,si-8).Trim();}catch{}}
                                    found.Add(rip+" — "+mdl);
                                }
                            }catch{}
                        }
                    }
                }catch{}
                foreach(string hip in new string[]{"192.168.123.100","192.168.192.168","192.168.0.100","192.168.1.100","10.0.0.100"}){
                    if(found.Exists(x=>x.StartsWith(hip))) continue;
                    try{using(var sk=new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,System.Net.Sockets.SocketType.Stream,System.Net.Sockets.ProtocolType.Tcp)){
                        var ar3=sk.BeginConnect(hip,9100,null,null);
                        if(ar3.AsyncWaitHandle.WaitOne(600)){try{sk.EndConnect(ar3);}catch{}
                            if(sk.Connected){string tg=hip.StartsWith("192.168.123")?"XPrinter":hip.StartsWith("192.168.192")?"Epson":"Impressora"; found.Add(hip+" — "+tg+" (padrao fabrica)");}}
                    }}catch{}
                }
                BeginInvoke((Action)(()=>{
                    if(found.Count>0){
                        var mm2=Regex.Match(found[0],@"(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})");
                        if(mm2.Success) txtCI.Text=mm2.Groups[1].Value;
                        lblSt.Text=found.Count+" encontrada(s): "+string.Join(", ",found.ToArray()); lblSt.ForeColor=Cacc;
                    }else{lblSt.Text="Nenhuma encontrada — verifique o cabo e o IP."; lblSt.ForeColor=Cerr;}
                    btnDisc.Enabled=true;
                }));
            });
        };
        // Ping
        btnPing.Click+=(s,e)=>{
            string ci=txtCI.Text.Trim(); if(ci.Length<7){lblSt.Text="Informe o IP da impressora."; lblSt.ForeColor=Cerr; return;}
            lblSt.Text="Pingando "+ci+"..."; lblSt.ForeColor=Csub; btnPing.Enabled=false;
            ThreadPool.QueueUserWorkItem(delegate(object st2){
                bool ok=false; string msg="";
                try{
                    using(var sk=new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,System.Net.Sockets.SocketType.Stream,System.Net.Sockets.ProtocolType.Tcp)){
                        int t0=Environment.TickCount;
                        var ar3=sk.BeginConnect(ci,9100,null,null);
                        ok=ar3.AsyncWaitHandle.WaitOne(2000);
                        int ms=Environment.TickCount-t0;
                        if(ok){try{sk.EndConnect(ar3);}catch{} msg="OK — "+ci+":9100 acessivel ("+ms+"ms)";}
                        else msg="Sem resposta de "+ci+":9100 — verifique o cabo.";
                    }
                }catch(Exception ex){msg="Erro: "+ex.Message;}
                BeginInvoke((Action)(()=>{lblSt.Text=msg; lblSt.ForeColor=ok?Cacc:Cerr; btnPing.Enabled=true;}));
            });
        };
        // Apply
        btnAp.Click+=(s,e)=>{
            bool dhcp=chkDhcp.Checked;
            string ni=txtNI.Text.Trim(), mk=txtMk.Text.Trim(), gw2=txtGw.Text.Trim();
            if(!dhcp&&!Regex.IsMatch(ni,@"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$")){lblSt.Text="Novo IP invalido."; lblSt.ForeColor=Cerr; return;}
            lblSt.Text="Aplicando..."; lblSt.ForeColor=Csub; btnAp.Enabled=false;
            if(rbEth.Checked){
                string ci=txtCI.Text.Trim(), locIp=txtLoc.Text.Trim();
                if(!Regex.IsMatch(ci,@"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$")){lblSt.Text="IP da impressora invalido."; lblSt.ForeColor=Cerr; btnAp.Enabled=true; return;}
                bool reachable; string adapter=FindAdapterForSubnet(ci,out reachable);
                if(!reachable){
                    if(adapter==null){lblSt.Text="Seu PC nao esta na rede de "+ci+" e nao achei uma placa de rede para usar."; lblSt.ForeColor=Cerr; btnAp.Enabled=true; return;}
                    string tempIp0=PickTempIpInSubnet(ci);
                    var dr3=MessageBox.Show(
                        "Seu computador nao esta na mesma rede de "+ci+".\n\n"+
                        "Para conseguir falar com a impressora, vou adicionar temporariamente o IP "+tempIp0+
                        " na placa \""+adapter+"\", aplicar a configuracao, e remover esse IP temporario em seguida.\n\n"+
                        "Continuar?","Rede diferente",MessageBoxButtons.YesNo,MessageBoxIcon.Question);
                    if(dr3!=DialogResult.Yes){ lblSt.Text="Cancelado."; lblSt.ForeColor=Csub; btnAp.Enabled=true; return; }
                }
                ThreadPool.QueueUserWorkItem(delegate(object st2){
                    string tempIp=null;
                    if(!reachable){
                        tempIp=PickTempIpInSubnet(ci);
                        UILog("PC fora da rede de "+ci+" — adicionando IP temporario "+tempIp+" na placa \""+adapter+"\"...");
                        if(!AddTempIp(adapter,tempIp,"255.255.255.0")){
                            BeginInvoke((Action)(()=>{lblSt.Text="Nao foi possivel adicionar o IP temporario na placa \""+adapter+"\". Execute como Administrador."; lblSt.ForeColor=Cerr; btnAp.Enabled=true;}));
                            return;
                        }
                        System.Threading.Thread.Sleep(1200);
                        locIp=tempIp;
                    }
                    string r=ApplyNetConfigXP(locIp,ci,ni,mk,gw2,dhcp);
                    if(tempIp!=null){
                        // Nao remove o IP temporario agora: a impressora normalmente so assume o IP
                        // novo depois de reiniciar. Removendo aqui, se ainda nao reiniciou (ou o
                        // comando nao funcionou), perdemos a unica rota pra falar com ela de novo.
                        tempIpActive=tempIp; tempIpAdapter=adapter;
                        r+="\n\nIP temporario "+tempIp+" MANTIDO na placa \""+adapter+"\". Reinicie a impressora"+
                           " e clique em \"Verificar e Remover IP Temporario\" para confirmar e limpar.";
                    }
                    BeginInvoke((Action)(()=>{
                        lblSt.Text=r; lblSt.ForeColor=r.StartsWith("OK")?Cacc:Cerr; btnAp.Enabled=true;
                        if(tempIp!=null){ btnVerifyTemp.Visible=true; lblTempIpInfo.Text="IP temporario ativo: "+tempIp+" na placa \""+adapter+"\" (nao removido ainda)."; lblTempIpInfo.Visible=true; }
                    }));
                });
            }else{
                if(cmbUP.SelectedItem==null||cmbUP.SelectedItem.ToString().StartsWith("(nenhuma")){lblSt.Text="Nenhuma impressora USB disponivel."; lblSt.ForeColor=Cerr; btnAp.Enabled=true; return;}
                var itm=cmbUP.SelectedItem.ToString(); var mm=Regex.Match(itm,@"^(.+?)\s*\(USB\d+\)$");
                string prtName=mm.Success?mm.Groups[1].Value.Trim():itm;
                ThreadPool.QueueUserWorkItem(delegate(object st2){
                    string r=ApplyNetConfigUsb(prtName,ni,mk,gw2,dhcp);
                    BeginInvoke((Action)(()=>{lblSt.Text=r; lblSt.ForeColor=r.StartsWith("OK")?Cacc:Cerr; btnAp.Enabled=true;}));
                });
            }
        };
        // Verificar e Remover IP Temporario
        btnVerifyTemp.Click+=(s,e)=>{
            if(tempIpActive==null){ btnVerifyTemp.Visible=false; lblTempIpInfo.Visible=false; return; }
            string targetNewIp=txtNI.Text.Trim();
            if(!Regex.IsMatch(targetNewIp,@"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$")){
                MessageBox.Show("Informe o novo IP da impressora no campo 'Novo IP' para verificar.","Atencao",MessageBoxButtons.OK,MessageBoxIcon.Warning); return;
            }
            btnVerifyTemp.Enabled=false; lblSt.Text="Verificando "+targetNewIp+"..."; lblSt.ForeColor=Csub;
            ThreadPool.QueueUserWorkItem(delegate(object st3){
                bool ok=false;
                try{
                    using(var sk=new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,System.Net.Sockets.SocketType.Stream,System.Net.Sockets.ProtocolType.Tcp)){
                        var ar4=sk.BeginConnect(targetNewIp,9100,null,null);
                        ok=ar4.AsyncWaitHandle.WaitOne(2000);
                        if(ok){try{sk.EndConnect(ar4);}catch{ok=false;}}
                    }
                }catch{}
                if(ok){
                    string ip2=tempIpActive, ad2=tempIpAdapter;
                    UILog("Impressora respondeu em "+targetNewIp+" — removendo IP temporario "+ip2+" da placa \""+ad2+"\"...");
                    RemoveTempIp(ad2,ip2);
                    tempIpActive=null; tempIpAdapter=null;
                    BeginInvoke((Action)(()=>{
                        lblSt.Text="OK - impressora respondeu em "+targetNewIp+" (porta 9100). IP temporario removido."; lblSt.ForeColor=Cacc;
                        btnVerifyTemp.Visible=false; lblTempIpInfo.Visible=false; btnVerifyTemp.Enabled=true;
                    }));
                }else{
                    BeginInvoke((Action)(()=>{
                        lblSt.Text="Ainda sem resposta em "+targetNewIp+":9100. Confirme que a impressora reiniciou (ela so assume o IP novo apos reiniciar) e tente de novo — o IP temporario continua ativo.";
                        lblSt.ForeColor=Cerr; btnVerifyTemp.Enabled=true;
                    }));
                }
            });
        };

        // === Ferramentas do fabricante (utilitarios oficiais de config de rede) ===
        int y2=95+cardH+CG;
        var cTools=Card(CM,y2,CW-CM*2,150); pg.Controls.Add(cTools);
        CardHdr(cTools,"Ferramentas do Fabricante");
        cTools.Controls.Add(Lbl(
            "Para marcas sem protocolo suportado acima, coloque o utilitario oficial de rede do fabricante em:\n"+
            "<pasta do Delitools>\\NetConfigTools\\{MARCA}\\ — o Delitools lista e abre por aqui.",
            new Font("Segoe UI",8),Csub,new Point(10,32),new Size(cTools.Width-20,32)));
        cTools.Controls.Add(Lbl("Marca:",new Font("Segoe UI",8,FontStyle.Bold),Csub,new Point(10,74),new Size(50,18)));
        var cmbToolBrand=new ComboBox{Location=new Point(62,71),Size=new Size(220,24),DropDownStyle=ComboBoxStyle.DropDownList,Font=new Font("Segoe UI",9)};
        cTools.Controls.Add(cmbToolBrand);
        cTools.Controls.Add(Lbl("Ferramenta:",new Font("Segoe UI",8,FontStyle.Bold),Csub,new Point(296,74),new Size(70,18)));
        var cmbToolFile=new ComboBox{Location=new Point(368,71),Size=new Size(260,24),DropDownStyle=ComboBoxStyle.DropDownList,Font=new Font("Segoe UI",9)};
        cTools.Controls.Add(cmbToolFile);
        var btnOpenTool=Btn("Abrir",new Point(cTools.Width-160,70),new Size(70,26),Cacc);
        var btnOpenFolder=Btn("Abrir Pasta",new Point(cTools.Width-84,70),new Size(74,26),Color.FromArgb(80,80,80));
        var btnAutoConfig=Btn("Automatizar (Beta)",new Point(700,70),new Size(150,26),Corange);
        cTools.Controls.AddRange(new Control[]{btnOpenTool,btnOpenFolder,btnAutoConfig});
        var lblToolsStatus=new Label{Text="",Font=new Font("Segoe UI",8),ForeColor=Csub,Location=new Point(10,106),Size=new Size(cTools.Width-20,18),AutoSize=false};
        cTools.Controls.Add(lblToolsStatus);
        // Marcas com automacao de UI implementada (AutoConfigBrands, nivel de classe) — clica
        // sozinho na ferramenta do fabricante. So funciona pra impressora ligada por cabo Ethernet
        // direto no PC, mesma exigencia da aba "Via Ethernet" acima.
        Action updateAutoBtn=()=>{ btnAutoConfig.Enabled=cmbToolBrand.SelectedItem!=null&&AutoConfigBrands.Contains(cmbToolBrand.SelectedItem.ToString()); };
        Action refreshToolBrands=()=>{
            cmbToolBrand.Items.Clear(); cmbToolFile.Items.Clear();
            foreach(var b in GetNetToolBrands()) cmbToolBrand.Items.Add(b);
            if(cmbToolBrand.Items.Count>0){ cmbToolBrand.SelectedIndex=0; lblToolsStatus.Text=""; }
            else lblToolsStatus.Text="Nenhuma pasta em NetConfigTools ainda. Clique em \"Abrir Pasta\" e crie uma subpasta com o nome da marca (ex: Selton) com o .exe dela dentro.";
            updateAutoBtn();
        };
        cmbToolBrand.SelectedIndexChanged+=(s,e)=>{
            cmbToolFile.Items.Clear();
            if(cmbToolBrand.SelectedItem==null) return;
            foreach(var f in GetNetToolFiles(cmbToolBrand.SelectedItem.ToString())) cmbToolFile.Items.Add(f);
            if(cmbToolFile.Items.Count>0){ cmbToolFile.SelectedIndex=0; lblToolsStatus.Text=""; }
            else lblToolsStatus.Text="Nenhum .exe/.msi encontrado nessa pasta.";
            updateAutoBtn();
        };
        btnOpenTool.Click+=(s,e)=>{
            if(cmbToolBrand.SelectedItem==null||cmbToolFile.SelectedItem==null){ lblToolsStatus.Text="Selecione a marca e a ferramenta."; lblToolsStatus.ForeColor=Cerr; return; }
            string brandFolder=Path.Combine(netToolsRoot,cmbToolBrand.SelectedItem.ToString());
            string fileName=cmbToolFile.SelectedItem.ToString();
            string path=Path.Combine(brandFolder,fileName);
            ExcludeFromDefender(brandFolder,fileName);
            try{ Process.Start(new ProcessStartInfo(path){UseShellExecute=true}); lblToolsStatus.Text="Aberto: "+fileName; lblToolsStatus.ForeColor=Cacc; Log("Ferramenta do fabricante aberta: "+path); }
            catch(Exception ex){ lblToolsStatus.Text="Erro ao abrir: "+ex.Message; lblToolsStatus.ForeColor=Cerr; }
        };
        btnOpenFolder.Click+=(s,e)=>{
            try{ if(!Directory.Exists(netToolsRoot)) Directory.CreateDirectory(netToolsRoot); Process.Start("explorer.exe",netToolsRoot); }
            catch(Exception ex){ lblToolsStatus.Text="Erro ao abrir pasta: "+ex.Message; lblToolsStatus.ForeColor=Cerr; }
        };
        btnAutoConfig.Click+=(s,e)=>{
            string ni2=txtNI.Text.Trim(), mk2=txtMk.Text.Trim(), gw3=txtGw.Text.Trim();
            if(chkDhcp.Checked){ lblToolsStatus.Text="Automacao ainda so cobre IP manual — desmarque DHCP acima."; lblToolsStatus.ForeColor=Cerr; return; }
            if(!Regex.IsMatch(ni2,@"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$")){ lblToolsStatus.Text="Preencha o campo 'Novo IP' (mais acima) antes de automatizar."; lblToolsStatus.ForeColor=Cerr; return; }
            btnAutoConfig.Enabled=false; lblToolsStatus.Text="Automatizando (Bixolon)... isso abre a ferramenta e clica sozinho, nao mexa na janela dela."; lblToolsStatus.ForeColor=Csub;
            ThreadPool.QueueUserWorkItem(delegate(object st){
                string r=AutoConfigBixolon(ni2,mk2.Length>6?mk2:"255.255.255.0",gw3.Length>6?gw3:"0.0.0.0");
                BeginInvoke((Action)(()=>{ lblToolsStatus.Text=r; lblToolsStatus.ForeColor=r.StartsWith("OK")?Cacc:Cerr; btnAutoConfig.Enabled=true; }));
            });
        };
        refreshToolBrands();

        // === Assistente guiado (pergunta marca, oferece manual/automatico, pede o cabo) ===
        var cWiz=Card(CM+cw+CG,95,CW-CM*2-cw-CG,cardH); pg.Controls.Add(cWiz);
        CardHdr(cWiz,"Assistente Guiado");
        var pnlWiz=new Panel{Location=new Point(10,36),Size=new Size(cWiz.Width-20,cWiz.Height-46),BackColor=Color.Transparent};
        cWiz.Controls.Add(pnlWiz);

        int wizStep=0; string wizBrand=null,wizMode=null;
        Action renderWizard=null;
        renderWizard=()=>{
            pnlWiz.Controls.Clear();
            int yy=0; int ww=pnlWiz.Width;
            Action<string,int,FontStyle,Color> addTxt=(t,h,fs,col)=>{ pnlWiz.Controls.Add(new Label{Text=t,Font=new Font("Segoe UI",8.5f,fs),ForeColor=col,Location=new Point(0,yy),Size=new Size(ww,h),AutoSize=false}); yy+=h+6; };
            Action<string,Color,bool,Action> addBtn=(t,col,enabled,onClick)=>{
                var b=Btn(t,new Point(0,yy),new Size(ww,32),col); b.Enabled=enabled; if(onClick!=null) b.Click+=(s,e)=>onClick();
                pnlWiz.Controls.Add(b); yy+=38;
            };

            if(wizStep==0){
                addTxt("1. Qual a marca da impressora?",30,FontStyle.Bold,Ctxt);
                foreach(var b in GetAllBrandNames()){
                    string bb=b;
                    addBtn(bb,Color.FromArgb(60,64,72),true,()=>{ wizBrand=bb; wizStep=1; renderWizard(); });
                }
            }
            else if(wizStep==1){
                addTxt("Marca: "+wizBrand,20,FontStyle.Bold,Ctxt);
                string toolFolder=BrandToolFolder(wizBrand);
                if(BrandIsNative(wizBrand)&&toolFolder==null){
                    addTxt("Essa marca ja e configurada automaticamente pelo Delitools. Preencha 'Novo IP' a esquerda e clique em 'Aplicar (Set New IP)' — nao precisa de ferramenta externa.",90,FontStyle.Regular,Csub);
                    addBtn("Recomecar",Color.FromArgb(80,80,80),true,()=>{ wizStep=0; wizBrand=null; renderWizard(); });
                } else if(toolFolder!=null){
                    if(BrandIsNative(wizBrand)) addTxt("Essa marca tem suporte nativo automatico (campos a esquerda). Pra usar a ferramenta oficial mesmo assim, escolha abaixo:",60,FontStyle.Regular,Csub);
                    else addTxt("Essa marca nao tem protocolo nativo suportado — precisa da ferramenta oficial do fabricante.",50,FontStyle.Regular,Csub);
                    addBtn("Manual — eu mesmo configuro",Cblue,true,()=>{ wizMode="manual"; wizStep=2; renderWizard(); });
                    bool autoOk=AutoConfigBrands.Contains(toolFolder);
                    addBtn(autoOk?"Automatico (Beta)":"Automatico (ainda nao disponivel)",autoOk?Corange:Color.FromArgb(150,150,150),autoOk,()=>{ wizMode="auto"; wizStep=2; renderWizard(); });
                    addBtn("Voltar",Color.FromArgb(80,80,80),true,()=>{ wizStep=0; wizBrand=null; renderWizard(); });
                } else {
                    addTxt("Essa marca nao tem protocolo nativo nem ferramenta cadastrada em NetConfigTools. Adicione a pasta da marca ali embaixo, ou peca pra implementar o protocolo dela.",80,FontStyle.Regular,Cerr);
                    addBtn("Voltar",Color.FromArgb(80,80,80),true,()=>{ wizStep=0; wizBrand=null; renderWizard(); });
                }
            }
            else if(wizStep==2){
                addTxt("2. Conecte a impressora "+wizBrand+" por cabo Ethernet direto neste computador (evite passar por switch/roteador, se der) e ligue ela.",80,FontStyle.Regular,Ctxt);
                addBtn("Ja conectei, continuar",Cacc,true,()=>{ wizStep=3; renderWizard(); });
                addBtn("Voltar",Color.FromArgb(80,80,80),true,()=>{ wizStep=1; renderWizard(); });
            }
            else if(wizStep==3){
                string toolFolder=BrandToolFolder(wizBrand);
                addTxt("3. "+(wizMode=="manual"?"Configuracao manual":"Configuracao automatica")+" — "+wizBrand,20,FontStyle.Bold,Ctxt);
                var lblResult=new Label{Text="",Font=new Font("Segoe UI",8.5f),ForeColor=Csub,Location=new Point(0,yy),Size=new Size(ww,100),AutoSize=false};
                pnlWiz.Controls.Add(lblResult); yy+=106;
                addBtn("Recomecar",Color.FromArgb(80,80,80),true,()=>{ wizStep=0; wizBrand=null; wizMode=null; renderWizard(); });

                if(wizMode=="manual"){
                    var files=GetNetToolFiles(toolFolder);
                    if(files.Count==0){ lblResult.Text="Nenhum executavel encontrado em NetConfigTools\\"+toolFolder+"."; lblResult.ForeColor=Cerr; }
                    else{
                        string path=Path.Combine(netToolsRoot,toolFolder,files[0]);
                        try{ Process.Start(new ProcessStartInfo(path){UseShellExecute=true}); lblResult.Text="Ferramenta aberta ("+files[0]+"). Configure o IP manualmente na janela dela."; lblResult.ForeColor=Cacc; Log("Assistente: ferramenta aberta - "+path); }
                        catch(Exception ex){ lblResult.Text="Erro ao abrir: "+ex.Message; lblResult.ForeColor=Cerr; }
                    }
                } else {
                    string ni2=txtNI.Text.Trim(), mk2=txtMk.Text.Trim(), gw3=txtGw.Text.Trim();
                    if(!Regex.IsMatch(ni2,@"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$")){
                        lblResult.Text="Preencha o campo 'Novo IP' na Configuracao de Rede (a esquerda) antes de continuar."; lblResult.ForeColor=Cerr;
                    } else {
                        lblResult.Text="Automatizando... isso abre a ferramenta e clica sozinho, nao mexa na janela dela.";
                        lblResult.ForeColor=Csub;
                        ThreadPool.QueueUserWorkItem(delegate(object st){
                            string r=toolFolder.Equals("BIXOLON",StringComparison.OrdinalIgnoreCase)?AutoConfigBixolon(ni2,mk2.Length>6?mk2:"255.255.255.0",gw3.Length>6?gw3:"0.0.0.0"):"Automacao ainda nao implementada pra essa marca.";
                            BeginInvoke((Action)(()=>{ lblResult.Text=r; lblResult.ForeColor=r.StartsWith("OK")?Cacc:Cerr; }));
                        });
                    }
                }
            }
        };
        renderWizard();
    }

    // Automatiza o "Net Configuration Tool" da Bixolon: abre, busca a impressora na rede local,
    // seleciona a primeira encontrada, le o IP atual dela, e grava o IP novo.
    // Mapeamento da janela (v3.3.1): dialogo "NetConfiguration Tool" > pagina "LAN/WLAN" tem os
    // controles Search(1159)/Lista(1124)/IP atual(1134)/Configuration(1126); clicar em
    // Configuration troca pra pagina "Network Configuration" (outro dialogo irmao) com Manual(1041)
    // + 3 SysIPAddress32 (IP=1119, Mascara=1120, Gateway=1121) + Save(1126).
    string AutoConfigBixolon(string newIp,string newMask,string newGw){
        string exe=Path.Combine(netToolsRoot,"BIXOLON","NetConfiguration.exe");
        if(!File.Exists(exe)) return "Erro: NetConfiguration.exe nao encontrado em NetConfigTools\\BIXOLON.";
        Process proc=null;
        try{
            UILog("Automacao Bixolon: liberando no antivirus...");
            ExcludeFromDefender(Path.GetDirectoryName(exe),"NetConfiguration.exe");
            UILog("Automacao Bixolon: abrindo NetConfiguration.exe...");
            proc=Process.Start(new ProcessStartInfo(exe){UseShellExecute=true,WorkingDirectory=Path.GetDirectoryName(exe)});
            IntPtr hMain=IntPtr.Zero;
            for(int i=0;i<20&&hMain==IntPtr.Zero;i++){ System.Threading.Thread.Sleep(500); proc.Refresh(); hMain=proc.MainWindowHandle; }
            if(hMain==IntPtr.Zero) return "Erro: a janela da ferramenta nao abriu a tempo.";
            System.Threading.Thread.Sleep(800);

            var all=EnumAllChildren(hMain);
            IntPtr hLanWlan=FindByText(all,"LAN/WLAN");
            if(hLanWlan==IntPtr.Zero) return "Erro: nao reconheci a tela da ferramenta (layout pode ter mudado).";
            IntPtr hSearch=FindByIdParent(all,hLanWlan,1159);
            IntPtr hList=FindByIdParent(all,hLanWlan,1124);
            IntPtr hCurIp=FindByIdParent(all,hLanWlan,1134);
            IntPtr hConfigBtn=FindByIdParent(all,hLanWlan,1126);
            if(hSearch==IntPtr.Zero||hList==IntPtr.Zero||hConfigBtn==IntPtr.Zero) return "Erro: nao achei os controles esperados na tela de busca.";

            UILog("Automacao Bixolon: buscando impressora na rede (cabo Ethernet direto)...");
            ClickCtrl(hSearch);
            int count=0;
            for(int i=0;i<12;i++){ System.Threading.Thread.Sleep(1000); count=(int)SendMessage(hList,LVM_GETITEMCOUNT,IntPtr.Zero,IntPtr.Zero); if(count>0) break; }
            if(count==0) return "Nenhuma impressora Bixolon encontrada na rede. Confirme que ela esta ligada e conectada por cabo Ethernet direto no PC.";

            SelectFirstListItem(hList);
            System.Threading.Thread.Sleep(500);
            string ipAtual=GetCtrlText(hCurIp);
            UILog("Automacao Bixolon: impressora encontrada, IP atual = "+ipAtual);

            ClickCtrl(hConfigBtn);
            System.Threading.Thread.Sleep(800);
            all=EnumAllChildren(hMain); // a tela de configuracao so existe/atualiza apos abrir
            IntPtr hNetCfg=FindByText(all,"Network Configuration");
            if(hNetCfg==IntPtr.Zero) return "OK-PARCIAL - Achei a impressora (IP atual "+ipAtual+"), mas a tela de configuracao nao abriu. Termine manualmente na janela aberta.";
            IntPtr hManual=FindByIdParent(all,hNetCfg,1041);
            IntPtr hIpCtl=FindByIdParent(all,hNetCfg,1119);
            IntPtr hMaskCtl=FindByIdParent(all,hNetCfg,1120);
            IntPtr hGwCtl=FindByIdParent(all,hNetCfg,1121);
            IntPtr hSave=FindByIdParent(all,hNetCfg,1126);
            if(hIpCtl==IntPtr.Zero||hSave==IntPtr.Zero) return "OK-PARCIAL - Achei a impressora (IP atual "+ipAtual+"), mas nao reconheci os campos da tela de configuracao. Termine manualmente na janela aberta.";

            if(hManual!=IntPtr.Zero) ClickCtrl(hManual);
            SetIpControl(hIpCtl,newIp);
            if(hMaskCtl!=IntPtr.Zero) SetIpControl(hMaskCtl,newMask);
            if(hGwCtl!=IntPtr.Zero) SetIpControl(hGwCtl,newGw);
            System.Threading.Thread.Sleep(300);
            UILog("Automacao Bixolon: gravando "+newIp+"...");
            ClickCtrl(hSave);
            System.Threading.Thread.Sleep(1000);

            return "OK - IP anterior: "+ipAtual+". Comando de gravacao enviado para "+newIp+".\n"+
                   "A impressora pode pedir reinicio pra aplicar — confira na propria ferramenta (deixei a janela aberta) se apareceu alguma confirmacao.";
        }catch(Exception ex){
            return "Erro na automacao: "+ex.Message;
        }
    }

    // ════════════════════════════════════════════════════════
    //  ASSISTENTE (CHAT) — mesma decisao do Assistente Guiado (marca -> manual/automatico),
    //  so que como conversa, numa area separada, com tudo automatizado exceto quando o
    //  usuario escolhe fazer manual (ai so abre a ferramenta certa, igual ao wizard).
    // ════════════════════════════════════════════════════════
    struct ChatOpt{ public string Label; public Color Col; public bool Enabled; public Action OnClick; public ChatOpt(string l,Color c,bool e,Action a){Label=l;Col=c;Enabled=e;OnClick=a;} }

    int MeasureTextHeight(string text,Font f,int width){
        var sz=TextRenderer.MeasureText(text,f,new Size(width,int.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.Left);
        return sz.Height;
    }
    void ChatScrollToBottom(Control last){ try{ pnlChatLog.ScrollControlIntoView(last); }catch{} }
    void AddBotBubble(string text){
        int bw=(int)(chatWidth*0.74); var f=new Font("Segoe UI",9);
        int h=MeasureTextHeight(text,f,bw-24)+22;
        var p=new Panel{Location=new Point(4,chatY),Size=new Size(bw,h),BackColor=CaiBubble};
        p.Region=Region.FromHrgn(CreateRoundRectRgn(0,0,bw,h,14,14));
        p.Controls.Add(new Label{Text=text,Font=f,ForeColor=Ctxt,Location=new Point(12,10),Size=new Size(bw-24,h-20),AutoSize=false});
        pnlChatLog.Controls.Add(p); chatY+=h+12; ChatScrollToBottom(p);
    }
    void AddUserBubble(string text){
        int bw=(int)(chatWidth*0.74); var f=new Font("Segoe UI",9,FontStyle.Bold);
        int h=MeasureTextHeight(text,f,bw-24)+22;
        var p=new Panel{Location=new Point(chatWidth-bw-4,chatY),Size=new Size(bw,h),BackColor=CaiAccent};
        p.Region=Region.FromHrgn(CreateRoundRectRgn(0,0,bw,h,14,14));
        p.Controls.Add(new Label{Text=text,Font=f,ForeColor=Color.White,Location=new Point(12,10),Size=new Size(bw-24,h-20),AutoSize=false});
        pnlChatLog.Controls.Add(p); chatY+=h+12; ChatScrollToBottom(p);
    }
    void AddOptions(params ChatOpt[] opts){
        int bw=(int)(chatWidth*0.74);
        var buttons=new List<Button>();
        for(int i=0;i<opts.Length;i++){
            var b=Btn(opts[i].Label,new Point(4,chatY),new Size(bw,30),opts[i].Col); b.Enabled=opts[i].Enabled;
            buttons.Add(b); pnlChatLog.Controls.Add(b); chatY+=36;
        }
        for(int i=0;i<opts.Length;i++){
            var opt=opts[i];
            buttons[i].Click+=(s,e)=>{ foreach(var bb in buttons) bb.Enabled=false; AddUserBubble(opt.Label); opt.OnClick(); };
        }
        if(buttons.Count>0) ChatScrollToBottom(buttons[buttons.Count-1]);
        chatY+=6;
    }
    void AddTextPrompt(string hint,Action<string> onSubmit){
        int bw=(int)(chatWidth*0.74);
        var tb=new TextBox{Location=new Point(4,chatY+2),Size=new Size(bw-72,26),Font=new Font("Segoe UI",9)};
        var btn=Btn("Enviar",new Point(4+bw-64,chatY),new Size(64,30),CaiAccent);
        pnlChatLog.Controls.Add(tb); pnlChatLog.Controls.Add(btn); chatY+=40;
        Action submit=()=>{
            string v=tb.Text.Trim(); if(v.Length==0) return;
            tb.Enabled=false; btn.Enabled=false;
            AddUserBubble(v);
            onSubmit(v);
        };
        btn.Click+=(s,e)=>submit();
        tb.KeyDown+=(s,e)=>{ if(e.KeyCode==Keys.Enter){ e.SuppressKeyPress=true; submit(); } };
        ChatScrollToBottom(tb);
        tb.Focus();
    }

    void StartIpConfigChat(){
        pnlChatLog.Controls.Clear(); chatY=6;
        AddBotBubble("Show, vamos trocar o IP dela. Qual a marca da impressora?");
        var opts=new List<ChatOpt>();
        foreach(var b in GetAllBrandNames()){ string bb=b; opts.Add(new ChatOpt(bb,Color.FromArgb(60,64,72),true,()=>OnChatBrand(bb))); }
        AddOptions(opts.ToArray());
    }

    // === Menu principal do chat — cobre todas as telas do Delitools ===
    const string BotName="Dely";
    ChatOpt ChatBack(){ return new ChatOpt("Voltar ao menu",Color.FromArgb(80,80,80),true,()=>StartChat()); }

    void StartChat(){
        pnlChatLog.Controls.Clear(); chatY=6;
        AddBotBubble("Oi, tudo bem? Eu sou a "+BotName+", assistente do Delitools. Me conta, no que eu posso te ajudar hoje?");
        AddOptions(
            new ChatOpt("Instalar uma impressora",CaiAccent,true,()=>ChatMenuInstall()),
            new ChatOpt("Ver minhas impressoras instaladas",Color.FromArgb(60,64,72),true,()=>ChatMenuInstalled()),
            new ChatOpt("Detectar o que esta conectado",Color.FromArgb(60,64,72),true,()=>ChatMenuDetect()),
            new ChatOpt("Minha impressao esta com problema",Color.FromArgb(60,64,72),true,()=>ChatMenuFix()),
            new ChatOpt("Ferramentas (spooler, gaveta, backup)",Color.FromArgb(60,64,72),true,()=>ChatMenuTools()),
            new ChatOpt("Imprimir uma pagina de teste",Color.FromArgb(60,64,72),true,()=>ChatMenuTestPage()),
            new ChatOpt("Falar da balanca",Color.FromArgb(60,64,72),true,()=>ChatMenuScales()),
            new ChatOpt("Trocar o IP de uma impressora de rede",Color.FromArgb(60,64,72),true,()=>StartIpConfigChat())
        );
    }

    // --- Instalar impressora ---
    void ChatMenuInstall(){
        AddBotBubble("Bora instalar! Como voce quer chamar essa impressora? Pode ser algo tipo \"Caixa 1\" ou \"Cozinha\", o que fizer sentido pra voce.");
        AddTextPrompt("ex: Balcao 1",(name)=>{
            AddBotBubble("Perfeito, \""+name+"\" anotado. Ela ta ligada no computador por USB ou por Rede (Ethernet/Wi-Fi)?");
            AddOptions(
                new ChatOpt("USB",CaiAccent,true,()=>ChatInstallUsb(name)),
                new ChatOpt("Rede",Color.FromArgb(60,64,72),true,()=>ChatInstallNetAskIp(name))
            );
        });
    }
    void ChatInstallUsb(string name){
        AddBotBubble("Beleza. So confirma pra mim: a impressora ta ligada na tomada e com o cabo USB conectado nesse computador?");
        AddOptions(new ChatOpt("Sim, pode instalar",CaiAccent,true,()=>{
            AddBotBubble("Show, deixa comigo. Instalando \""+name+"\"... pode levar uns 20-30 segundos, ja te aviso quando terminar.");
            ThreadPool.QueueUserWorkItem(delegate(object st){
                try{ CreatePrinter(name,false); }catch{}
                bool okF=PrinterExists(name);
                SendTelemetry("instalar_usb",okF?"ok":"falhou",name);
                BeginInvoke((Action)(()=>{
                    AddBotBubble(okF?"Prontinho! \""+name+"\" foi instalada com sucesso, ja pode usar.":"Hmm, nao consegui confirmar que \""+name+"\" foi instalada de verdade. Da uma olhada no log detalhado na aba \"Instalar Impressora\" (modo tecnico) pra ver o que rolou — pode ter sido a porta USB ou o driver. Confere o cabo e a energia dela e tenta de novo comigo.");
                    AddOptions(ChatBack());
                }));
            });
        }));
    }
    void ChatInstallNetAskIp(string name){
        AddBotBubble("Certo, impressora de rede. Qual o IP dela?");
        AddTextPrompt("ex: 192.168.1.50",(ip)=>{
            if(!Regex.IsMatch(ip,@"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$")){ AddBotBubble("Esse IP nao me parece valido — tem que ser 4 numeros separados por ponto, tipo 192.168.1.50. Tenta de novo?"); AddTextPrompt("ex: 192.168.1.50",(ip2)=>ChatInstallNetGo(name,ip2)); return; }
            ChatInstallNetGo(name,ip);
        });
    }
    void ChatInstallNetGo(string name,string ip){
        AddBotBubble("Anotado. Instalando \""+name+"\" em "+ip+", so um instante...");
        ThreadPool.QueueUserWorkItem(delegate(object st){
            bool ok=false; try{ ok=RunNetworkInstall(ip,"9100",name,false); }catch{}
            bool okF=ok;
            SendTelemetry("instalar_rede",okF?"ok":"falhou",ip);
            BeginInvoke((Action)(()=>{ AddBotBubble(okF?"Prontinho! Impressora de rede instalada com sucesso.":"Nao rolou dessa vez — confere se o IP esta certo e se a impressora esta ligada, e tenta de novo comigo."); AddOptions(ChatBack()); }));
        });
    }

    // --- Impressoras instaladas ---
    void ChatMenuInstalled(){
        var pp=GetRealPrinters();
        if(pp.Length==0){ AddBotBubble("Olhei aqui e nao achei nenhuma impressora instalada ainda. Quer que eu te ajude a instalar uma?"); AddOptions(new ChatOpt("Instalar uma impressora",CaiAccent,true,()=>ChatMenuInstall()),ChatBack()); return; }
        AddBotBubble("Essas sao as impressoras que voce tem instaladas:\n"+string.Join("\n",pp));
        AddBotBubble("Quer fazer algo com alguma delas?");
        AddOptions(
            new ChatOpt("Imprimir teste em uma delas",Color.FromArgb(60,64,72),true,()=>ChatPickPrinter(pp,"imprimir teste em",(n)=>ChatRunVerifiedTest(n))),
            new ChatOpt("Definir uma como padrao",Color.FromArgb(60,64,72),true,()=>ChatPickPrinter(pp,"definir como padrao",(n)=>{ SetDefaultPrinter(n); AddBotBubble("Feito! "+n+" agora e a sua impressora padrao."); AddOptions(ChatBack()); })),
            new ChatOpt("Remover uma",Cerr,true,()=>ChatPickPrinter(pp,"remover",(n)=>ChatConfirmRemove(n))),
            ChatBack()
        );
    }
    void ChatPickPrinter(string[] pp,string action,Action<string> onPick){
        AddBotBubble("Qual delas voce quer "+action+"?");
        var opts=new List<ChatOpt>();
        foreach(var p in pp){ string pn=p; opts.Add(new ChatOpt(pn,Color.FromArgb(60,64,72),true,()=>onPick(pn))); }
        AddOptions(opts.ToArray());
    }
    void ChatConfirmRemove(string name){
        AddBotBubble("Peraí, quero ter certeza: voce quer mesmo remover \""+name+"\"? Depois de remover nao da pra desfazer.");
        AddOptions(
            new ChatOpt("Sim, pode remover",Cerr,true,()=>{ OnRemoveName(name); AddBotBubble("Prontinho, removi \""+name+"\"."); AddOptions(ChatBack()); }),
            new ChatOpt("Deixa quieto, cancelar",Color.FromArgb(80,80,80),true,()=>{ AddBotBubble("Tranquilo, cancelei — ela continua instalada."); AddOptions(ChatBack()); })
        );
    }

    // --- Detectar impressoras conectadas ---
    void ChatMenuDetect(){
        AddBotBubble("Deixa eu dar uma olhada no que ta conectado por USB...");
        ThreadPool.QueueUserWorkItem(delegate(object st){
            List<UsbPrinterCandidate> found=null; try{ found=ScanUsbPrinterCandidates(); }catch{}
            var fr=found;
            BeginInvoke((Action)(()=>{
                if(fr==null||fr.Count==0){
                    AddBotBubble("Nao achei nenhum dispositivo USB com cara de impressora — vale conferir se ela ta ligada e o cabo bem encaixado.");
                } else {
                    var sb=new System.Text.StringBuilder("Achei "+fr.Count+" dispositivo(s) USB que parecem impressora:\n");
                    bool anyNotHigh=false;
                    foreach(var f in fr){ sb.Append("- "+f.Name+" (certeza "+f.Confidence+")\n"); if(f.Confidence!="alta") anyNotHigh=true; }
                    AddBotBubble(sb.ToString().TrimEnd());
                    if(anyNotHigh) AddBotBubble("Alguns eu identifiquei com certeza media ou baixa — vale voce confirmar visualmente se e mesmo a impressora antes de instalar.");
                }
                AddOptions(ChatBack());
            }));
        });
    }

    // --- Corrigir impressao ---
    void ChatMenuFix(){
        List<QueueHealth> health=null; try{ health=GetQueueHealth(); }catch{}
        QueueHealth? sickest=null;
        if(health!=null&&health.Count>0){
            var sick=health.FindAll(h=>h.Score>0);
            var healthy=health.FindAll(h=>h.Score==0);
            if(sick.Count>0) sickest=sick[0];
            if(health.Count>1){
                if(sick.Count>0){
                    var sb=new System.Text.StringBuilder("Dei uma olhada em todas as suas impressoras. A \""+sick[0].Name+"\" ta com problema: "+string.Join("; ",sick[0].Sintomas)+".");
                    if(healthy.Count>0) sb.Append(" As outras ("+string.Join(", ",healthy.ConvertAll(h=>h.Name).ToArray())+") estao funcionando normalmente, nao vou mexer nelas.");
                    AddBotBubble(sb.ToString());
                } else {
                    AddBotBubble("Dei uma olhada nas suas "+health.Count+" impressoras e nenhuma delas parece ter problema agora. Me conta o que ta acontecendo, que eu tento ajudar mesmo assim:");
                }
            } else if(sick.Count>0){
                AddBotBubble("Vi que a \""+sick[0].Name+"\" ta com problema: "+string.Join("; ",sick[0].Sintomas)+". Vamos resolver.");
            } else {
                AddBotBubble("Vamos resolver isso. O que esta acontecendo?");
            }
        } else {
            AddBotBubble("Vamos resolver isso. O que esta acontecendo?");
        }
        var opts=new List<ChatOpt>();
        if(sickest!=null&&!sickest.Value.PortoVivo){
            var sk=sickest.Value;
            opts.Add(new ChatOpt("Reconectar \""+sk.Name+"\" (recomendado)",CaiAccent,true,()=>ChatOfferReconnect(sk.Name,sk.Driver)));
        }
        opts.Add(new ChatOpt("Reiniciar o Spooler",Color.FromArgb(60,64,72),true,()=>{
                AddBotBubble("Ok, reiniciando o Spooler de impressao...");
                ThreadPool.QueueUserWorkItem(delegate(object st){ RestartSpooler(false); SendTelemetry("reiniciar_spooler","ok",""); BeginInvoke((Action)(()=>{ AddBotBubble("Pronto, Spooler reiniciado. Tenta imprimir de novo pra ver se resolveu."); RefreshStatus(); AddOptions(ChatBack()); })); });
            }));
        opts.Add(new ChatOpt("Limpar fila de impressao",Color.FromArgb(60,64,72),true,()=>{
                AddBotBubble("Isso vai apagar todos os trabalhos que estao esperando na fila agora. Pode confirmar?");
                AddOptions(
                    new ChatOpt("Sim, pode limpar",Cerr,true,()=>{
                        AddBotBubble("Certo, limpando a fila...");
                        ThreadPool.QueueUserWorkItem(delegate(object st){ RestartSpooler(true); SendTelemetry("limpar_fila","ok",""); BeginInvoke((Action)(()=>{ AddBotBubble("Pronto, fila limpa."); RefreshStatus(); AddOptions(ChatBack()); })); });
                    }),
                    new ChatOpt("Deixa quieto, cancelar",Color.FromArgb(80,80,80),true,()=>{ AddBotBubble("Tranquilo, nao mexi em nada."); AddOptions(ChatBack()); })
                );
            }));
        opts.Add(new ChatOpt("Abrir Gerenciador de Dispositivos",Color.FromArgb(60,64,72),true,()=>{
                try{Process.Start("devmgmt.msc");}catch{}
                AddBotBubble("Abri o Gerenciador de Dispositivos pra voce dar uma olhada.");
                AddOptions(ChatBack());
            }));
        opts.Add(ChatBack());
        AddOptions(opts.ToArray());
    }
    // --- Ferramentas ---
    void ChatMenuTools(){
        AddBotBubble("Tenho essas ferramentas aqui, qual voce precisa?");
        AddOptions(
            new ChatOpt("Status do Spooler",Color.FromArgb(60,64,72),true,()=>{
                AddBotBubble("O Spooler esta "+GetSpoolerStatus().ToLower()+", com "+GetQueueCount()+" documento(s) na fila.");
                AddOptions(ChatBack());
            }),
            new ChatOpt("Testar IP/porta de rede",Color.FromArgb(60,64,72),true,()=>ChatToolsPing()),
            new ChatOpt("Abrir gaveta de dinheiro",Color.FromArgb(60,64,72),true,()=>ChatToolsDrawer()),
            new ChatOpt("Fazer backup das impressoras",Color.FromArgb(60,64,72),true,()=>{
                AddBotBubble("Certo, fazendo o backup...");
                ThreadPool.QueueUserWorkItem(delegate(object st){
                    string file=BackupPrinters(); bool failed=file.StartsWith("(falhou");
                    BeginInvoke((Action)(()=>{ AddBotBubble(failed?"Deu erro no backup: "+file:"Prontinho, salvei o backup em: "+file); AddOptions(ChatBack()); }));
                });
            }),
            new ChatOpt("Restaurar backup",Color.FromArgb(60,64,72),true,()=>{
                AddBotBubble("Beleza, escolhe o arquivo de backup na janela que vai abrir.");
                var dlg=new OpenFileDialog{Title="Selecionar Backup",Filter="Backup|*.txt|Todos|*.*",FileName="PrinterBackup.txt"};
                if(dlg.ShowDialog()==DialogResult.OK){
                    string file=dlg.FileName;
                    AddBotBubble("Restaurando de "+file+", so um instante...");
                    ThreadPool.QueueUserWorkItem(delegate(object st){ RestorePrinters(file); BeginInvoke((Action)(()=>{ AddBotBubble("Prontinho, restauracao concluida."); AddOptions(ChatBack()); })); });
                } else { AddBotBubble("Tranquilo, cancelei."); AddOptions(ChatBack()); }
            }),
            ChatBack()
        );
    }
    void ChatToolsPing(){
        AddBotBubble("Qual o IP que voce quer que eu teste?");
        AddTextPrompt("ex: 192.168.1.50",(ip)=>{
            AddBotBubble("Testando "+ip+" na porta 9100...");
            ThreadPool.QueueUserWorkItem(delegate(object st){
                bool ok=TestTcpPort(ip,9100);
                BeginInvoke((Action)(()=>{ AddBotBubble(ok?"Boa, a porta 9100 esta aberta em "+ip+" — a impressora esta acessivel pela rede!":"Sem resposta de "+ip+" na porta 9100 — ela pode estar desligada, fora da rede ou o IP errado."); AddOptions(ChatBack()); }));
            });
        });
    }
    void ChatToolsDrawer(){
        var pp=GetRealPrinters();
        if(pp.Length==0){ AddBotBubble("Voce ainda nao tem nenhuma impressora instalada pra usar a gaveta."); AddOptions(ChatBack()); return; }
        ChatPickPrinter(pp,"usar pra abrir a gaveta",(n)=>{
            bool ok=SendRawBytes(n,new byte[]{0x1B,0x70,0x00,25,(byte)250});
            AddBotBubble(ok?"Prontinho, mandei o sinal pra "+n+".":"Nao consegui mandar o sinal pra "+n+" — confere se ela esta ligada.");
            AddOptions(ChatBack());
        });
    }

    // --- Imprimir pagina de teste ---
    void ChatMenuTestPage(){
        var pp=GetRealPrinters();
        if(pp.Length==0){ AddBotBubble("Voce ainda nao tem nenhuma impressora instalada pra eu testar."); AddOptions(new ChatOpt("Instalar uma impressora",CaiAccent,true,()=>ChatMenuInstall()),ChatBack()); return; }
        ChatPickPrinter(pp,"imprimir teste em",(n)=>ChatRunVerifiedTest(n));
    }
    // Manda o teste e so avisa sucesso depois de confirmar que saiu da fila de verdade.
    void ChatRunVerifiedTest(string name){
        AddBotBubble("Mandando um teste pra \""+name+"\"...");
        ThreadPool.QueueUserWorkItem(delegate(object st){
            bool drained=false; try{ drained=DoTestPageVerified(name); }catch{}
            bool okF=drained;
            SendTelemetry("teste_pagina",okF?"ok":"preso_na_fila",name);
            BeginInvoke((Action)(()=>{
                AddBotBubble(okF?"Prontinho, o teste saiu da fila — confere se o papel realmente imprimiu na impressora.":"Mandei o teste, mas ele ficou preso na fila e nao saiu — pode ser que a impressora esteja desligada, sem papel, ou com algum problema. Quer que eu tente diagnosticar?");
                if(okF) AddOptions(ChatBack());
                else AddOptions(new ChatOpt("Diagnosticar o problema",CaiAccent,true,()=>ChatMenuFix()),ChatBack());
            }));
        });
    }

    // --- Balancas: leitura ao vivo nao cabe em chat, leva pra tela dedicada ---
    void ChatMenuScales(){
        AddBotBubble("Balanca eu monitoro ao vivo numa tela dedicada (ela fica lendo o peso o tempo todo pela porta serial), aqui no chat nao rola direito. Clica ali que eu te levo pra la.");
        AddOptions(new ChatOpt("Ir para Balancas",CaiAccent,true,()=>ShowPage(6)));
    }
    void OnChatBrand(string brand){
        string toolFolder=BrandToolFolder(brand);
        bool native=BrandIsNative(brand);
        if(native&&toolFolder==null){
            AddBotBubble(brand+"? Otimo, essa eu configuro sozinha, sem precisar de ferramenta nenhuma. Voce sabe o IP atual dela?");
            AddTextPrompt("ex: 192.168.123.100",(curIp)=>OnChatNativeCurIp(brand,curIp));
        } else if(toolFolder!=null){
            bool autoOk=AutoConfigBrands.Contains(toolFolder);
            AddBotBubble("Pra "+brand+" eu tenho dois caminhos: quer que eu resolva tudo sozinha, ou prefere fazer voce mesmo com a ferramenta oficial do fabricante?");
            AddOptions(
                new ChatOpt(autoOk?"Deixa que eu faco tudo":"Automatico (ainda nao disponivel)",autoOk?CaiAccent:Color.FromArgb(150,150,150),autoOk,()=>OnChatWantAuto(brand,toolFolder)),
                new ChatOpt("Prefiro fazer manual",Color.FromArgb(60,64,72),true,()=>OnChatWantManual(brand,toolFolder))
            );
        } else {
            AddBotBubble("Poxa, essa marca eu ainda nao conheço. Se voce adicionar a pasta dela em NetConfigTools (modo tecnico > Config IP), ou pedir pra ensinarem o protocolo dela pra mim, eu aprendo rapidinho.");
            AddOptions(new ChatOpt("Voltar ao menu",Color.FromArgb(80,80,80),true,()=>StartChat()));
        }
    }
    void OnChatWantManual(string brand,string toolFolder){
        AddBotBubble("Combinado. So preciso que voce conecte a impressora "+brand+" por cabo Ethernet direto nesse computador e ligue ela.");
        AddOptions(new ChatOpt("Ja conectei",CaiAccent,true,()=>OnChatManualConnected(brand,toolFolder)));
    }
    void OnChatManualConnected(string brand,string toolFolder){
        var files=GetNetToolFiles(toolFolder);
        if(files.Count==0){ AddBotBubble("Que estranho, nao achei nenhum executavel em NetConfigTools\\"+toolFolder+". Confere se a ferramenta esta la."); AddOptions(new ChatOpt("Terminei, voltar ao menu",Color.FromArgb(80,80,80),true,()=>StartChat())); return; }
        AddBotBubble("Ja libero ela no antivirus pra nao correr risco de bloquear, e abro em seguida...");
        string brandFolder=Path.Combine(netToolsRoot,toolFolder);
        string fileName=files[0];
        ThreadPool.QueueUserWorkItem(delegate(object st){
            try{ ExcludeFromDefender(brandFolder,fileName); }catch{}
            BeginInvoke((Action)(()=>{
                string path=Path.Combine(brandFolder,fileName);
                try{ Process.Start(new ProcessStartInfo(path){UseShellExecute=true}); AddBotBubble("Abri a ferramenta ("+fileName+") pra voce. E so configurar o IP na janela que apareceu."); Log("Assistente (chat): ferramenta aberta - "+path); }
                catch(Exception ex){ AddBotBubble("Nao consegui abrir a ferramenta: "+ex.Message); }
                AddOptions(new ChatOpt("Terminei, voltar ao menu",Color.FromArgb(80,80,80),true,()=>StartChat()));
            }));
        });
    }
    void OnChatWantAuto(string brand,string toolFolder){
        AddBotBubble("Pode deixar comigo. Qual o IP novo que voce quer colocar na impressora?");
        AddTextPrompt("ex: 192.168.1.50",(newIp)=>OnChatGotNewIpAuto(brand,toolFolder,newIp));
    }
    void OnChatGotNewIpAuto(string brand,string toolFolder,string newIp){
        if(!Regex.IsMatch(newIp,@"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$")){
            AddBotBubble("Esse IP nao me parece valido — precisa ser 4 numeros separados por ponto, tipo 192.168.1.50. Tenta de novo?");
            AddTextPrompt("ex: 192.168.1.50",(ni)=>OnChatGotNewIpAuto(brand,toolFolder,ni));
            return;
        }
        AddBotBubble("Anotado: "+newIp+". Agora conecta a impressora "+brand+" por cabo Ethernet direto nesse computador e liga ela.");
        AddOptions(new ChatOpt("Ja conectei, pode automatizar",CaiAccent,true,()=>RunChatAutoBot(brand,toolFolder,newIp)));
    }
    void RunChatAutoBot(string brand,string toolFolder,string newIp){
        AddBotBubble("Beleza, vou abrir a ferramenta da "+brand+" e configurar sozinha. So nao mexe na janela dela enquanto eu trabalho, ta?");
        ThreadPool.QueueUserWorkItem(delegate(object st){
            string r=toolFolder.Equals("BIXOLON",StringComparison.OrdinalIgnoreCase)?AutoConfigBixolon(newIp,"255.255.255.0","0.0.0.0"):"Ainda nao aprendi a fazer isso sozinha pra essa marca — mas voce pode fazer manual, sem problema.";
            BeginInvoke((Action)(()=>{ AddBotBubble(r); AddOptions(new ChatOpt("Voltar ao menu",Color.FromArgb(80,80,80),true,()=>StartChat())); }));
        });
    }
    void OnChatNativeCurIp(string brand,string curIp){
        if(!Regex.IsMatch(curIp,@"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$")){
            AddBotBubble("Esse IP nao me parece valido — precisa ser 4 numeros separados por ponto, tipo 192.168.123.100. Tenta de novo?");
            AddTextPrompt("ex: 192.168.123.100",(ci)=>OnChatNativeCurIp(brand,ci));
            return;
        }
        AddBotBubble("Certo. E qual o IP novo que voce quer colocar nela?");
        AddTextPrompt("ex: 192.168.1.50",(newIp)=>OnChatNativeNewIp(brand,curIp,newIp));
    }
    void OnChatNativeNewIp(string brand,string curIp,string newIp){
        if(!Regex.IsMatch(newIp,@"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$")){
            AddBotBubble("Esse IP tambem nao parece valido — mesmo formato, 4 numeros separados por ponto. Tenta de novo?");
            AddTextPrompt("ex: 192.168.1.50",(ni)=>OnChatNativeNewIp(brand,curIp,ni));
            return;
        }
        AddBotBubble("Show, anotei tudo. Agora conecta a impressora "+brand+" por cabo Ethernet direto nesse computador e liga ela.");
        AddOptions(new ChatOpt("Ja conectei, pode automatizar",CaiAccent,true,()=>RunChatNativeAuto(brand,curIp,newIp)));
    }
    void RunChatNativeAuto(string brand,string curIp,string newIp){
        AddBotBubble("Configurando "+curIp+" para "+newIp+", so um instante...");
        ThreadPool.QueueUserWorkItem(delegate(object st){
            bool reachable; string adapter=FindAdapterForSubnet(curIp,out reachable);
            string tempIp=null; string locIp=GetLocalIp(curIp);
            if(!reachable){
                if(adapter==null){
                    BeginInvoke((Action)(()=>{ AddBotBubble("Seu computador nao esta na mesma rede de "+curIp+" e eu nao achei uma placa de rede pra usar como ponte."); AddOptions(new ChatOpt("Voltar ao menu",Color.FromArgb(80,80,80),true,()=>StartChat())); }));
                    return;
                }
                tempIp=PickTempIpInSubnet(curIp);
                BeginInvoke((Action)(()=>AddBotBubble("Seu computador nao esta na mesma rede dela — vou adicionar temporariamente o IP "+tempIp+" na placa \""+adapter+"\" so pra conseguir falar com ela.")));
                if(!AddTempIp(adapter,tempIp,"255.255.255.0")){
                    BeginInvoke((Action)(()=>{ AddBotBubble("Nao consegui adicionar o IP temporario. Tenta abrir o Delitools como Administrador e me chama de novo."); AddOptions(new ChatOpt("Voltar ao menu",Color.FromArgb(80,80,80),true,()=>StartChat())); }));
                    return;
                }
                System.Threading.Thread.Sleep(1200);
                locIp=tempIp;
            }
            string r=ApplyNetConfigXP(locIp,curIp,newIp,"255.255.255.0","0.0.0.0",false);
            if(tempIp!=null){ tempIpActive=tempIp; tempIpAdapter=adapter; r+="\n\nAh, e deixei o IP temporario "+tempIp+" na placa \""+adapter+"\" por enquanto — depois de reiniciar a impressora, va em Config IP (modo tecnico) e clica em \"Verificar e Remover IP Temporario\" pra eu confirmar e limpar isso."; }
            BeginInvoke((Action)(()=>{ AddBotBubble(r); AddOptions(new ChatOpt("Voltar ao menu",Color.FromArgb(80,80,80),true,()=>StartChat())); }));
        });
    }

    void BuildAssistantPage(){
        var pg=pages[8]; PageHeader(pg,BotName,"Converse com a "+BotName+" pra instalar impressora, corrigir problemas, configurar IP e mais — ela resolve tudo por voce, ou te leva pro caminho manual se preferir.");
        int chatH=FH-95-12;
        var cChat=Card(CM,95,CW-CM*2,chatH); pg.Controls.Add(cChat);
        // Header proprio (escuro, com indicador de status) — visual diferente do resto do app,
        // marcando que essa e a tela principal, mas ainda dentro do mesmo tom profissional.
        int headerH=48;
        var header=new Panel{Location=new Point(0,0),Size=new Size(cChat.Width,headerH),BackColor=CaiBg};
        header.Controls.Add(Lbl(BotName,new Font("Segoe UI",11,FontStyle.Bold),Color.White,new Point(16,8),new Size(320,22)));
        var dot=Lbl("●",new Font("Segoe UI",8),Color.FromArgb(70,220,140),new Point(16,30),new Size(16,14));
        var onlineTxt=Lbl("assistente do Delitools — online",new Font("Segoe UI",7.5f),Color.FromArgb(170,175,200),new Point(30,31),new Size(240,14));
        header.Controls.AddRange(new Control[]{dot,onlineTxt});
        cChat.Controls.Add(header);

        chatWidth=cChat.Width-20;
        int listTop=headerH+10;
        pnlChatLog=new Panel{Location=new Point(10,listTop),Size=new Size(cChat.Width-20,cChat.Height-listTop-46),BackColor=Ccard,AutoScroll=true,BorderStyle=BorderStyle.FixedSingle};
        cChat.Controls.Add(pnlChatLog);
        var btnRestart=Btn("Recomecar Conversa",new Point(10,cChat.Height-38),new Size(170,30),Color.FromArgb(80,80,80));
        btnRestart.Click+=(s,e)=>StartChat();
        cChat.Controls.Add(btnRestart);
        StartChat();
    }

    // ── Diagnostico multi-impressora (portado do FudoPrintDoctor) ──────────────
    // Avalia TODAS as filas reais (sem virtuais) e da uma pontuacao de severidade pra cada
    // uma — 0 = saudavel. A de maior pontuacao e a que deve ser diagnosticada; as saudaveis
    // nao sao tocadas. Um local com caixa+cozinha nao deve ter a impressora sadia mexida so
    // porque a outra esta com fila travada.
    struct QueueHealth{ public string Name,Port,Driver,Estado; public int Score,Jobs; public List<string> Sintomas; public bool Offline,Pausada,PortoVivo; }

    // Portas USBxxx que TEM um dispositivo presente agora — cruza o mapeamento historico do
    // registro (Enum\USBPRINT, que guarda toda porta que ja existiu) contra quem esta
    // realmente conectado neste momento (Win32_PnPEntity), pra nao achar que uma porta orfa
    // de uma impressora ja desconectada ainda esta "viva".
    HashSet<string> GetLiveUsbPorts(){
        var live=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try{
            var presentIds=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(ManagementObject o in new ManagementObjectSearcher("SELECT DeviceID FROM Win32_PnPEntity").Get()){
                var id=o["DeviceID"]!=null?o["DeviceID"].ToString():""; if(id.Length>0) presentIds.Add(id);
            }
            using(var root=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USBPRINT")){
                if(root!=null) foreach(var devClass in root.GetSubKeyNames())
                    using(var ck=root.OpenSubKey(devClass)){ if(ck==null) continue;
                        foreach(var inst in ck.GetSubKeyNames()){
                            string fullId="USBPRINT\\"+devClass+"\\"+inst;
                            using(var dp=ck.OpenSubKey(inst+@"\Device Parameters")){
                                if(dp==null) continue;
                                var pv=dp.GetValue("PortName") as string;
                                if(pv!=null&&pv.Length>0&&presentIds.Contains(fullId)) live.Add(pv);
                            }
                        }
                    }
            }
        }catch{}
        return live;
    }

    List<QueueHealth> GetQueueHealth(){
        var result=new List<QueueHealth>();
        var livePorts=GetLiveUsbPorts();
        var jobCounts=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        try{
            foreach(ManagementObject j in new ManagementObjectSearcher("SELECT Name FROM Win32_PrintJob").Get()){
                string jn=j["Name"]!=null?j["Name"].ToString():""; int comma=jn.IndexOf(',');
                string pn=comma>0?jn.Substring(0,comma):jn;
                if(pn.Length==0) continue;
                jobCounts[pn]=jobCounts.ContainsKey(pn)?jobCounts[pn]+1:1;
            }
        }catch{}
        try{
            foreach(ManagementObject o in new ManagementObjectSearcher("SELECT * FROM Win32_Printer").Get()){
                string name=o["Name"]!=null?o["Name"].ToString():""; if(name.Length==0) continue;
                string drv=o["DriverName"]!=null?o["DriverName"].ToString():"";
                string port=o["PortName"]!=null?o["PortName"].ToString():"";
                string vreason; if(IsVirtualPrinter(name,drv,port,out vreason)) continue;

                bool offline=false,pausada=false;
                try{ offline=o["WorkOffline"]!=null&&(bool)o["WorkOffline"]; }catch{}
                try{ int ps=o["PrinterState"]!=null?Convert.ToInt32(o["PrinterState"]):0; pausada=(ps&1)!=0; }catch{}
                int jobs=jobCounts.ContainsKey(name)?jobCounts[name]:0;

                bool portoVivo=true;
                if(Regex.IsMatch(port,@"^USB\d+",RegexOptions.IgnoreCase)) portoVivo=livePorts.Contains(port);

                int score=0; var sint=new List<string>();
                if(jobs>=3){ score+=40; sint.Add(jobs+" trabalhos parados na fila"); }
                else if(jobs>0){ score+=10; sint.Add(jobs+" trabalho(s) na fila"); }
                if(!portoVivo){ score+=30; sint.Add("a porta "+port+" nao tem nenhum dispositivo conectado"); }
                if(offline){ score+=25; sint.Add("marcada como sem conexao (offline)"); }
                if(pausada){ score+=20; sint.Add("pausada"); }

                string estado=score==0?"saudavel":(score>=40?"nao imprime":"com problemas");
                result.Add(new QueueHealth{Name=name,Port=port,Driver=drv,Estado=estado,Score=score,Jobs=jobs,Sintomas=sint,Offline=offline,Pausada=pausada,PortoVivo=portoVivo});
            }
        }catch{}
        result.Sort((a,b)=>b.Score.CompareTo(a.Score));
        return result;
    }

    // ── Reconexao guiada + recriacao segura de fila (portado do FudoPrintDoctor) ───
    bool QueueIsEmpty(string name){
        try{ return new ManagementObjectSearcher("SELECT Name FROM Win32_PrintJob WHERE Name LIKE '"+name.Replace("'","''")+",%'").Get().Count==0; }catch{ return true; }
    }
    // Recria uma fila que nao imprime em porta nenhuma: cria uma fila TEMPORARIA em cada porta
    // candidata, manda um ticket ESC/POS real, e SO quando uma responde de verdade (fila
    // esvaziou) e que apaga a antiga e renomeia a temporaria com o nome original — nunca deixa
    // o cliente sem fila. Retorna a porta que funcionou, ou null se nenhuma funcionou (nesse
    // caso nada foi alterado).
    string SafeRecreateQueue(string originalName,IEnumerable<string> candidatePorts,string driverName){
        foreach(var port in candidatePorts){
            string tmpName="DELITOOLS-TEST-"+port;
            try{ RunPS("Remove-Printer -Name '"+tmpName.Replace("'","''")+"'"); }catch{}
            string err=RunPS("Add-Printer -Name '"+tmpName.Replace("'","''")+"' -DriverName '"+driverName.Replace("'","''")+"' -PortName '"+port+"'");
            if(err.Trim().Length>0) continue;
            bool sent=false;
            try{ sent=SendRawBytes(tmpName,System.Text.Encoding.ASCII.GetBytes("\x1B@Delitools\n\n\n")); }catch{}
            System.Threading.Thread.Sleep(1500);
            bool drained=QueueIsEmpty(tmpName);
            if(sent&&drained){
                RunPS("Remove-Printer -Name '"+originalName.Replace("'","''")+"'");
                RunPS("Rename-Printer -Name '"+tmpName.Replace("'","''")+"' -NewName '"+originalName.Replace("'","''")+"'");
                return port;
            }
            try{ RunPS("Remove-Printer -Name '"+tmpName.Replace("'","''")+"'"); }catch{}
        }
        return null;
    }
    // Espera a pessoa desconectar/reconectar o cabo USB (a forma mais confiavel de resolver
    // "estava instalada e parou de imprimir": Windows reenumera o dispositivo e da porta nova),
    // detecta a porta que apareceu, e chama SafeRecreateQueue nela.
    void ChatOfferReconnect(string printerName,string driverName){
        AddBotBubble("Isso geralmente resolve desconectando e reconectando o cabo USB dela. Quando estiver pronto, desconecta e conecta de novo (pode ser na mesma porta ou em outra).");
        AddOptions(new ChatOpt("Pronto, pode esperar",CaiAccent,true,()=>{
            var before=GetLiveUsbPorts();
            AddBotBubble("Beleza, vou ficar de olho por ate 2 minutos. Pode desconectar e reconectar agora.");
            ThreadPool.QueueUserWorkItem(delegate(object st){
                string newPort=null;
                for(int i=0;i<40&&newPort==null;i++){
                    System.Threading.Thread.Sleep(3000);
                    var now=GetLiveUsbPorts();
                    foreach(var p in now) if(!before.Contains(p)){ newPort=p; break; }
                }
                if(newPort==null){
                    BeginInvoke((Action)(()=>{ AddBotBubble("Nao detectei nenhuma reconexao em 2 minutos. Confere se o cabo esta bem encaixado e me chama de novo quando quiser tentar outra vez."); AddOptions(ChatBack()); }));
                    return;
                }
                string np=newPort;
                BeginInvoke((Action)(()=>AddBotBubble("Reconectou na porta "+np+"! Testando e ajustando a fila \""+printerName+"\"...")));
                string ok=null; try{ ok=SafeRecreateQueue(printerName,new List<string>{np},driverName); }catch{}
                string okF=ok;
                SendTelemetry("reconectar_usb",okF!=null?"ok":"falhou",printerName);
                BeginInvoke((Action)(()=>{
                    AddBotBubble(okF!=null?"Prontinho! A fila \""+printerName+"\" foi ajustada pra porta nova e o teste de impressao saiu certinho.":"A porta reconectou, mas o teste de impressao nao confirmou que saiu papel — pode ser driver, ou a impressora sem papel/com a tampa aberta. Nao mexi na fila antiga pra nao te deixar sem impressora.");
                    AddOptions(ChatBack());
                }));
            });
        }));
    }

    // ── Exclusao do Windows Defender (portado do FudoPrintDoctor) ──────────────
    // Ferramentas de fabricante em NetConfigTools costumam ser .exe antigos e sem assinatura
    // digital — exatamente o perfil que o Defender mais barra. Em vez de deixar o antivirus
    // decidir depois de rodar, adiciona a exclusao ANTES, na pasta especifica da ferramenta
    // (nao a pasta toda do Delitools) e no processo dela.
    void ExcludeFromDefender(string folderPath,string exeFileName){
        try{ RunPS("Add-MpPreference -ExclusionPath '"+folderPath.Replace("'","''")+"' -ErrorAction SilentlyContinue"); }catch{}
        try{ RunPS("Add-MpPreference -ExclusionProcess '"+exeFileName.Replace("'","''")+"' -ErrorAction SilentlyContinue"); }catch{}
        UILog("Exclusao de antivirus adicionada: "+folderPath);
    }

    // ── Telemetria opcional (portado do FudoPrintDoctor) ───────────────────────
    // Silenciosa por completo: se nao tiver telemetria.txt do lado do exe, ou se a rede
    // falhar, nao acontece nada (nao avisa, nao atrapalha o fluxo). A URL nunca fica no
    // codigo/repositorio — vem de um arquivo local, exatamente pra evitar publicar um
    // endpoint de escrita no GitHub publico.
    string GetTelemetryUrl(){
        try{
            string path=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"telemetria.txt");
            if(File.Exists(path)){ string u=File.ReadAllText(path).Trim(); if(u.Length>0) return u; }
        }catch{}
        return null;
    }
    // Id anonimo e estavel por maquina (hash do MachineGuid do Windows) — da pra saber que
    // duas corridas vieram do mesmo PC sem saber de qual cliente/comercio e.
    string GetPcId(){
        string guid="";
        try{ using(var k=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography")){ if(k!=null) guid=(k.GetValue("MachineGuid") as string)??""; } }catch{}
        if(guid.Length==0) return "desconhecido";
        try{
            using(var sha=System.Security.Cryptography.SHA256.Create()){
                var hash=sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(guid));
                var sb=new System.Text.StringBuilder();
                for(int i=0;i<8;i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }catch{ return "desconhecido"; }
    }
    string JsonEsc(string s){ if(s==null) return ""; return s.Replace("\\","\\\\").Replace("\"","\\\"").Replace("\n"," ").Replace("\r",""); }
    void SendTelemetry(string acao,string resultado,string detalhe){
        string url=GetTelemetryUrl();
        if(string.IsNullOrEmpty(url)) return;
        ThreadPool.QueueUserWorkItem(delegate(object st){
            try{
                var sb=new System.Text.StringBuilder();
                sb.Append("{");
                sb.Append("\"schemaVersion\":\"1.0\",");
                sb.Append("\"pcId\":\""+JsonEsc(GetPcId())+"\",");
                sb.Append("\"timestamp\":\""+DateTime.UtcNow.ToString("o")+"\",");
                sb.Append("\"appVersion\":\""+JsonEsc(APP_VERSION)+"\",");
                sb.Append("\"so\":\""+JsonEsc(Environment.OSVersion.VersionString)+"\",");
                sb.Append("\"acao\":\""+JsonEsc(acao)+"\",");
                sb.Append("\"resultado\":\""+JsonEsc(resultado)+"\",");
                sb.Append("\"detalhe\":\""+JsonEsc(detalhe)+"\"");
                sb.Append("}");
                PostTelemetry(url,sb.ToString(),0);
            }catch{}
        });
    }
    // Apps Script (o receptor mais pratico, sem servidor proprio) responde /exec com um
    // redirect 302 pra script.googleusercontent.com. Se a gente so seguir o redirect padrao,
    // o POST vira GET no caminho e o corpo se perde — por isso segue manualmente, mantendo
    // o metodo POST, exatamente como o FudoPrintDoctor documentou precisar fazer.
    void PostTelemetry(string url,string jsonBody,int depth){
        if(depth>3||string.IsNullOrEmpty(url)) return;
        try{
            byte[] data=System.Text.Encoding.UTF8.GetBytes(jsonBody);
            var req=(System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
            req.Method="POST"; req.ContentType="application/json"; req.Timeout=6000; req.AllowAutoRedirect=false;
            using(var s=req.GetRequestStream()) s.Write(data,0,data.Length);
            using(var resp=(System.Net.HttpWebResponse)req.GetResponse()){
                int code=(int)resp.StatusCode;
                if(code>=300&&code<400){
                    string loc=resp.Headers["Location"];
                    if(!string.IsNullOrEmpty(loc)) PostTelemetry(loc,jsonBody,depth+1);
                }
            }
        }catch{}
    }

    string netToolsRoot { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"NetConfigTools"); } }

    List<string> GetNetToolBrands(){
        var list=new List<string>();
        try{ if(Directory.Exists(netToolsRoot)) foreach(var d in Directory.GetDirectories(netToolsRoot)) list.Add(Path.GetFileName(d)); }catch{}
        list.Sort();
        return list;
    }

    // ── Marcas: protocolo nativo vs ferramenta de terceiro (compartilhado entre o
    //    Assistente Guiado da tela Config IP e a tela de Chat) ─────────────────
    static readonly string[] NativeProtocolBrands=new string[]{"XPrinter","Epson","Elgin","Bematech"};
    static readonly HashSet<string> AutoConfigBrands=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"BIXOLON"};
    bool BrandIsNative(string b){ foreach(var n in NativeProtocolBrands) if(n.Equals(b,StringComparison.OrdinalIgnoreCase)) return true; return false; }
    string BrandToolFolder(string b){ foreach(var f in GetNetToolBrands()) if(f.Equals(b,StringComparison.OrdinalIgnoreCase)) return f; return null; }
    List<string> GetAllBrandNames(){
        var outp=new List<string>();
        foreach(var b in NativeProtocolBrands) outp.Add(b);
        foreach(var f in GetNetToolBrands()){ bool dup=false; foreach(var o in outp) if(o.Equals(f,StringComparison.OrdinalIgnoreCase)){dup=true;break;} if(!dup) outp.Add(f); }
        outp.Sort();
        return outp;
    }

    List<string> GetNetToolFiles(string brand){
        var list=new List<string>();
        try{
            string dir=Path.Combine(netToolsRoot,brand);
            if(Directory.Exists(dir)) foreach(var f in Directory.GetFiles(dir)){
                var ext=Path.GetExtension(f).ToLowerInvariant();
                if(ext==".exe"||ext==".msi") list.Add(Path.GetFileName(f));
            }
        }catch{}
        list.Sort();
        return list;
    }

    string GetLocalIp(string targetIp){
        // Use routing socket trick to find which local adapter reaches the target
        if(targetIp.Length>6){
            try{
                using(var sk=new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,System.Net.Sockets.SocketType.Dgram,System.Net.Sockets.ProtocolType.Udp)){
                    sk.Connect(targetIp,9100);
                    return ((System.Net.IPEndPoint)sk.LocalEndPoint).Address.ToString();
                }
            }catch{}
        }
        try{foreach(var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()){
            if(ni.OperationalStatus!=System.Net.NetworkInformation.OperationalStatus.Up||ni.NetworkInterfaceType==System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
            foreach(var ua in ni.GetIPProperties().UnicastAddresses){
                if(ua.Address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork){var s=ua.Address.ToString(); if(!s.StartsWith("127")) return s;}
            }
        }}catch{}
        return "0.0.0.0";
    }

    // Ve se algum adaptador local ja esta na mesma sub-rede /24 de targetIp. Se nenhum estiver
    // (impressora em outra faixa — comum quando ela ainda esta no IP de fabrica), devolve o nome
    // do adaptador Ethernet ativo mais provavel (prefere cabeado sobre Wi-Fi) pra usarmos um IP
    // temporario nele e conseguir falar com a impressora.
    string FindAdapterForSubnet(string targetIp,out bool alreadyReachable){
        alreadyReachable=false; string candidate=null;
        try{
            var targetBytes=System.Net.IPAddress.Parse(targetIp).GetAddressBytes();
            foreach(var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()){
                if(ni.OperationalStatus!=System.Net.NetworkInformation.OperationalStatus.Up||ni.NetworkInterfaceType==System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                bool hasIPv4=false;
                foreach(var ua in ni.GetIPProperties().UnicastAddresses){
                    if(ua.Address.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    hasIPv4=true;
                    var maskBytes=ua.IPv4Mask!=null?ua.IPv4Mask.GetAddressBytes():new byte[]{255,255,255,0};
                    var ipBytes=ua.Address.GetAddressBytes();
                    bool same=true;
                    for(int i=0;i<4;i++) if((ipBytes[i]&maskBytes[i])!=(targetBytes[i]&maskBytes[i])){same=false;break;}
                    if(same){ alreadyReachable=true; return ni.Name; }
                }
                if(hasIPv4&&(candidate==null||ni.NetworkInterfaceType==System.Net.NetworkInformation.NetworkInterfaceType.Ethernet)) candidate=ni.Name;
            }
        }catch{}
        return candidate;
    }

    // Escolhe um host livre na sub-rede /24 de targetIp para usar como IP temporario do PC.
    string PickTempIpInSubnet(string targetIp){
        var parts=targetIp.Split('.');
        string baseNet=parts[0]+"."+parts[1]+"."+parts[2]+".";
        for(int h=250;h>=240;h--){ string cand=baseNet+h; if(cand!=targetIp) return cand; }
        return baseNet+"239";
    }

    bool AddTempIp(string adapterName,string ip,string mask){
        try{
            var psi=new ProcessStartInfo("netsh","interface ip add address name=\""+adapterName+"\" addr="+ip+" mask="+mask){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
            using(var p=Process.Start(psi)){ p.WaitForExit(6000); return p.ExitCode==0; }
        }catch{ return false; }
    }

    void RemoveTempIp(string adapterName,string ip){
        try{
            var psi=new ProcessStartInfo("netsh","interface ip delete address name=\""+adapterName+"\" addr="+ip){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
            using(var p=Process.Start(psi)){ p.WaitForExit(6000); }
        }catch{}
    }

    string ApplyNetConfigXP(string localIp,string curIp,string newIp,string mask,string gw,bool dhcp){
        bool sentUdp=false;
        // Strategy 1: XPrinter V3.0C UDP protocol — port 3000
        // Packet: [00 06] [00 00] [localIP 4B] [newIP 4B] [mask 4B] [gw 4B] [dhcp 1B] [00 00 00]
        try{
            byte[] lB=System.Net.IPAddress.Parse(localIp.Length>6?localIp:"0.0.0.0").GetAddressBytes();
            byte[] nB=System.Net.IPAddress.Parse(dhcp?"0.0.0.0":newIp).GetAddressBytes();
            byte[] mB=System.Net.IPAddress.Parse(dhcp?"0.0.0.0":(mask.Length>6?mask:"255.255.255.0")).GetAddressBytes();
            byte[] gB=System.Net.IPAddress.Parse(gw.Length>6?gw:"0.0.0.0").GetAddressBytes();
            var pkt=new List<byte>{0x00,0x06,0x00,0x00};
            pkt.AddRange(lB); pkt.AddRange(nB); pkt.AddRange(mB); pkt.AddRange(gB);
            pkt.Add(dhcp?(byte)0x01:(byte)0x00); pkt.Add(0x00); pkt.Add(0x00); pkt.Add(0x00);
            using(var udp=new System.Net.Sockets.UdpClient()){
                udp.Send(pkt.ToArray(),pkt.Count,new System.Net.IPEndPoint(System.Net.IPAddress.Parse(curIp),3000));
                sentUdp=true;
            }
        }catch{}
        // Strategy 2: @eipca via TCP port 9100 (Epson TM-Net / Elgin / Bematech)
        string cfg=dhcp?"@eipca\r\nDHCP:1\r\n"
            :"@eipca\r\nIP:"+newIp+"\r\nSM:"+(mask.Length>6?mask:"255.255.255.0")+"\r\nGW:"+(gw.Length>6?gw:"0.0.0.0")+"\r\nDHCP:0\r\n";
        byte[] data=System.Text.Encoding.ASCII.GetBytes(cfg);
        bool sentTcp=false;
        try{
            using(var sk=new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,System.Net.Sockets.SocketType.Stream,System.Net.Sockets.ProtocolType.Tcp)){
                var ar2=sk.BeginConnect(curIp,9100,null,null);
                if(ar2.AsyncWaitHandle.WaitOne(4000)){sk.EndConnect(ar2); sk.Send(data); sentTcp=true;}
            }
        }catch{}
        if(sentUdp||sentTcp){
            string via=(sentUdp&&sentTcp?"UDP+TCP":sentUdp?"UDP (XPrinter)":"TCP (Epson)");
            return "OK - Comando enviado via "+via+" para "+curIp+".\n"+
                   "Isso NAO confirma que a impressora aplicou a mudanca — o protocolo @eipca/UDP so e"+
                   " entendido por XPrinter, Epson TM-Net, Elgin e Bematech (e clones com a mesma placa de rede)."+
                   " Reinicie a impressora e confira o IP novo (autoteste ou pagina web dela); se nao mudar,"+
                   " essa marca/modelo usa outro protocolo e nao e suportado por aqui.";
        }
        return "Erro: nao foi possivel conectar em "+curIp+".\nVerifique o cabo e confirme o IP da impressora.";
    }

    string ApplyNetConfigUsb(string printerName,string newIp,string mask,string gw,bool dhcp){
        string cfg=dhcp?"@eipca\r\nDHCP:1\r\n"
            :"@eipca\r\nIP:"+newIp+"\r\nSM:"+mask+"\r\nGW:"+(gw.Length>6?gw:"0.0.0.0")+"\r\nDHCP:0\r\n";
        bool ok=SendRawBytes(printerName,System.Text.Encoding.ASCII.GetBytes(cfg));
        if(ok) return "OK - Comando enviado via USB para \""+printerName+"\".\n"+
                      "Isso so confirma que o Windows aceitou o envio, NAO que a impressora reconheceu o comando —"+
                      " o protocolo @eipca so funciona em XPrinter, Epson TM-Net, Elgin e Bematech (e clones da mesma placa)."+
                      " Reinicie a impressora e confira o IP novo; se nao mudar, essa marca/modelo nao e suportado por aqui.";
        return "Nao foi possivel enviar via USB.\nVerifique se a impressora esta ligada, no modo de configuracao e instalada no Windows.";
    }

    void RefreshPrinterList(int pageIdx){
        var lst=pageIdx==1?lstInstalled:lstTest; if(lst==null) return;
        lst.Items.Clear();
        try{ foreach(ManagementObject o in new ManagementObjectSearcher("SELECT * FROM Win32_Printer").Get()){
            var n=o["Name"]!=null?o["Name"].ToString():"";
            ushort st=0; if(o["PrinterStatus"]!=null) try{st=Convert.ToUInt16(o["PrinterStatus"]);}catch{}
            bool def=o["Default"]!=null&&(bool)o["Default"];
            lst.Items.Add(n+"  |  "+(st==3?"Online":"Offline")+(def?" [PADRAO]":""));
        }}catch{}
    }

    void RefreshToolsPage(){
        if(lblToolsSpooler==null) return;
        var st=GetSpoolerStatus(); var q=GetQueueCount();
        lblToolsSpooler.Text="Spooler: "+st; lblToolsSpooler.ForeColor=st=="Executando"?Cacc:Cerr;
        lblToolsQueue.Text="Fila: "+q+" documento(s)";
    }

    // ════════════════════════════════════════════════════════
    //  BUSINESS LOGIC
    // ════════════════════════════════════════════════════════
    struct DetRes { public string Name,DevId,Model; }

    DetRes? DoDetect(){
        try{ foreach(ManagementObject o in new ManagementObjectSearcher("SELECT * FROM Win32_PnPEntity").Get()){
            var d=o["DeviceID"]!=null?o["DeviceID"].ToString():"";
            var m=Regex.Match(d,@"USB\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})",RegexOptions.IgnoreCase);
            if(!m.Success) continue;
            var vid=m.Groups[1].Value.ToUpper(); var pid=m.Groups[2].Value.ToUpper();
            var vp=vid+":"+pid; var nm=o["Name"]!=null?o["Name"].ToString():"Impressora USB";
            if(VidPidMap.ContainsKey(vp)) return new DetRes{Name=nm,DevId="USB\\VID_"+vid+"&PID_"+pid,Model=VidPidMap[vp]};
        }}catch{}
        return null;
    }

    void ApplyDetect(DetRes? r){
        if(r==null){pnlDetected.Visible=false; lblNoDetect.Visible=true; return;}
        var v=r.Value; pnlDetected.Visible=true; lblNoDetect.Visible=false;
        lblDetName.Text=v.Name; lblDetVid.Text=v.DevId;
        if(v.Model!=null&&txtPrinterName!=null&&txtPrinterName.Text.Trim().Length==0) txtPrinterName.Text=v.Model;
        Log("USB detectada: "+v.Name+" ("+v.DevId+")");
    }

    void OnInstall(object s,EventArgs e){
        string customName=txtPrinterName!=null?txtPrinterName.Text.Trim():"";
        if(customName.Length==0){MessageBox.Show("Digite um nome para a impressora.","Atencao",MessageBoxButtons.OK,MessageBoxIcon.Warning); return;}
        bool setDef=chkSetDefault!=null&&chkSetDefault.Checked;
        string ip=txtIpAddress!=null?txtIpAddress.Text.Trim():"";
        string portNum=txtPortNum!=null?txtPortNum.Text.Trim():"9100";
        bool usb=connUsb;
        btnInstall.Enabled=false; btnInstall.Text="Instalando..."; SetStep(1);
        Log("=== Instalando: "+customName+" ===");
        Log("Conexao: "+(usb?"USB":"Rede TCP/IP "+ip)+" (driver generico)");
        ThreadPool.QueueUserWorkItem(delegate(object state){
            bool ok; string fatalErr=null;
            try{
                if(!usb&&ip.Length>0){
                    ok=RunNetworkInstall(ip,portNum.Length>0?portNum:"9100",customName,setDef);
                } else {
                    CreatePrinter(customName,setDef);
                    ok=PrinterExists(customName); // CreatePrinter e void — so confirmando que a fila existe de verdade
                }
            } catch(Exception ex) {
                ok=false; fatalErr=ex.GetType().Name+": "+ex.Message;
            }
            bool okF=ok; string errF=fatalErr;
            BeginInvoke((Action)(()=>{
                btnInstall.Enabled=true; btnInstall.Text="Instalar Impressora";
                if(errF!=null){ SetStep(0); Log("=== ERRO: "+errF+" ==="); MessageBox.Show("Erro durante a instalacao:\n\n"+errF,"Erro",MessageBoxButtons.OK,MessageBoxIcon.Error); }
                else if(okF){SetStep(2); Log("=== CONCLUIDO! ==="); MessageBox.Show("Concluido!\n\n"+customName,"Sucesso",MessageBoxButtons.OK,MessageBoxIcon.Information);}
                else  {SetStep(0); Log("=== FALHOU ==="); MessageBox.Show("Falha. Verifique o log.","Erro",MessageBoxButtons.OK,MessageBoxIcon.Error);}
                RefreshStatus(); RefreshDrawerPrinters();
            }));
        });
    }

    void CreatePrinter(string customName,bool setDefault){
        UILog("--- Criando impressora ---");
        System.Threading.Thread.Sleep(1500);
        // Snapshot printers AND ports that exist before we touch anything
        var beforePrinters=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var beforePorts=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try{foreach(ManagementObject o in new ManagementObjectSearcher("SELECT * FROM Win32_Printer").Get()){var n=o["Name"]!=null?o["Name"].ToString():""; if(n.Length>0)beforePrinters.Add(n);}}catch(Exception ex){UILog("Aviso ao listar impressoras: "+ex.Message);}
        try{foreach(ManagementObject o in new ManagementObjectSearcher("SELECT * FROM Win32_PrinterPort").Get()){var n=o["Name"]!=null?o["Name"].ToString():""; if(n.StartsWith("USB",StringComparison.OrdinalIgnoreCase))beforePorts.Add(n);}}catch(Exception ex){UILog("Aviso ao listar portas: "+ex.Message);}
        // Remove existing with same custom name so we can recreate
        if(beforePrinters.Contains(customName)){
            DialogResult dr2=DialogResult.Cancel;
            Invoke((Action)(()=>{ dr2=MessageBox.Show("Ja existe uma impressora chamada \""+customName+"\".\n\nRemover e recriar?","Confirmar",MessageBoxButtons.YesNo,MessageBoxIcon.Warning); }));
            if(dr2!=DialogResult.Yes){ UILog("Instalacao cancelada: ja existe \""+customName+"\"."); return; }
            UILog("Removendo instalacao anterior: "+customName);
            RunPS("Remove-Printer -Name '"+customName.Replace("'","")+"'");
            beforePrinters.Remove(customName);
            System.Threading.Thread.Sleep(800);
        }
        // Driver generico
        var drivers=GetAllDriverNames();
        string driverName=FindGenericDriver(drivers);
        if(driverName==null){UILog("Nenhum driver disponivel."); return;}
        UILog("Driver: "+driverName);
        string baseDrv=DriverBaseName(driverName);
        UILog("Driver a usar: "+baseDrv);
        // Force Windows to reprocess already-connected USB devices waiting for a driver
        try{var p=Process.Start(new ProcessStartInfo("pnputil.exe","/scan-devices"){UseShellExecute=false,CreateNoWindow=true}); if(p!=null)p.WaitForExit();}catch{}
        System.Threading.Thread.Sleep(1500);
        // === Strategy 1: find the correct USB port ===
        // FindUsbPort(beforePorts) uses registry to map device→port, and excludes ports that
        // existed before installation (beforePorts), correctly handling multiple USB ports
        string portName=FindUsbPort(beforePorts);
        if(portName==null){
            for(int att=0;att<4&&portName==null;att++){
                UILog("Aguardando porta USB... ("+(att+1)+"/4)"); System.Threading.Thread.Sleep(3000);
                portName=FindUsbPort(beforePorts);
            }
        }
        if(portName!=null){
            UILog("Porta USB identificada: "+portName);
            string e1=RunPS("Add-Printer -Name '"+customName.Replace("'","")+"' -DriverName '"+baseDrv.Replace("'","")+"' -PortName '"+portName+"'");
            if(e1.Trim().Length==0){UILog("Impressora criada: "+customName); if(setDefault)SetDefaultPrinter(customName); return;}
            UILog("Add-Printer falhou: "+e1.Trim());
        }
        // === Strategy 2: Windows auto-created the printer via PnP — find and rename it ===
        UILog("Aguardando auto-deteccao do Windows (ate 24s)...");
        for(int att=0;att<8;att++){
            System.Threading.Thread.Sleep(3000);
            UILog("Verificando... ("+(att+1)+"/8)");
            foreach(ManagementObject o in new ManagementObjectSearcher("SELECT * FROM Win32_Printer").Get()){
                var n=o["Name"]!=null?o["Name"].ToString():"";
                var pn=o["PortName"]!=null?o["PortName"].ToString():"";
                if(n.Length>0&&!beforePrinters.Contains(n)&&pn.StartsWith("USB",StringComparison.OrdinalIgnoreCase)){
                    UILog("Impressora auto-detectada: "+n+" ("+pn+")");
                    if(!string.Equals(n,customName,StringComparison.OrdinalIgnoreCase)){
                        UILog("Renomeando para: "+customName);
                        string re=RunPS("Rename-Printer -Name '"+n.Replace("'","")+"' -NewName '"+customName.Replace("'","")+"'");
                        if(re.Length>0) UILog("Aviso: "+re.Trim());
                    }
                    if(setDefault)SetDefaultPrinter(customName);
                    UILog("Impressora instalada: "+customName); return;
                }
            }
            // Retry Strategy 1 without excluding beforePorts (last resort)
            if(portName==null){
                portName=FindUsbPort(null);
                if(portName!=null){
                    string e2=RunPS("Add-Printer -Name '"+customName.Replace("'","")+"' -DriverName '"+baseDrv.Replace("'","")+"' -PortName '"+portName+"'");
                    if(e2.Trim().Length==0){UILog("Impressora criada: "+customName); if(setDefault)SetDefaultPrinter(customName); return;}
                    UILog("Erro porta "+portName+": "+e2.Trim()); portName=null;
                }
            }
        }
        UILog("ERRO: Impressora nao detectada apos 24s.");
        UILog("Verifique: impressora LIGADA, cabo USB encaixado.");
    }

    bool RunNetworkInstall(string ip,string portNum,string name,bool setDefault){
        UILog("--- Instalando impressora de rede ---");
        UILog("Verificando "+ip+"...");
        bool pong=PingAddress(ip);
        UILog(pong?"Ping OK.":"Aviso: sem resposta ao ping. Continuando...");
        string portName="IP_"+ip;
        string portErr=RunPS("Add-PrinterPort -Name '"+portName+"' -PrinterHostAddress '"+ip+"'");
        if(portErr.Length>0&&portErr.IndexOf("already",StringComparison.OrdinalIgnoreCase)<0){
            UILog("Tentando criar porta via WMI...");
            try{
                var mc=new ManagementClass("Win32_TCPIPPrinterPort");
                var inst=mc.CreateInstance();
                inst["Name"]=portName; inst["HostAddress"]=ip;
                int pn=9100; int.TryParse(portNum,out pn); inst["PortNumber"]=pn; inst["Protocol"]=1;
                inst.Put(); UILog("Porta criada via WMI.");
            }catch(Exception ex2){UILog("Erro porta: "+ex2.Message); return false;}
        }else{UILog("Porta: OK");}
        var drivers=GetAllDriverNames();
        string driverName=FindGenericDriver(drivers);
        if(driverName==null){UILog("Nenhum driver disponivel."); return false;}
        UILog("Driver: "+driverName);
        string err=RunPS("Add-Printer -Name '"+name.Replace("'","")+"' -DriverName '"+DriverBaseName(driverName).Replace("'","")+"' -PortName '"+portName+"'");
        if(err.Length==0){UILog("Impressora de rede criada: "+name); if(setDefault)SetDefaultPrinter(name); return true;}
        UILog("Erro: "+err.Trim()); return false;
    }

    static readonly string[] GenericDriverExclude=new string[]{"Universal Print Class","Microsoft Print","XPS","OneNote","Fax","Remote Desktop","Send To"};
    bool IsUsableDriver(string d){ foreach(var ex in GenericDriverExclude) if(d.IndexOf(ex,StringComparison.OrdinalIgnoreCase)>=0) return false; return true; }
    string FindGenericDriver(List<string> drivers){
        string[] preferred=new string[]{"Generic / Text Only","Generic IBM Graphics 9pin","Generic ESC/POS"};
        foreach(var pref in preferred)
            foreach(var d in drivers)
                if(d.IndexOf(pref,StringComparison.OrdinalIgnoreCase)>=0&&IsUsableDriver(d)) return d;
        UILog("Instalando driver 'Generic / Text Only'...");
        RunPS("Add-PrinterDriver -Name 'Generic / Text Only'");
        return "Generic / Text Only";
    }

    static string DriverBaseName(string n){ int c=n.IndexOf(','); return c>0?n.Substring(0,c).Trim():n; }

    List<string> GetAllDriverNames(){
        var list=new List<string>();
        try{foreach(ManagementObject o in new ManagementObjectSearcher("SELECT * FROM Win32_PrinterDriver").Get()){var n=o["Name"]!=null?o["Name"].ToString():""; if(n.Length>0)list.Add(n);}}catch{}
        return list;
    }

    // Le HKLM\SYSTEM\CurrentControlSet\Enum\USBPRINT — o Windows preenche essa chave assim que
    // reconhece um dispositivo USB de classe Impressora, mesmo sem driver/fila instalada ainda.
    // Retorna [portName, nomeAmigavel] para cada dispositivo encontrado.
    List<string[]> GetUsbPrintRegistryPorts(){
        var list=new List<string[]>();
        try{
            using(var root=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USBPRINT")){
                if(root!=null) foreach(var devClass in root.GetSubKeyNames()){
                    using(var ck=root.OpenSubKey(devClass)){ if(ck==null) continue;
                        foreach(var inst in ck.GetSubKeyNames()){
                            using(var ik=ck.OpenSubKey(inst)){
                                if(ik==null) continue;
                                string friendly=ik.GetValue("FriendlyName") as string ?? ik.GetValue("DeviceDesc") as string ?? devClass;
                                int semi=friendly.LastIndexOf(';'); if(semi>=0) friendly=friendly.Substring(semi+1);
                                using(var dp=ik.OpenSubKey("Device Parameters")){
                                    if(dp==null) continue;
                                    var pv=dp.GetValue("PortName") as string;
                                    if(pv!=null&&pv.Length>0) list.Add(new string[]{pv,friendly});
                                }
                            }
                        }
                    }
                }
            }
        }catch{}
        return list;
    }

    // Returns the best USB port for a new printer install.
    // Priority: 1) registry (exact device→port mapping)  2) free WMI port  3) PS fallback  4) any USB port
    string FindUsbPort(){ return FindUsbPort(null); }
    string FindUsbPort(HashSet<string> excludePorts){
        // Build set of ports already used by installed printers
        var usedByPrinter=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try{foreach(ManagementObject o in new ManagementObjectSearcher("SELECT * FROM Win32_Printer").Get()){
            var pn=o["PortName"]!=null?o["PortName"].ToString():""; if(pn.Length>0)usedByPrinter.Add(pn);
        }}catch{}
        // --- Method 1: Registry USBPRINT device entries ---
        // Windows stores the device→port mapping at:
        // HKLM\SYSTEM\CurrentControlSet\Enum\USBPRINT\{DevClass}\{Instance}\Device Parameters → PortName
        string regPort=null;
        try{
            using(var root=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USBPRINT")){
                if(root!=null) foreach(var devClass in root.GetSubKeyNames()){
                    using(var ck=root.OpenSubKey(devClass)){if(ck==null)continue;
                        foreach(var inst in ck.GetSubKeyNames()){
                            using(var dp=ck.OpenSubKey(inst+@"\Device Parameters")){
                                if(dp==null)continue;
                                var pv=dp.GetValue("PortName") as string ?? dp.GetValue("Port Number") as string;
                                if(pv!=null&&pv.StartsWith("USB",StringComparison.OrdinalIgnoreCase)){
                                    bool excluded=excludePorts!=null&&excludePorts.Contains(pv);
                                    bool free=!usedByPrinter.Contains(pv);
                                    if(free&&!excluded){UILog("Porta via registro: "+pv+" (dispositivo: "+devClass+")"); return pv;}
                                    if(regPort==null) regPort=pv; // keep as fallback
                                }
                            }
                        }
                    }
                }
            }
        }catch{}
        // --- Method 2: WMI Win32_PrinterPort — prefer free ports ---
        var all=new List<string>();
        try{foreach(ManagementObject o in new ManagementObjectSearcher("SELECT * FROM Win32_PrinterPort").Get()){
            var n=o["Name"]!=null?o["Name"].ToString():""; if(n.StartsWith("USB",StringComparison.OrdinalIgnoreCase))all.Add(n);
        }}catch{}
        all.Sort();
        foreach(var p in all){bool excluded=excludePorts!=null&&excludePorts.Contains(p); if(!usedByPrinter.Contains(p)&&!excluded)return p;}
        // --- Method 3: PowerShell fallback ---
        try{
            string psOut=RunPSOut("Get-PrinterPort|Where-Object{$_.Name -like 'USB*'}|Sort-Object Name|ForEach-Object{$_.Name}");
            foreach(var line in psOut.Split(new char[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries)){
                var t=line.Trim();
                if(t.StartsWith("USB",StringComparison.OrdinalIgnoreCase)){bool excl=excludePorts!=null&&excludePorts.Contains(t); if(!usedByPrinter.Contains(t)&&!excl)return t;}
            }
        }catch{}
        // --- Method 4: last resort — return any USB port (registry or WMI), even if in use ---
        if(regPort!=null) return regPort;
        if(all.Count>0) return all[0];
        return null;
    }

    void SetDefaultPrinter(string name){
        try{
            foreach(ManagementObject o in new ManagementObjectSearcher("SELECT * FROM Win32_Printer WHERE Name='"+name.Replace("'","")+"'").Get()){
                o.InvokeMethod("SetDefaultPrinter",null); UILog("Padrao: "+name); return;
            }
        }catch(Exception ex){UILog("Erro padrao: "+ex.Message);}
    }

    bool PingAddress(string ip){
        try{return new Ping().Send(ip,1500).Status==IPStatus.Success;}catch{return false;}
    }

    bool TestTcpPort(string ip,int port){
        try{using(var c=new System.Net.Sockets.TcpClient()){var r=c.BeginConnect(ip,port,null,null); bool ok=r.AsyncWaitHandle.WaitOne(1500); if(!ok){c.EndConnect(r);} return ok&&c.Connected;}}catch{return false;}
    }

    bool SendRawBytes(string printerName,byte[] data){
        IntPtr h; if(!OpenPrinter(printerName,out h,IntPtr.Zero)) return false;
        var di=new DOCINFOA{pDocName="Raw",pOutputFile=null,pDataType="RAW"};
        if(StartDocPrinter(h,1,ref di)<=0){ClosePrinter(h); return false;}
        StartPagePrinter(h);
        IntPtr buf=Marshal.AllocCoTaskMem(data.Length); Marshal.Copy(data,0,buf,data.Length);
        int w; WritePrinter(h,buf,data.Length,out w); Marshal.FreeCoTaskMem(buf);
        EndPagePrinter(h); EndDocPrinter(h); ClosePrinter(h);
        return w==data.Length;
    }

    string BackupPrinters(){
        var lines=new List<string>();
        lines.Add("# Delitools Backup - "+DateTime.Now.ToString("dd/MM/yyyy HH:mm"));
        try{
            foreach(ManagementObject o in new ManagementObjectSearcher("SELECT * FROM Win32_Printer").Get()){
                string n=o["Name"]!=null?o["Name"].ToString():"";
                string dr=o["DriverName"]!=null?o["DriverName"].ToString():"";
                string pt=o["PortName"]!=null?o["PortName"].ToString():"";
                bool def=o["Default"]!=null&&(bool)o["Default"];
                if(n.Length>0)lines.Add(n+"|"+dr+"|"+pt+"|"+def);
            }
            string file=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"PrinterBackup.txt");
            File.WriteAllLines(file,lines.ToArray());
            return file;
        }catch(Exception ex){UILog("Erro no backup: "+ex.Message); return "(falhou: "+ex.Message+")";}
    }

    void RestorePrinters(string file){
        try{
            if(!File.Exists(file)){UILog("Arquivo nao encontrado: "+file); return;}
            foreach(var line in File.ReadAllLines(file)){
                if(line.StartsWith("#")||line.Trim().Length==0) continue;
                var parts=line.Split('|');
                if(parts.Length<3) continue;
                string n=parts[0],dr=parts[1],pt=parts[2];
                bool def=parts.Length>3&&parts[3]=="True";
                UILog("Restaurando: "+n);
                string err=RunPS("Add-Printer -Name '"+n.Replace("'","")+"' -DriverName '"+DriverBaseName(dr).Replace("'","")+"' -PortName '"+pt.Replace("'","")+"'");
                UILog(err.Length==0?"  OK":"  Erro: "+err.Trim());
                if(def&&err.Length==0) SetDefaultPrinter(n);
            }
        }catch(Exception ex){UILog("Erro ao restaurar: "+ex.Message);}
    }

    void OnTestPage(){ var pp=GetPrinters(); if(pp.Length==0){MessageBox.Show("Nenhuma impressora.","Atencao",MessageBoxButtons.OK,MessageBoxIcon.Warning); return;} DoTestPage(pp[0]); }
    void OnOpenProps(){ var pp=GetPrinters(); if(pp.Length>0)try{Process.Start("rundll32.exe","printui.dll,PrintUIEntry /p /n \""+pp[0]+"\"");}catch(Exception ex){Log("Erro: "+ex.Message);} }
    void OnRemove(){ var pp=GetPrinters(); if(pp.Length==0)return; if(MessageBox.Show("Remover: "+pp[0]+"?","Confirmar",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)==DialogResult.Yes) OnRemoveName(pp[0]); }
    void OnRemoveName(string name){
        try{
            ManagementObject obj=null;
            foreach(ManagementObject mo in new ManagementObjectSearcher("SELECT * FROM Win32_Printer WHERE Name='"+name.Replace("'","")+"'").Get()){obj=mo;break;}
            if(obj!=null)obj.Delete(); Log("Removida: "+name); BeginInvoke((Action)(()=>RefreshPrinterList(1)));
        }catch(Exception ex){Log("Erro: "+ex.Message);}
    }

    void DoTestPage(string name){
        try{
            ManagementObject obj=null;
            foreach(ManagementObject mo in new ManagementObjectSearcher("SELECT * FROM Win32_Printer WHERE Name='"+name.Replace("'","")+"'").Get()){obj=mo;break;}
            if(obj!=null)obj.InvokeMethod("PrintTestPage",null,null);
            Log(obj!=null?"Pagina de teste enviada!":"Impressora nao encontrada.");
        }catch(Exception ex){Log("Erro: "+ex.Message);}
    }

    // Confirma que o teste realmente SAIU da fila, nao so que o Windows aceitou o pedido —
    // PrintTestPage sem erro so significa que o spooler recebeu (portado do FudoPrintDoctor:
    // WritePrinter/PrintTestPage OK != o papel saiu). Chamar de uma thread de fundo, pois
    // faz ate 8s de polling. Retorna false se o trabalho ainda estava preso na fila no fim.
    bool DoTestPageVerified(string name){
        int before=CountJobsFor(name);
        DoTestPage(name);
        int elapsed=0;
        while(elapsed<8000){
            System.Threading.Thread.Sleep(500); elapsed+=500;
            int now=CountJobsFor(name);
            if(now<=before) return true;
        }
        return false;
    }
    int CountJobsFor(string printerName){
        try{ return new ManagementObjectSearcher("SELECT Name FROM Win32_PrintJob WHERE Name LIKE '"+printerName.Replace("'","''")+",%'").Get().Count; }catch{ return 0; }
    }

    void RestartSpooler(bool clear){
        try{using(var svc=new ServiceController("Spooler")){
            if(svc.Status!=ServiceControllerStatus.Stopped){svc.Stop(); svc.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(10));}
            if(clear){var dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"spool","PRINTERS"); foreach(var f in Directory.GetFiles(dir))try{File.Delete(f);}catch{}}
            svc.Start(); svc.WaitForStatus(ServiceControllerStatus.Running,TimeSpan.FromSeconds(10));
        }}catch(Exception ex){UILog("Erro Spooler: "+ex.Message);}
    }

    string GetSpoolerStatus(){try{using(var s=new ServiceController("Spooler"))return s.Status==ServiceControllerStatus.Running?"Executando":"Parado";}catch{return"?";}}
    int    GetQueueCount(){try{return new ManagementObjectSearcher("SELECT * FROM Win32_PrintJob").Get().Count;}catch{return 0;}}
    string[] GetPrinters(){
        var list=new List<string>();
        try{foreach(ManagementObject o in new ManagementObjectSearcher("SELECT * FROM Win32_Printer").Get()) if(o["Name"]!=null){var n=o["Name"].ToString(); if(n!="")list.Add(n);}}catch{}
        return list.ToArray();
    }
    bool PrinterExists(string name){ foreach(var p in GetPrinters()) if(p.Equals(name,StringComparison.OrdinalIgnoreCase)) return true; return false; }

    void RefreshStatus(){
        if(lblSpoolerDot==null) return;
        var st=GetSpoolerStatus(); var q=GetQueueCount();
        lblSpoolerDot.ForeColor=st=="Executando"?Cacc:Cerr;
        lblSpoolerTxt.Text="Spooler: "+st;
        lblQueueTxt.Text="Fila: "+q+" documento(s)";
    }

    string RunPS(string cmd){ string err=""; RunPSBoth(cmd,out err); return err; }
    string RunPSOut(string cmd){ string err="",stdout=""; RunPSBoth(cmd,out err,out stdout); return stdout.Trim(); }
    void RunPSBoth(string cmd,out string stderr){ string o=""; RunPSBoth(cmd,out stderr,out o); }
    void RunPSBoth(string cmd,out string stderr,out string stdout){
        stderr=""; stdout="";
        try{
            var psi=new ProcessStartInfo("powershell.exe","-NonInteractive -NoProfile -Command \""+cmd.Replace("\"","\\\"")+"\""){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
            using(var pr=Process.Start(psi)){
                string e="",o="";
                // Read both streams on separate threads to avoid deadlock
                var tErr=new Thread(()=>{try{e=pr.StandardError.ReadToEnd();}catch{}});
                var tOut=new Thread(()=>{try{o=pr.StandardOutput.ReadToEnd();}catch{}});
                tErr.Start(); tOut.Start();
                pr.WaitForExit(); tErr.Join(); tOut.Join();
                stderr=e; stdout=o;
            }
        }catch(Exception ex){stderr=ex.Message;}
    }

    void SetStep(int done){ for(int i=0;i<3;i++) stepCircles[i].BackColor=i<=done?Cacc:Color.FromArgb(200,205,215); }
    static readonly string LogFile=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Delitools","delitools.log");
    static readonly object logFileLock=new object();
    static void WriteLogFile(string line){
        lock(logFileLock){
            try{
                var dir=Path.GetDirectoryName(LogFile);
                if(!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                if(File.Exists(LogFile)&&new FileInfo(LogFile).Length>5*1024*1024) File.Delete(LogFile);
                File.AppendAllText(LogFile,"["+DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss")+"]  "+line+Environment.NewLine);
            }catch{ /* logging nunca deve derrubar o app */ }
        }
    }
    void UILog(string msg){ if(InvokeRequired)BeginInvoke((Action)(()=>Log(msg))); else Log(msg); }
    void Log(string msg){
        WriteLogFile(msg);
        if(logBox==null) return;
        if(logBox.InvokeRequired){logBox.BeginInvoke((Action)(()=>Log(msg))); return;}
        logBox.SelectionStart=logBox.TextLength; logBox.SelectionColor=Csub;
        logBox.AppendText("["+DateTime.Now.ToString("HH:mm:ss")+"]   "+msg+"\n"); logBox.ScrollToCaret();
    }

    static Label Lbl(string t,Font f,Color c,Point p,Size s){return new Label{Text=t,Font=f,ForeColor=c,Location=p,Size=s,AutoSize=false};}
    Button Btn(string t,Point p,Size s,Color bg){var b=new Button{Text=t,Font=new Font("Segoe UI",9,FontStyle.Bold),ForeColor=Color.White,BackColor=bg,FlatStyle=FlatStyle.Flat,Location=p,Size=s,Cursor=Cursors.Hand}; b.FlatAppearance.BorderSize=0; return b;}
    Panel  Card(int x,int y,int w,int h){return new Panel{Location=new Point(x,y),Size=new Size(w,h),BackColor=Ccard,BorderStyle=BorderStyle.FixedSingle};}
    void   CardHdr(Panel c,string t){c.Controls.Add(Lbl(t,new Font("Segoe UI",9,FontStyle.Bold),Ctxt,new Point(10,10),new Size(c.Width-20,20))); c.Controls.Add(new Panel{Location=new Point(0,32),Size=new Size(c.Width,1),BackColor=Cbord});}
    void   PageHeader(Panel pg,string t,string sub){pg.Controls.Add(Lbl(t,new Font("Segoe UI",16,FontStyle.Bold),Ctxt,new Point(CM,18),new Size(900,32))); pg.Controls.Add(Lbl(sub,new Font("Segoe UI",9),Csub,new Point(CM,54),new Size(900,18))); pg.Controls.Add(new Panel{Location=new Point(0,78),Size=new Size(CW,1),BackColor=Cbord});}
}
