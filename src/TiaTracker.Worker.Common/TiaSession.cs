using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Online;
using Siemens.Engineering.SW;
using TiaTracker.Contracts;
using TiaTracker.Worker.Protocol;

namespace TiaTracker.Worker
{
    /// <summary>Errore con codice di uscita gia' deciso.</summary>
    internal sealed class WorkerException : Exception
    {
        internal WorkerException(int exitCode, string code, string message)
            : base(message)
        {
            ExitCode = exitCode;
            Code = code;
        }

        internal int ExitCode { get; private set; }

        internal string Code { get; private set; }
    }

    /// <summary>Un PLC del progetto con il DeviceItem da cui leggere lo stato online.</summary>
    internal sealed class PlcTarget
    {
        internal PlcTarget(PlcSoftware software, DeviceItem item, string deviceName)
        {
            Software = software;
            Item = item;
            DeviceName = deviceName;
        }

        internal PlcSoftware Software { get; private set; }

        internal DeviceItem Item { get; private set; }

        internal string DeviceName { get; private set; }

        internal string Name
        {
            get { return Software.Name; }
        }

        /// <summary>Offline, Online, Connecting... o null se il servizio non c'e'.</summary>
        internal string OnlineState()
        {
            try
            {
                OnlineProvider provider = Item.GetService<OnlineProvider>();
                return provider == null ? null : provider.State.ToString();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Stato online non leggibile per " + Name + ": " + ex.Message);
                return null;
            }
        }

