# TiaTracker

App per Windows che tiene in ordine le versioni dei progetti Siemens TIA Portal di ogni
commessa PLC.

Lavorando su un impianto si accumulano copie del progetto (`..._V0.68`, `..._V0.69`,
`.rar`, backup) e diventa difficile ricordare quale versione e' sul PLC, su quale si sta
lavorando e a che punto e' ogni modifica su ciascuna. TiaTracker legge le cartelle della
commessa e lo mostra in un posto solo:

- **Versioni e stati**: trova le versioni nelle cartelle, segna quella In lavoro, quelle
  caricate su ogni PLC, le vecchie e le scartate; si accorge se una versione e' aperta in TIA.
- **Snapshot da TIA**: con TIA Openness, in sola lettura, esporta blocchi, hardware, rete
  e tabelle tag, dal TIA aperto o da una copia della cartella.
- **Confronto e modifiche**: confronta due versioni blocco per blocco e incrocia le
  differenze con il registro delle modifiche (cosa e' stato toccato, su quale versione,
  se importato, compilato o salvato).
- **Documenti**: Lista IP, Lista hardware, Lista IO, registro modifiche e piano attivita'
  in DOCX, PDF e CSV; la Lista IP si allinea anche a una scansione PRONETA.
- **Pianificazione**: attivita' e milestone su un Gantt, con una dashboard per commessa.

Requisiti: Windows 10/11 a 64 bit; per gli snapshot TIA Portal V21 (o V18) con TIA
Openness e l'utente nel gruppo locale "Siemens TIA Openness". Per compilare serve
l'SDK .NET 10; i worker si compilano solo dove sono installate le DLL di Openness, che
non sono incluse. Istruzioni per l'utente in `tools\LEGGIMI.txt` (finisce nel pacchetto).

## Struttura

| Progetto | Target | Cosa |
|---|---|---|
| `src\TiaTracker.Contracts` | netstandard2.0 | protocollo worker (JSON lines), codici di uscita, export hardware, manifest |
| `src\TiaTracker.Core` | net10.0 | dominio, stati e carichi, scanner, canonicalizzatore XML, confronto, riconciliazione, import storico, hardware/IO/IP, PRONETA, documenti, pianificazione (scheduler, calendario), TiaTrackerOut |
| `src\TiaTracker.Data` | net10.0 | SQLite (migrazioni incorporate `Migrations\001...006`), repository, CAS Brotli, storico pianificazione, attivita' |
| `src\TiaTracker.Export` | net10.0 | documenti neutri in DOCX (OpenXml 3.5), PDF (PDFsharp-MigraDoc 6.2, build Core con font di Windows) e CSV |
| `src\TiaTracker.App` | net10.0-windows | WPF tema scuro Fluent, MVVM Toolkit, Gantt disegnato in OnRender, WorkerRunner, servizi |
| `src\TiaTracker.Worker.Common` | (solo .cs) | worker Openness in sola lettura: hello, instances, probe, snapshot, hardware |
| `src\TiaTracker.Worker.V21` / `.V18` | net48 x64 | stessi sorgenti, `#if TIA_V21 / TIA_V18`, DLL Siemens risolte a runtime |
| `tests\TiaTracker.Core.Tests` | xunit | test su dati sintetici; quelli sui dati reali di una commessa stanno in `Locali\`, esclusa da git |

Le schede della commessa: Dashboard, Modifiche, Matrice, Documenti, Pianificazione,
Rete / IP, Impostazioni. Il DB si usa solo dal thread UI (controllo in `Db`, eccezione in
Debug); i lavori pesanti in `Task.Run` restituiscono dati. Un solo lavoro con TIA alla
volta (`JobRunner`).

## Comandi

```
dotnet build TiaTracker.slnx
dotnet test tests\TiaTracker.Core.Tests\TiaTracker.Core.Tests.csproj
powershell -ExecutionPolicy Bypass -File tools\publish.ps1 [-NoZip]
python tools\make-icon.py [cartella anteprima]
```

`make-icon.py` (Pillow) rigenera l'icona `src\TiaTracker.App\Assets\TiaTracker.ico` (exe e
finestre) e `TiaTracker.png` (logo nella barra dell'app).

`publish.ps1` produce `dist\TiaTracker` (app self-contained + `worker\v21`, `worker\v18`)
e lo zip; si ferma se manca il worker V21 e prova `hello`. Ogni build dell'app copia i
worker in `bin\<cfg>\net10.0-windows\worker\vNN` (se le DLL Openness ci sono); in
sviluppo l'app li cerca anche risalendo fino a `src\TiaTracker.Worker.Vnn\bin\...`
(vedi Diagnostica). Per non toccare l'archivio vero: `TIATRACKER_DATA=<cartella di prova>`
e TiaTrackerOut della commessa di prova in una cartella di prova.

## Worker a mano

```
tiatracker-worker-v21.exe hello
tiatracker-worker-v21.exe instances
tiatracker-worker-v21.exe probe --pid 6232
tiatracker-worker-v21.exe snapshot --project <copia.ap21> --out <cartella> [--plc NOME] [--no-scl] [--no-hw] [--no-tags]
tiatracker-worker-v21.exe hardware (--pid N | --project <copia.ap21>) --out <cartella> [--no-tags] [--dump-attributes]
```

stdout = una riga JSON per messaggio, esattamente un `result` o `error` finale; stderr = log.
Uscite: 0 ok, 2 argomenti, 3 DLL, 4 accesso negato, 5 istanza, 6 apertura, 7 PLC,
8 worker non trovato e 9 occupato (solo dall'app), 10 PLC online, 11 TIA caduto,
12 parziale, 20 annullato, 21 timeout (anche prompt Openness).

Note misurate su un impianto reale: `Address.Length` di Openness e' in bit (DI16 = 16); hardware,
rete e 106 tabelle tag in circa 21 s dal TIA aperto; snapshot completo da cartella
(816 blocchi + hardware) in circa 100 s. TIA apre solo progetti in percorsi fino a 143
caratteri: la copia di lavoro sta in `work\` o, se la cartella dati e' lunga, in
`%TEMP%\TiaTracker`.

## Stato

- F0 worker trovato sempre + Diagnostica; F1 tema, notifiche, lavori; F2 stati delle
  versioni e carichi sui PLC dal tasto destro; F3 hardware/rete/tag da TIA, Lista IP
  allineata, import PRONETA; F4 documenti DOCX/PDF/CSV; F5 nuova commessa con export
  iniziale e scansione automatica; F6 pianificazione con Gantt; F7 dashboard e
  impostazioni. Ogni fase verificata con i test e guidando l'app via UI Automation su
  una copia dei dati di una commessa reale.

## Licenza

[MIT](LICENSE). Siemens, SIMATIC e TIA Portal sono marchi di Siemens AG: TiaTracker non e'
un prodotto Siemens e non e' affiliato a Siemens.
