using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Siemens.Engineering;
using TiaTracker.Contracts;
using TiaTracker.Worker.Protocol;

namespace TiaTracker.Worker.Commands
{
    internal static class HelloCommand
    {
        /// <summary>Verifica che le DLL Openness si carichino, senza avviare ne' agganciare TIA.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int Run()
        {
            Assembly engineering = typeof(TiaPortal).Assembly;
            HelloResult result = new HelloResult
            {
                WorkerVersion = Program.Version,
                TiaMajor = Program.TiaMajor,
                ApiPath = OpennessResolver.ApiPath,
                EngineeringAssembly = engineering.GetName().Name + " " + engineering.GetName().Version,
                Framework = Environment.Version.ToString(),
            };
            return Out.Result("hello", result);
        }
    }

    internal static class InstancesCommand
    {
        /// <summary>PID e progetto di ogni TIA aperto. Nessun attach, quindi nessun prompt.</summary>
        internal static int Run()
        {
            InstancesResult result = new InstancesResult();
            foreach (TiaPortalProcess p in TiaPortal.GetProcesses())
            {
                string path = null;
                string acquired = null;
                try
                {
                    path = p.ProjectPath == null ? null : p.ProjectPath.FullName;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("ProjectPath del PID " + p.Id + ": " + ex.Message);
                }

                try
                {
                    acquired = p.AcquisitionTime.ToString("o", CultureInfo.InvariantCulture);
                }
                catch (Exception)
                {
                }

                result.Instances.Add(new InstanceInfo
                {
                    Pid = p.Id,
                    ProjectPath = path,
                    Mode = p.Mode.ToString(),
                    AcquisitionTime = acquired,
                });
            }

            return Out.Result("instances", result);
        }
    }

    internal static class ProbeCommand
    {
        internal static int Run(Args args)
        {
            using (TiaSession session = SessionFactory.Open(args))
            {
                ProbeResult result = new ProbeResult
                {
                    ProjectName = session.Project.Name,
                    ProjectPath = session.ProjectPath(),
                    IsModified = session.IsModified(),
                    Source = session.Source,
                };

                foreach (PlcTarget plc in session.Plcs())
                {
                    Cancel.ThrowIfRequested();
                    result.Plcs.Add(new PlcInfo
                    {
                        Name = plc.Name,
                        DeviceName = plc.DeviceName,
                        OnlineState = plc.OnlineState(),
                        BlockCount = BlockCollector.CountBlocks(plc.Software),
                        TypeCount = BlockCollector.CountTypes(plc.Software),
                    });
                }

                return Out.Result("probe", result);
            }
        }
    }

    internal static class SessionFactory
    {
        internal static TiaSession Open(Args args)
        {
            int? pid = args.Int("pid");
            string project = args.Get("project");
            if (pid.HasValue == (project != null))
            {
                throw new ArgumentException2("Serve uno e uno solo fra --pid e --project.");
            }

            return pid.HasValue ? TiaSession.Attach(pid.Value) : TiaSession.Open(project);
        }

        internal static List<PlcTarget> SelectPlcs(TiaSession session, string wanted)
        {
            List<PlcTarget> all = session.Plcs();
            if (all.Count == 0)
            {
                throw new WorkerException(ExitCodes.PlcNotFound, "no-plc", "Il progetto non contiene PLC.");
            }

            if (string.IsNullOrEmpty(wanted) || wanted == "*")
            {
                return all;
            }

            List<PlcTarget> chosen = all.FindAll(p => p.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase) ||
                                                      p.DeviceName.Equals(wanted, StringComparison.OrdinalIgnoreCase));
            if (chosen.Count == 0)
            {
                List<string> names = all.ConvertAll(p => p.Name);
                throw new WorkerException(ExitCodes.PlcNotFound, "plc-not-found",
                    "PLC '" + wanted + "' non trovato. Disponibili: " + string.Join(", ", names));
            }

            return chosen;
        }
    }
}