        internal bool IsOnline()
        {
            string state = OnlineState();
            return state != null && state.Equals("Online", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Aggancio a un'istanza di TIA aperta (attach) oppure apertura senza
    /// interfaccia di un progetto (sempre una copia: l'app non passa mai
    /// l'originale). Sola lettura: niente Save, Compile, GoOffline.
    /// </summary>
    internal sealed class TiaSession : IDisposable
    {
        private const int AccessPromptDelayMs = 3000;
        private const int AccessPromptTimeoutMs = 120000;

        private readonly Project _openedProject;

        private TiaSession(TiaPortal portal, Project project, Project openedProject, string source, int? pid)
        {
            Portal = portal;
            Project = project;
            _openedProject = openedProject;
            Source = source;
            Pid = pid;
        }

        internal TiaPortal Portal { get; private set; }

        internal Project Project { get; private set; }

        internal string Source { get; private set; }

        internal int? Pid { get; private set; }

        internal static TiaSession Attach(int pid)
        {
            TiaPortalProcess target = null;
            foreach (TiaPortalProcess p in TiaPortal.GetProcesses())
            {
                if (p.Id == pid)
                {
                    target = p;
                    break;
                }
            }

            if (target == null)
            {
                throw new WorkerException(ExitCodes.InstanceNotFound, "instance-not-found",
                    "Nessuna istanza di TIA Portal con PID " + pid + ".");
            }

            Out.Status(StatusCodes.Attaching, "Aggancio a TIA Portal (PID " + pid + ")");
            TiaPortal portal = WithAccessPrompt(() => target.Attach());
            Project project = null;
            foreach (Project p in portal.Projects)
            {
                project = p;
                break;
            }

            if (project == null)
            {
                portal.Dispose();
                throw new WorkerException(ExitCodes.InstanceNotFound, "no-project",
                    "TIA Portal (PID " + pid + ") non ha un progetto aperto.");
            }

            Out.Status(StatusCodes.Attached, "Agganciato a " + project.Name);
            return new TiaSession(portal, project, null, "attach", pid);
        }

        internal static TiaSession Open(string projectPath)
        {
            FileInfo file = new FileInfo(projectPath);
            if (!file.Exists)
            {
                throw new WorkerException(ExitCodes.InstanceNotFound, "project-not-found", "Progetto non trovato: " + file.FullName);
            }

            Out.Status(StatusCodes.Opening, "Avvio TIA Portal senza interfaccia");
            TiaPortal portal = WithAccessPrompt(() => new TiaPortal(TiaPortalMode.WithoutUserInterface));
            try
            {
                Project project;
                using (Heartbeat.Start(StatusCodes.Opening, "Apertura di " + file.Name))
                {
                    project = portal.Projects.Open(file);
                }

                Out.Status(StatusCodes.Opened, "Aperto " + project.Name);
                return new TiaSession(portal, project, project, "file", null);
            }
            catch (WorkerException)
            {
                portal.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                portal.Dispose();
                WorkerErrorClassifier.Classification c = WorkerErrorClassifier.Classify(ex.GetType().Name, ex.Message);
                int code = c.ExitCode == ExitCodes.Unexpected ? ExitCodes.OpenFailed : c.ExitCode;
                throw new WorkerException(code, code == ExitCodes.OpenFailed ? "open-failed" : c.Code,
                    "Apertura di " + file.Name + " fallita: " + ex.Message);
            }
        }

        /// <summary>
        /// Al primo uso di un exe nuovo TIA mostra "Openness access" e la
        /// chiamata (Attach o avvio headless) resta bloccata finche' l'utente
        /// non risponde: si avvisa l'app e dopo 120 s si esce con 21.
        /// </summary>
        private static T WithAccessPrompt<T>(Func<T> call)
        {
            Stopwatch clock = Stopwatch.StartNew();
            using (Timer prompt = new Timer(_ =>
                   {
                       if (clock.ElapsedMilliseconds >= AccessPromptTimeoutMs)
                       {
                           Out.Error(ExitCodes.Timeout, "access-prompt-timeout",
                               "Nessuna risposta alla richiesta di accesso Openness in TIA Portal entro 120 s.");
                           Environment.Exit(ExitCodes.Timeout);
                       }

                       Out.Status(StatusCodes.WaitingAccessPrompt,
                           "Confermare in TIA Portal la richiesta di accesso Openness (finestra \"Openness access\")");
                   }, null, AccessPromptDelayMs, 5000))
            {
                T result = call();
                prompt.Change(Timeout.Infinite, Timeout.Infinite);
                return result;
            }
        }

        internal bool? IsModified()
        {
            try
            {
                return Project.IsModified;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("IsModified non leggibile: " + ex.Message);
                return null;
            }
        }

        internal string ProjectPath()
        {
            try
            {
                return Project.Path == null ? null : Project.Path.FullName;
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal List<PlcTarget> Plcs()
        {
            List<PlcTarget> result = new List<PlcTarget>();
            foreach (Device device in Project.Devices)
            {
                CollectFromDevice(device, result);
            }

            foreach (DeviceUserGroup group in Project.DeviceGroups)
            {
                CollectFromGroup(group, result);
            }

            return result;
        }

        private static void CollectFromGroup(DeviceUserGroup group, List<PlcTarget> result)
        {
            foreach (Device device in group.Devices)
            {
                CollectFromDevice(device, result);
            }

            foreach (DeviceUserGroup child in group.Groups)
            {
                CollectFromGroup(child, result);
            }
        }

        private static void CollectFromDevice(Device device, List<PlcTarget> result)
        {
            foreach (DeviceItem item in device.DeviceItems)
            {
                CollectFromItem(device, item, result);
            }
        }

        private static void CollectFromItem(Device device, DeviceItem item, List<PlcTarget> result)
        {
            SoftwareContainer container = item.GetService<SoftwareContainer>();
            if (container != null)
            {
                PlcSoftware plc = container.Software as PlcSoftware;
                if (plc != null)
                {
                    result.Add(new PlcTarget(plc, item, device.Name));
                }
            }

            foreach (DeviceItem child in item.DeviceItems)
            {
                CollectFromItem(device, child, result);
            }
        }

        public void Dispose()
        {
            if (_openedProject != null)
            {
                Out.Status(StatusCodes.Closing, "Chiusura del progetto (senza salvare)");
                try
                {
                    _openedProject.Close();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Chiusura progetto fallita: " + ex.Message);
                }
            }

            if (Portal != null)
            {
                // In attach Dispose stacca soltanto: TIA resta aperto con il progetto.
                Portal.Dispose();
                Portal = null;
            }
        }
    }
}
